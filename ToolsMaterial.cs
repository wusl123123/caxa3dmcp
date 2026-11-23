// 工具集：material 类 —— 对应 caxa3dmytoolsaddin 子插件 caxa3dsetMaterial。
//
// 通道取证（TLB 优先铁律，2027 TLB members.csv）：
//   IZPartProperty: get/set_MassDensity(7/8)、GetVolume(10)、GetMassProperties(11)、
//                   GetMaterialName(12)/SetMaterialName(13)、get_SpecifiedMass(14)、
//                   get_CalculatedMass(16)、get_MassType(19)
//   IZElement.GetMaterialNameProperty(36)；IZPart.get_SurfaceFinish(9)；IZSurfaceFinish get/set_Color(7/8)
// 实证口径（caxa3dsetMaterial/MaterialProbe.cs）：
//   ★ 密度必须在材料名之后写（命中材料库会带出库内密度）（:403）
//   ★ IZSurfaceFinish.Color 是 COLORREF 0xBBGGRR，不是 0xRRGGBB（:685-686，2026-10-28 三组对照实测）
//   ★ 判成功只看回读，不看 API 返回值（集合体第九节约定 15）
// 全部 RCW 直调（阶梯顶层），由桥封送回 CAXA 主线程(STA)。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    [ComVisible(false)]
    internal static class MatKit
    {
        /// <summary>COLORREF(0xBBGGRR) → "#RRGGBB"。</summary>
        public static string ColorRefToHex(int c)
        {
            int r = c & 0xFF, g = (c >> 8) & 0xFF, b = (c >> 16) & 0xFF;
            return "#" + r.ToString("X2", CultureInfo.InvariantCulture)
                       + g.ToString("X2", CultureInfo.InvariantCulture)
                       + b.ToString("X2", CultureInfo.InvariantCulture);
        }

        /// <summary>接受 "#RRGGBB" / "RRGGBB" / "0xBBGGRR" / 十进制 COLORREF / "r,g,b"。</summary>
        public static bool ParseColor(string s, out int cref, out string err)
        {
            cref = 0; err = null;
            if (string.IsNullOrEmpty(s)) { err = "color 不能为空"; return false; }
            string t = s.Trim();
            try
            {
                if (t.StartsWith("#")) t = t.Substring(1);
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    cref = Convert.ToInt32(t.Substring(2), 16); return true;
                }
                if (t.IndexOf(',') >= 0)
                {
                    string[] p = t.Split(',');
                    if (p.Length != 3) { err = "r,g,b 需要三段"; return false; }
                    int r = int.Parse(p[0], CultureInfo.InvariantCulture);
                    int g = int.Parse(p[1], CultureInfo.InvariantCulture);
                    int b = int.Parse(p[2], CultureInfo.InvariantCulture);
                    if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255) { err = "RGB 分量须在 0..255"; return false; }
                    cref = (b << 16) | (g << 8) | r; return true;
                }
                if (t.Length == 6)
                {
                    int r = Convert.ToInt32(t.Substring(0, 2), 16);
                    int g = Convert.ToInt32(t.Substring(2, 2), 16);
                    int b = Convert.ToInt32(t.Substring(4, 2), 16);
                    cref = (b << 16) | (g << 8) | r; return true;   // 用户给 RRGGBB，内部转 BBGGRR
                }
                if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out cref)) return true;
                err = "无法解析颜色：" + s; return false;
            }
            catch (Exception ex) { err = "颜色解析异常：" + ex.Message; return false; }
        }

        /// <summary>定位元素并切到 IZPart / IZPartProperty 表面；返回 null 表示成功。</summary>
        public static Dictionary<string, object> Locate(McpToolContext ctx, JValue args,
            out IZElement e, out IZPart part, out IZPartProperty pp)
        {
            e = null; part = null; pp = null;
            string err;
            e = Elem.Find(ctx, args, out err);
            if (e == null) return Fail(err);
            part = e as IZPart;
            pp = e as IZPartProperty;
            if (part == null && pp == null) return Fail("目标元素不支持 IZPart / IZPartProperty 表面（不是零件）");
            return null;
        }

        private static Dictionary<string, object> Fail(string reason)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = reason };
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetMaterialInfo : McpToolBase
    {
        public override string Name { get { return "get_material_info"; } }
        public override string Category { get { return "material"; } }
        public override string Description { get { return "读取零件的材料名、密度、体积、计算/指定质量、质量类型与表面颜色（COLORREF→#RRGGBB）。逐项第三态，读不到单列说明，不假装成功。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"), ["name"] = S.Str("元素名称（用于定位）"),
                ["last_created"] = S.Bool("取最后一个顶层元素", false, false, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZElement e; IZPart part; IZPartProperty pp;
            var bad = MatKit.Locate(ctx, args, out e, out part, out pp); if (bad != null) return bad;
            var d = new Dictionary<string, object> { ["ok"] = true, ["element"] = McpToolContext.SafeName(e) };
            try { string mn; e.GetMaterialNameProperty(out mn); d["materialName"] = mn; }
            catch (Exception ex) { d["materialNameError"] = ex.Message; }
            if (pp != null)
            {
                try { string mn; bool onlyThis; pp.GetMaterialName(out mn, out onlyThis); d["propertyMaterialName"] = mn; d["useByOnlyThisPart"] = onlyThis; }
                catch (Exception ex) { d["propertyMaterialNameError"] = ex.Message; }
                try { d["massDensity"] = pp.MassDensity; } catch (Exception ex) { d["massDensityError"] = ex.Message; }
                try { d["calculatedMass"] = pp.CalculatedMass; } catch (Exception ex) { d["calculatedMassError"] = ex.Message; }
                try { d["specifiedMass"] = pp.SpecifiedMass; } catch (Exception ex) { d["specifiedMassError"] = ex.Message; }
                try { d["massType"] = pp.MassType.ToString(); } catch (Exception ex) { d["massTypeError"] = ex.Message; }
                try { double achieved; double vol = pp.GetVolume(0.001, out achieved); d["volume"] = vol; d["volumeAchievedAccuracy"] = achieved; }
                catch (Exception ex) { d["volumeError"] = ex.Message; }
            }
            else d["propertySurface"] = "目标不支持 IZPartProperty 表面（第三态）";
            if (part != null)
            {
                try
                {
                    var sf = part.SurfaceFinish;
                    int c = (int)sf.Color;
                    d["color"] = MatKit.ColorRefToHex(c);
                    d["colorRef"] = c;
                }
                catch (Exception ex) { d["colorError"] = "SurfaceFinish.Color 读取失败（第三态）：" + ex.Message; }
            }
            return d;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolSetMaterial : McpToolBase
    {
        public override string Name { get { return "set_material"; } }
        public override string Category { get { return "material"; } }
        public override string Description { get { return "设置零件材料名（可选同时覆盖密度）。顺序铁律：先 SetMaterialName 再写 MassDensity；写后逐项回读校验，verified 才是真成功。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"), ["name"] = S.Str("元素名称（用于定位）"),
                ["last_created"] = S.Bool("取最后一个顶层元素", false, false, true),
                ["material_name"] = S.Str("材料名", true),
                ["density"] = S.Num("可选密度（CAXA 内部单位 kg/m³ 量级；不传则沿用材料库带出的值）"),
                ["only_this"] = S.Bool("是否仅本零件使用，默认 true", false, true, true)
            }, "material_name");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZElement e; IZPart part; IZPartProperty pp;
            var bad = MatKit.Locate(ctx, args, out e, out part, out pp); if (bad != null) return bad;
            if (pp == null) return Fail("material_surface_missing", "目标不支持 IZPartProperty 表面，无法写材料（第三态）");
            string mat = args.ParamString("material_name", "");
            if (string.IsNullOrEmpty(mat)) return Fail("material_name 不能为空");
            bool onlyThis = args.ParamBool("only_this", true);
            var attempts = new List<object>();
            try
            {
                pp.SetMaterialName(mat, onlyThis);
                attempts.Add(new Dictionary<string, object> { ["step"] = "SetMaterialName(13)", ["ok"] = true });
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                { ["ok"] = false, ["error"] = "SetMaterialName 失败：" + ex.Message, ["attempts"] = attempts };
            }
            bool hasDensity = args.Has("density");
            if (hasDensity)
            {
                double density = args.ParamDouble("density", 0.0);
                try { pp.MassDensity = density; attempts.Add(new Dictionary<string, object> { ["step"] = "set_MassDensity(8)", ["ok"] = true }); }
                catch (Exception ex) { attempts.Add(new Dictionary<string, object> { ["step"] = "set_MassDensity(8)", ["ok"] = false, ["error"] = ex.Message }); }
            }
            if (part != null) { try { part.Update(); attempts.Add(new Dictionary<string, object> { ["step"] = "IZPart.Update(7)", ["ok"] = true }); } catch (Exception ex) { attempts.Add(new Dictionary<string, object> { ["step"] = "Update", ["ok"] = false, ["error"] = ex.Message }); } }
            // 权威回读
            string rbName = null; bool rbOnly = false; bool nameOk = false; bool densOk = !hasDensity;
            try { pp.GetMaterialName(out rbName, out rbOnly); nameOk = string.Equals(rbName, mat, StringComparison.OrdinalIgnoreCase); }
            catch { }
            double rbDensity = 0.0;
            if (hasDensity)
            {
                try { rbDensity = pp.MassDensity; densOk = Math.Abs(rbDensity - args.ParamDouble("density", 0.0)) < 1e-6; } catch { }
            }
            return Ok("element", McpToolContext.SafeName(e), "materialName", mat,
                      "readBackName", rbName, "readBackOnlyThis", rbOnly,
                      "readBackDensity", hasDensity ? (object)rbDensity : null,
                      "verified", nameOk && densOk, "attempts", attempts);
        }
    }

    [ComVisible(false)]
    internal sealed class ToolSetColor : McpToolBase
    {
        public override string Name { get { return "set_color"; } }
        public override string Category { get { return "material"; } }
        public override string Description { get { return "设置零件表面颜色（IZSurfaceFinish.Color，COLORREF 0xBBGGRR）。支持 #RRGGBB / RRGGBB / r,g,b / 十进制；写后 Update 并回读校验。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"), ["name"] = S.Str("元素名称（用于定位）"),
                ["last_created"] = S.Bool("取最后一个顶层元素", false, false, true),
                ["color"] = S.Str("颜色：#RRGGBB / RRGGBB / r,g,b / 十进制 COLORREF", true)
            }, "color");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZElement e; IZPart part; IZPartProperty pp;
            var bad = MatKit.Locate(ctx, args, out e, out part, out pp); if (bad != null) return bad;
            if (part == null) return Fail("part_surface_missing", "目标不支持 IZPart 表面，无法取 SurfaceFinish（第三态）");
            int cref; string perr;
            if (!MatKit.ParseColor(args.ParamString("color", ""), out cref, out perr)) return Fail("bad_color", perr);
            try
            {
                var sf = part.SurfaceFinish;
                sf.Color = cref;
                part.Update();
                int back = (int)sf.Color;
                return Ok("element", McpToolContext.SafeName(e), "requested", MatKit.ColorRefToHex(cref),
                          "colorRef", cref, "readBackRef", back, "readBack", MatKit.ColorRefToHex(back),
                          "verified", back == cref);
            }
            catch (Exception ex) { return Fail("color_write_failed", "写颜色失败：" + ex.Message); }
        }
    }

    [ComVisible(false)]
    internal sealed class ToolListMaterialLibrary : McpToolBase
    {
        public override string Name { get { return "list_material_library"; } }
        public override string Category { get { return "material"; } }
        public override string Description { get { return "只读扫描 CAXA 安装目录下与材料库相关的 XML 文件（对应 caxa3dsetMaterial 的材料资料库扫描）。返回候选路径与大小，不写任何文件。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["max_files"] = S.Num("最多返回条数，默认 60", false, 60.0, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            int max = args.ParamInt("max_files", 60);
            if (max < 1) max = 1; if (max > 300) max = 300;
            var roots = new List<string>();
            try
            {
                string bin = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                roots.Add(bin);
                string parent = Path.GetDirectoryName(bin);
                if (!string.IsNullOrEmpty(parent)) roots.Add(parent);
            }
            catch (Exception ex) { return Fail("install_path_failed", "无法定位 CAXA 安装目录：" + ex.Message); }
            var found = new List<object>();
            var scanned = new List<string>();
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    scanned.Add(root);
                    foreach (string f in Directory.GetFiles(root, "*.xml", SearchOption.TopDirectoryOnly))
                    {
                        string n = Path.GetFileName(f).ToLowerInvariant();
                        if (n.Contains("material") || n.Contains("材料") || n.Contains("matlib") || n.Contains("mat_"))
                            found.Add(new Dictionary<string, object> { ["file"] = f, ["sizeBytes"] = new FileInfo(f).Length });
                        if (found.Count >= max) break;
                    }
                    if (found.Count < max)
                        foreach (string dir in Directory.GetDirectories(root))
                        {
                            string dn = Path.GetFileName(dir).ToLowerInvariant();
                            if (!(dn.Contains("material") || dn.Contains("材料") || dn.Contains("lib"))) continue;
                            try
                            {
                                foreach (string f in Directory.GetFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                                {
                                    found.Add(new Dictionary<string, object> { ["file"] = f, ["sizeBytes"] = new FileInfo(f).Length });
                                    if (found.Count >= max) break;
                                }
                            }
                            catch { }
                            if (found.Count >= max) break;
                        }
                }
                catch (Exception ex) { scanned.Add(root + "（扫描异常：" + ex.Message + "）"); }
                if (found.Count >= max) break;
            }
            return Ok("scannedRoots", scanned, "count", found.Count, "files", found,
                      "note", "只读扫描；未做 XML 内容解析，命中即候选，需调用方自行确认格式。");
        }
    }
}
