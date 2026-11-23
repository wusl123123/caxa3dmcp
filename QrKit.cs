// 「工程图附加二维码」本地算法层（2026-11-15 学习 caxa3dBatchMaster 后移植）。
//
// 本文件只做纯本地计算与磁盘产物，不碰 CAXA、不碰桥、不弹 UI：
//   ① QRCoder 生成二维码 PNG（磁盘文件 > 0 字节 = 唯一权威判据）；
//   ② 解析 GETSCALE 回包，把图纸比例归一为分母（兼容 2.5 与 0.4 两种存储形态）；
//   ③ 定位规则（1:1 图幅左上角基准）× 比例分母 → 插入点；
//   ④ 尺寸比例补偿：成品 mm × 分母 = 落图 mm，宿主比例因子 = 落图 ÷ PNG@96DPI。
//
// ★ 移植纪律（caxa3dBatchMaster 实机坑）：
//   · PNG 文件名必须带时间戳——固定名重跑时宿主「图像」框滞留旧路径、插图命令 pending=1 挂死；
//   · 静区取 1 模块、8 px/模块、显式 SetResolution(96,96)，比例换算才有确定基准；
//   · 规则文件走本项目纪律：DLL 同目录（*.txt 随编译复制 + deploy.ps1 上拷到 CAXA bin）；
//     找不到就用内置 SeedRules 播种，绝不静默用错坐标；
//   · 全部计算按 CultureInfo.InvariantCulture，避免中文系统小数点逗号。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace caxa3dmcp
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal static class QrKit
    {
        public const string RuleFileName = "caxa3dmcp二维码定位规则.txt";
        public const double PxPerModule = 8.0;
        public const double Dpi = 96.0;
        public const double MmPerInch = 25.4;
        public const int QuietModules = 1;

        public const string SeedRules =
            "#下面是1:1时候二维码定位坐标（caxa3dmcp 工程图附加二维码）\r\n" +
            "A4竖-----(-105,148.5)\r\nA4横-----(-148.5,105)\r\n" +
            "A3横-----(-210,148.5)\r\nA2横-----(-297,210)\r\n" +
            "A1横-----(-420.5,297)\r\nA0横-----(-594.5,420.5)\r\n";

        // ---------------------------------------------------------------- ① 二维码 PNG

        /// <summary>QRCoder(ECC L) → 1 模块静区 → PNG。唯一判据 = 磁盘文件存在且 > 0 字节。</summary>
        public static bool BuildQr(string payload, string tag, Action<string> log,
            out string pngPath, out int side, out int pngPx)
        {
            pngPath = ""; side = 0; pngPx = 0;
            if (string.IsNullOrEmpty(payload))
            {
                if (log != null) log("qr: 内容为空，拒绝制作（第三态）");
                return false;
            }
            try
            {
                bool[,] core;
                using (var gen = new QRCoder.QRCodeGenerator())
                {
                    var data = gen.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.L);
                    var mm = data.ModuleMatrix;
                    int w = mm.Count, h = mm[0].Count;
                    core = new bool[w, h];
                    for (int r = 0; r < w; r++)
                        for (int c = 0; c < h; c++) core[r, c] = mm[r][c];
                }

                int n = core.GetLength(0) + QuietModules * 2;
                side = n;
                bool[,] m = new bool[n, n];
                for (int r = 0; r < core.GetLength(0); r++)
                    for (int c = 0; c < core.GetLength(1); c++)
                        m[r + QuietModules, c + QuietModules] = core[r, c];

                int px = (int)(n * PxPerModule);
                pngPx = px;
                using (var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb))
                {
                    bmp.SetResolution((float)Dpi, (float)Dpi);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        using (var brush = new SolidBrush(Color.Black))
                            for (int r = 0; r < n; r++)
                                for (int c = 0; c < n; c++)
                                    if (m[r, c]) g.FillRectangle(brush, (int)(c * PxPerModule), (int)(r * PxPerModule),
                                                                 (int)PxPerModule, (int)PxPerModule);
                    }
                    string safe = string.IsNullOrEmpty(tag) ? "qr" : tag;
                    foreach (char ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
                    // 实机坑：固定文件名重跑 ⇒ 宿主「图像」框滞留旧路径、命令 pending=1 挂死。
                    pngPath = Path.Combine(Path.GetTempPath(), "caxa3dmcp_qr_" + safe + "_" +
                                           DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                    bmp.Save(pngPath, ImageFormat.Png);
                }
                var fi = new FileInfo(pngPath);
                bool ok = fi.Exists && fi.Length > 0;
                if (log != null) log("qr: PNG " + (ok ? fi.Length + " B verified" : "落盘判据失败（第三态）") +
                                     "；矩阵 " + n + "x" + n + "；像素 " + px + "x" + px);
                return ok;
            }
            catch (Exception ex)
            {
                if (log != null) log("qr: 二维码制作 EX " + ex.Message);
                pngPath = ""; side = 0; pngPx = 0;
                return false;
            }
        }

        // ---------------------------------------------------------------- ② GETSCALE 回包解析

        public static Dictionary<string, string> ParseFields(string message)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(message)) return d;
            // batchMaster 的 GETSCALE 用 | 分隔；本项目桥统一 | 分隔，另兼容空格
            foreach (string part in message.Split(new char[] { '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                d[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            return d;
        }

        public static bool TryDouble(Dictionary<string, string> f, string key, out double v)
        {
            v = 0;
            string s;
            return f.TryGetValue(key, out s) &&
                   double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>比例归一：CAXA 存 2.5（=1:2.5）或 0.4（倒数），一律折算成分母 ≥1；读不到 = false 第三态。</summary>
        public static bool TryNormalizeDenom(Dictionary<string, string> f, out double denom, out double raw)
        {
            denom = 1.0; raw = 0;
            if (!TryDouble(f, "scale", out raw) || raw <= 1e-9) return false;
            denom = raw >= 1.0 - 1e-9 ? raw : 1.0 / raw;
            return denom >= 1e-9;
        }

        // ---------------------------------------------------------------- ③ 定位规则

        public sealed class RuleEntry
        {
            public string Name;
            public double X, Y;
            public double ExpectW, ExpectH;
        }

        private static readonly string[][] IsoSizes = {
            new[] { "A4", "210", "297" }, new[] { "A3", "297", "420" },
            new[] { "A2", "420", "594" }, new[] { "A1", "594", "841" },
            new[] { "A0", "841", "1189" }
        };

        private static readonly Regex LineRx = new Regex(
            @"^\s*(?<name>[^#=\r\n]+?)\s*[-—]{2,}\s*[(（]\s*(?<x>-?\d+(?:\.\d+)?)\s*[,，]\s*(?<y>-?\d+(?:\.\d+)?)\s*[)）]\s*$",
            RegexOptions.Compiled);

        /// <summary>规则文件路径：DLL 同目录优先（随包 *.txt），不存在则原地播种内置规则。</summary>
        public static string ResolveRulePath(out string note)
        {
            note = "";
            try
            {
                string dir = Path.GetDirectoryName(typeof(QrKit).Assembly.Location) ?? "";
                string p = Path.Combine(dir, RuleFileName);
                if (File.Exists(p)) { note = "DLL 同目录规则文件"; return p; }
                File.WriteAllText(p, SeedRules, new UTF8Encoding(true));
                note = "规则文件不存在，已按内置默认规则播种：" + p;
                return p;
            }
            catch (Exception ex) { note = "规则文件路径不可用：" + ex.Message; return ""; }
        }

        public static List<RuleEntry> LoadRules(out string path, out string err)
        {
            path = ResolveRulePath(out err);
            var list = new List<RuleEntry>();
            if (string.IsNullOrEmpty(path)) { err = "找不到规则文件（" + RuleFileName + "）"; return null; }
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) { err = "规则文件读取失败：" + ex.Message; return null; }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                Match m = LineRx.Match(line);
                if (!m.Success) { err += "第 " + (i + 1) + " 行格式无法解析（已跳过）；"; continue; }
                var e = new RuleEntry
                {
                    Name = m.Groups["name"].Value.Trim(),
                    X = double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
                    Y = double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture)
                };
                FillExpect(e);
                if (e.ExpectW <= 0) { err += "第 " + (i + 1) + " 行认不出图幅规格（已跳过）；"; continue; }
                list.Add(e);
            }
            if (list.Count == 0) { err = string.IsNullOrEmpty(err) ? "规则文件没有可解析的条目" : err; return null; }
            return list;
        }

        private static void FillExpect(RuleEntry e)
        {
            string name = e.Name ?? "";
            string fmt = null;
            foreach (var iso in IsoSizes)
                if (name.IndexOf(iso[0], StringComparison.OrdinalIgnoreCase) >= 0) { fmt = iso[0]; break; }
            if (fmt == null) return;
            double s = 0, l = 0;
            foreach (var iso in IsoSizes)
                if (iso[0] == fmt)
                {
                    s = double.Parse(iso[1], CultureInfo.InvariantCulture);
                    l = double.Parse(iso[2], CultureInfo.InvariantCulture);
                    break;
                }
            bool portrait = name.IndexOf("竖", StringComparison.Ordinal) >= 0;
            e.ExpectW = portrait ? s : l;
            e.ExpectH = portrait ? l : s;
        }

        public static bool TryMatch(List<RuleEntry> rules, double paperW, double paperH,
            out RuleEntry hit, out string why)
        {
            hit = null; why = "";
            if (rules == null || rules.Count == 0) { why = "规则列表为空"; return false; }
            if (paperW <= 1 || paperH <= 1) { why = "实机图幅宽高无效或未提供"; return false; }
            foreach (var e in rules)
                if (Math.Abs(e.ExpectW - paperW) <= 2 && Math.Abs(e.ExpectH - paperH) <= 2)
                {
                    hit = e;
                    why = "命中「" + e.Name + "」（图幅 " + Fmt(paperW) + "x" + Fmt(paperH) + " mm）";
                    return true;
                }
            why = "图幅 " + Fmt(paperW) + "x" + Fmt(paperH) + " mm 不在规则表中（现有 " + rules.Count + " 条）";
            return false;
        }

        // ---------------------------------------------------------------- ④ 比例补偿编排（纯计算）

        /// <summary>把「成品尺寸 + 比例分母 + 图幅」换算成宿主插图命令需要的插入点与比例因子。</summary>
        public static Dictionary<string, object> Plan(double sizeMm, double denom,
            double paperW, double paperH, double fallbackX, double fallbackY, int pngPx)
        {
            var d = new Dictionary<string, object>();
            if (denom <= 1e-9) denom = 1.0;
            string rulePath, ruleErr, hitName = "";
            string ruleWhy = "";
            double ix = fallbackX, iy = fallbackY;
            var rules = LoadRules(out rulePath, out ruleErr);
            RuleEntry hit = null;
            bool matched = rules != null && TryMatch(rules, paperW, paperH, out hit, out ruleWhy);
            if (matched)
            {
                hitName = hit.Name;
                ix = hit.X * denom;
                iy = hit.Y * denom;
            }
            else if (rules == null) ruleWhy = "规则不可用：" + ruleErr;
            else ruleWhy += "；插入点回落兜底值";

            double pngMm = pngPx > 0 ? pngPx / Dpi * MmPerInch : 0.0;
            double insertMm = sizeMm * denom;
            double imgScale = pngMm > 1e-6 ? insertMm / pngMm : 0.0;

            d["rulePath"] = rulePath;
            d["ruleError"] = ruleErr;
            d["ruleWhy"] = ruleWhy;
            d["ruleMatched"] = matched;
            d["ruleHit"] = hitName;
            d["denom"] = denom;
            d["insertX"] = ix;
            d["insertY"] = iy;
            d["insertPoint"] = Fmt(ix) + "," + Fmt(iy);
            d["sizeMm"] = sizeMm;
            d["insertMm"] = insertMm;
            d["pngPx"] = pngPx;
            d["pngMm"] = pngMm;
            d["imgScale"] = imgScale;
            d["verified"] = matched && imgScale > 1e-9;
            d["note"] = "verified 只表示计算链完整（规则命中 + 比例因子有效），不代表图纸已插图；插图成败只认 draft_qr_state 的 delta>0";
            return d;
        }

        public static string Fmt(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
