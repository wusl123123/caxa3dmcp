// 工具集：基础建模 —— 参考 C:\code\ICAPI\caxa3dtracker 的「基体模型建立」功能。
//
// ★ 借鉴映射（tracker → caxa3dmcp）★
//   tracker 的 CubeForm 三行图素（长方体 / 圆柱体 / 圆管）与 CaxaAutomation.TryCreatePrimitivePart
//   的「共用链」：CreatePart → 图素命令 → Update → 首特征命名 → IZSizeBox 四勾选 → Reset →
//   GetPartType 回读。本文件把这条链完整复刻为 MCP 工具，并补齐 TLB 里另外两种基础图素。
//
// ★ 通道差异（重要）★
//   tracker 走 vtable 槽位直调（ProbeCommon.Fn<T>）；本项目实证 RCW 直调可用（2026-11-12 实机：
//   CreatePart(9)+IZPartFeatureMgr.CreateBlock(19)+Update(7) 成功生成 10×10×10 方块），且
//   interop.ICApiIronCAD.dll 字节扫描确认 IZSizeBox / SetDisplayHandle / GetDisplayHandle /
//   GetPartType / OperationType / Z_SUBTRACT / CreateCone / CreateTorus 全部暴露 →
//   全部走通道阶梯顶层的 RCW 直调，不下沉 vtable，不引入裸指针与引用计数管理。
//
// ★ 实证否决（tracker 项目总结 11-08）★
//   IZPartFeatureMgr.CreateShellFeature(12) 抽壳圆管路线实机 hr=0x80004005（识别链全对仍 E_FAIL），
//   已技术否决。圆管改走 tracker「保留为备用库函数」的除料路线：内圆柱 + IZFeature.OperationType
//   = eZOperationType.Z_SUBTRACT + Update，并有 caxa3dminitool 增料/除料实机先例。
//   UI 里 16:05 删除「圆管」按钮只是交互收敛，不是技术否决。
//
// ★ 单位与坐标口径（tracker 15:19 裁定）★
//   CAXA 内部单位为米；工具入参默认 mm（×0.001）。长方体底面矩形中心与原点重合，高沿 +Z；
//   圆柱/圆锥/圆环轴点=(0,0,0)、轴向=(0,0,1) 单位向量（不是高度）。
//
// ★ 失败清理铁律 ★ 图素特征长在本次自建零件里，失败只 Remove 本次自建零件，绝不碰用户已有元素。
// ★ STA 铁律 ★ 所有 ICAPI 调用由 McpBridge 经隐藏窗体 Control.Invoke 封送回 CAXA 主线程执行。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    /// <summary>
    /// 基础建模共用壳（对应 tracker 的 TryCreatePrimitivePart 共用链）。
    /// 把「建模 → 命名 → 包围盒显示 → 重置 → 种类回读 → 包围盒回读」封装给所有基础建模工具复用。
    /// </summary>
    [ComVisible(false)]
    internal static class PrimitiveKit
    {
        public const double MmToMeter = 0.001;

        /// <summary>统一建模结果。</summary>
        internal sealed class Built
        {
            public IZPart Part;
            public object Feature;      // 首特征（ZExtrudeFeature / ZSpinFeature）
            public IZPartFeatureMgr Mgr;
            public string Variant;
            public bool Ok { get { return Part != null; } }
        }

        public static double ToMeter(JValue args, string key, double dfltMm)
        {
            return PrimitiveFactory.ToMeter(args, key, dfltMm);
        }

        public static Dictionary<string, object> NeedScene(McpToolContext ctx)
        {
            return ctx.Scene == null
                ? new Dictionary<string, object> { ["ok"] = false, ["error"] = "当前没有 3D 场景文档（活动文档不是场景文档或没有打开文档）" }
                : null;
        }

        /// <summary>
        /// 唯一实证可用通道：CreatePart(9) → as IZPartFeatureMgr → 图素命令 → Update(7)。
        /// 场景级 IZSceneDoc.Create*Part(13~17) 在 CAXA 2027 运行时一律 E_NOTIMPL，故不纳入。
        /// 点数组按 double[3] → float[3] → object[3] 阶梯尝试，每步成败写入 attempts。
        /// </summary>
        public static Built Build(McpToolContext ctx,
            Func<IZPartFeatureMgr, object[], object> createFeature,
            List<object[]> variants, string label, List<object> attempts)
        {
            foreach (object[] v in variants)
            {
                IZPart p = null;
                try
                {
                    p = ctx.Scene.CreatePart();
                    if (p == null) throw new InvalidOperationException("CreatePart 返回 null");
                    IZPartFeatureMgr fm = p as IZPartFeatureMgr;
                    if (fm == null) throw new InvalidOperationException("零件不支持 IZPartFeatureMgr 表面（RCW as 失败）");
                    object feat = createFeature(fm, v);
                    if (feat == null) throw new InvalidOperationException(label + " 返回 null");
                    p.Update();
                    string variant = PrimitiveFactory.VariantName(v[0]);
                    attempts.Add(new Dictionary<string, object> { ["channel"] = label, ["variant"] = variant, ["ok"] = true });
                    return new Built { Part = p, Feature = feat, Mgr = fm, Variant = variant };
                }
                catch (Exception ex)
                {
                    attempts.Add(new Dictionary<string, object>
                    {
                        ["channel"] = label,
                        ["variant"] = PrimitiveFactory.VariantName(v[0]),
                        ["ok"] = false,
                        ["error"] = ex.Message
                    });
                    ctx.Log(label + "(" + PrimitiveFactory.VariantName(v[0]) + ") FAIL " + ex.Message);
                    if (p != null) { string err; McpToolContext.TryRemove(p, out err); }
                }
            }
            return new Built();
        }

        /// <summary>首特征按建模顺序编号命名（用户 11-14 裁定：N-名称，如 1-长方体）。写后回读是唯一判据。</summary>
        public static void NameFeature(McpToolContext ctx, object feat, string label, Dictionary<string, object> result)
        {
            if (feat == null) { result["featureSeqNameVerified"] = false; result["featureSeqNameError"] = "特征对象为空（第三态）"; return; }
            int seq = BuildSeq.Next(ctx);
            var d = BuildSeq.Apply(ctx, feat, string.IsNullOrEmpty(label) ? "图素" : label, seq);
            BuildSeq.Merge(result, "feature", d);
            if (d.ContainsKey("seqName")) result["featureName"] = d["seqName"];              // 兼容旧字段名
            if (d.ContainsKey("seqNameError")) result["featureNameError"] = d["seqNameError"];
        }

        /// <summary>
        /// 权威判据：IZPart.GetPartType(51) 回读零件种类掩码（tracker 共用链第 4 步）。
        /// 0x10=创新零件、0x20=工程零件（2027 enums.csv）。读不到记第三态，绝不折算成功。
        /// </summary>
        public static void ReadPartType(IZPart part, Dictionary<string, object> result)
        {
            try
            {
                eZPartType pt;
                part.GetPartType(out pt);
                int mask = Convert.ToInt32(pt);
                const int Innovation = 0x10, Structured = 0x20;
                bool innovation = (mask & Innovation) != 0;
                bool structured = (mask & Structured) != 0;
                string kind = structured && !innovation ? "工程零件"
                            : innovation && !structured ? "创新零件"
                            : structured && innovation ? "工程+创新（两位都置）"
                            : "未判定（无工程/创新位）";
                result["partTypeMask"] = mask;
                result["partType"] = kind;
            }
            catch (Exception ex) { result["partTypeReadBack"] = "GetPartType 读取失败（第三态）：" + ex.Message; }
        }

        /// <summary>
        /// 在特征/零件元素树上找 IZSizeBox 表面（tracker FindSizeBox 口径：深度≤3、每层≤16 节点）。
        /// RCW 的 as 等价 QI，无需裸指针与 Marshal.Release。
        /// </summary>
        public static IZSizeBox FindSizeBox(object node, int depth)
        {
            if (node == null || depth > 3) return null;
            IZSizeBox sb = node as IZSizeBox;
            if (sb != null) return sb;
            IZElement el = node as IZElement;
            if (el == null) return null;
            List<object> kids;
            try { kids = McpToolContext.UnpackArray(el.GetChildren()); }
            catch { return null; }
            int n = 0;
            foreach (object k in kids)
            {
                if (k == null) continue;
                if (++n > 16) break;
                IZSizeBox found = FindSizeBox(k, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        public static IZSizeBox FindSizeBox(object feat, IZPart part)
        {
            IZSizeBox sb = FindSizeBox(feat, 0);
            return sb != null ? sb : FindSizeBox(part, 0);
        }

        /// <summary>只读回读 sizebox 驱动尺寸（L/W/H，mm），不产生任何副作用。</summary>
        public static void ReadSizeBox(IZSizeBox sb, Dictionary<string, object> result)
        {
            try
            {
                double l = sb.Length / MmToMeter;
                double w = sb.Width / MmToMeter;
                double h = sb.Height / MmToMeter;
                result["sizeboxLengthMm"] = l;
                result["sizeboxWidthMm"] = w;
                result["sizeboxHeightMm"] = h;
                // 实机 2026-11-12 CAXA 2027(29.0)：未经 Reset 的 IZSizeBox 三个尺寸都是
                // 1e-6 m 占位值（换算后 0.001 mm），并非真实几何尺寸；执行 set_sizebox_handles
                // （四勾选 + Reset(false) + Update）后才回读真实 20/20/30 mm。此处显式标注
                // 第三态，避免 MCP 客户端把占位值误当成模型尺寸，也避免「假装成功」。
                if (l > 0 && l <= 0.01 && w > 0 && w <= 0.01 && h > 0 && h <= 0.01)
                {
                    result["sizeboxInitialized"] = false;
                    result["sizeboxNote"] = "占位值：IZSizeBox 尚未 Reset，L/W/H 非真实几何尺寸；请先调用 set_sizebox_handles(reset=true)，或改用 get_bounding_box 读取真实包围盒。";
                }
                else result["sizeboxInitialized"] = true;
            }
            catch (Exception ex) { result["sizeboxError"] = "尺寸回读失败（第三态）：" + ex.Message; }
        }

        /// <summary>
        /// tracker 共用链第 3.5/3.6 步：SetDisplayHandle 四勾选 → GetDisplayHandle 回读 →
        /// PermitResetSizeBox 预检 → Reset(false) → IZPart.Update → L/W/H 回读。
        /// 全部为「尽力而为」：找不到 IZSizeBox 或任一步失败只记第三态，不影响建模成功判定。
        /// </summary>
        public static void ConfigureSizeBox(McpToolContext ctx, IZSizeBox sb, IZPart part, Dictionary<string, object> result)
        {
            if (sb == null) { result["sizebox"] = "特征/零件元素树上未找到 IZSizeBox（第三态，未设置）"; return; }
            try
            {
                sb.SetDisplayHandle(true, true, true, true);
                bool l, w, h, b;
                sb.GetDisplayHandle(out l, out w, out h, out b);
                result["sizeboxHandles"] = new List<object> { l, w, h, b };
                result["sizeboxHandlesAllOn"] = l && w && h && b;
            }
            catch (Exception ex) { result["sizeboxHandleError"] = "Set/GetDisplayHandle 失败（第三态）：" + ex.Message; }

            try
            {
                if (sb.PermitResetSizeBox)
                {
                    sb.Reset(false);
                    if (part != null) part.Update();
                    result["sizeboxReset"] = true;
                }
                else result["sizeboxReset"] = "PermitResetSizeBox=false（跳过重置，第三态）";
            }
            catch (Exception ex) { result["sizeboxReset"] = "Reset 失败（第三态）：" + ex.Message; }

            ReadSizeBox(sb, result);
        }

        /// <summary>
        /// 给既有建模工具追加 tracker 权威判据，零副作用：只回读 GetPartType 与 sizebox 尺寸，
        /// 不写 Name、不勾显示柄、不 Reset，避免改变上一轮已实机验证的 create_box/create_cylinder 行为。
        /// </summary>
        public static void EnrichPart(McpToolContext ctx, IZPart part, Dictionary<string, object> result)
        {
            if (part == null) return;
            ReadPartType(part, result);
            IZSizeBox sb = FindSizeBox(part, 0);
            if (sb == null) { result["sizebox"] = "未找到 IZSizeBox（第三态）"; return; }
            ReadSizeBox(sb, result);
        }

        /// <summary>顶层元素里找零件：name 为空取第一个 IZPart，否则按名称（忽略大小写）匹配。</summary>
        public static IZPart FindPart(McpToolContext ctx, string name)
        {
            foreach (object o in McpToolContext.UnpackArray(ctx.Scene.GetChildElements()))
            {
                IZPart p = o as IZPart;
                if (p == null) continue;
                if (string.IsNullOrEmpty(name)) return p;
                if (string.Equals(McpToolContext.SafeNameOf(p), name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        public static Dictionary<string, object> Finish(McpToolContext ctx, Built b, string shape,
            string name, List<object> attempts, Dictionary<string, object> dims, bool configureSizeBox)
        {
            if (!b.Ok)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = shape + "：CreatePart + 图素命令 + Update 全部点形态失败，请看 attempts 判定断在哪条轴。",
                    ["attempts"] = attempts
                };

            var result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["channel"] = "CreatePart(9)+IZPartFeatureMgr+" + shape + "+Update(7)",
                ["pointVariant"] = b.Variant,
                ["partName"] = McpToolContext.SafeNameOf(b.Part),
                ["attempts"] = attempts
            };
            foreach (var kv in dims) result[kv.Key] = kv.Value;

            if (!string.IsNullOrEmpty(name))
            {
                string err;
                if (McpToolContext.TrySetName(b.Part, name, out err)) result["partName"] = name;
                else result["partNameError"] = err;
            }
            NameFeature(ctx, b.Feature, shape, result);   // 首特征 = N-形状名（建模顺序号）
            ReadPartType(b.Part, result);
            if (configureSizeBox) ConfigureSizeBox(ctx, FindSizeBox(b.Feature, b.Part), b.Part, result);
            PrimitiveFactory.ReadBackBox(ctx, b.Part, result);
            ctx.Log(shape + " OK variant=" + b.Variant + " partType=" + (result.ContainsKey("partType") ? result["partType"] : "第三态"));
            return result;
        }
    }

    // ============================================================ 圆管（tracker 除料路线）

    [ComVisible(false)]
    internal sealed class ToolCreateTube : McpToolBase
    {
        public override string Name { get { return "create_tube"; } }
        public override string Category { get { return "model"; } }
        public override string Description
        {
            get
            {
                return "生成圆管：外圆柱 + 内孔圆柱转除料(Z_SUBTRACT) + Update，默认外径 20 / 壁厚 2 / 高 30 mm，轴沿 +Z 过原点。"
                     + "对应 caxa3dtracker 基体模型建立第三行；抽壳路线 CreateShellFeature(12) 实机 E_FAIL 已否决，本工具走除料路线。";
            }
        }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["outer_diameter"] = S.Num("外径，默认 20", false, 20.0, true),
                ["wall_thickness"] = S.Num("壁厚，默认 2（必须 < 外径/2，否则内孔消失）", false, 2.0, true),
                ["height"] = S.Num("高度，默认 30", false, 30.0, true),
                ["unit"] = S.Str("单位：mm（默认）或 m", false, "mm"),
                ["name"] = S.Str("可选：给新建零件命名"),
                ["sizebox"] = S.Bool("是否执行包围盒四勾选 + Reset 共用链，默认 true", false, true, true)
            });
        }

        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = PrimitiveKit.NeedScene(ctx); if (bad != null) return bad;
            double dia = PrimitiveKit.ToMeter(args, "outer_diameter", 20.0);
            double wall = PrimitiveKit.ToMeter(args, "wall_thickness", 2.0);
            double h = PrimitiveKit.ToMeter(args, "height", 30.0);
            if (dia <= 0 || h <= 0) return Fail("outer_diameter / height 必须为正数");
            if (wall <= 0 || wall >= dia / 2.0)
                return Fail("invalid_wall", "壁厚必须满足 0 < 壁厚 < 外径/2（否则内孔消失无法成管）");
            string name = args.ParamString("name", "");
            bool doSizeBox = args.ParamBool("sizebox", true);
            var attempts = new List<object>();
            List<object[]> axis = PrimitiveFactory.PointVariants(0, 0, 0, 0, 0, 1);

            // 1) 外圆柱
            PrimitiveKit.Built b = PrimitiveKit.Build(ctx,
                (fm, v) => fm.CreateCylinder(dia / 2.0, h, v[0], v[1]),
                axis, "CreatePart+IZPartFeatureMgr.CreateCylinder(20)+Update(7)", attempts);
            if (!b.Ok)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "圆管外圆柱建立失败", ["attempts"] = attempts };

            // 2) 内孔圆柱：r = 外径/2 − 壁厚，同轴同高；再转除料并回读校验
            double rInner = dia / 2.0 - wall;
            object innerFeat = null;
            string subtractErr = null;
            try
            {
                innerFeat = b.Mgr.CreateCylinder(rInner, h, axis[0][0], axis[0][1]);
                if (innerFeat == null) throw new InvalidOperationException("内孔 CreateCylinder 返回 null");
                IZFeature f = innerFeat as IZFeature;
                if (f == null) throw new InvalidOperationException("内孔特征不支持 IZFeature 表面（RCW as 失败）");
                eZOperationType before = f.OperationType;
                f.OperationType = eZOperationType.Z_SUBTRACT;
                eZOperationType wrote = f.OperationType;
                if (wrote != eZOperationType.Z_SUBTRACT)
                    throw new InvalidOperationException("OperationType 写后回读不一致：" + wrote);
                b.Part.Update();
                attempts.Add(new Dictionary<string, object>
                {
                    ["channel"] = "内孔除料 CreateCylinder(20)+OperationType=Z_SUBTRACT+Update",
                    ["variant"] = PrimitiveFactory.VariantName(axis[0][0]),
                    ["ok"] = true,
                    ["operationTypeBefore"] = before.ToString(),
                    ["operationTypeAfter"] = wrote.ToString()
                });
            }
            catch (Exception ex)
            {
                subtractErr = ex.Message;
                attempts.Add(new Dictionary<string, object>
                {
                    ["channel"] = "内孔除料 CreateCylinder(20)+OperationType=Z_SUBTRACT+Update",
                    ["variant"] = PrimitiveFactory.VariantName(axis[0][0]),
                    ["ok"] = false,
                    ["error"] = ex.Message
                });
                ctx.Log("圆管内孔除料 FAIL " + ex.Message);
                string err; McpToolContext.TryRemove(b.Part, out err);   // 只清理本次自建零件
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["code"] = "tube_subtract_failed",
                    ["error"] = "圆管内孔除料失败：" + subtractErr + "（本次自建零件已清理）",
                    ["attempts"] = attempts
                };
            }

            var dims = new Dictionary<string, object>
            {
                ["outerDiameterMm"] = dia / PrimitiveKit.MmToMeter,
                ["wallThicknessMm"] = wall / PrimitiveKit.MmToMeter,
                ["innerDiameterMm"] = (2.0 * rInner) / PrimitiveKit.MmToMeter,
                ["heightMm"] = h / PrimitiveKit.MmToMeter,
                ["subtractReadBack"] = eZOperationType.Z_SUBTRACT.ToString()
            };
            var result = PrimitiveKit.Finish(ctx, b, "圆管", name, attempts, dims, doSizeBox);
            // 内孔除料是同一零件内的第二个特征，也按建模顺序编号（便于出错后按号重排）
            if (innerFeat != null)
            {
                var nd = new Dictionary<string, object>();
                BuildSeq.ApplyNext(ctx, innerFeat, "圆管内孔除料", nd, "innerFeature");
                foreach (var kv in nd) result[kv.Key] = kv.Value;
            }
            // 除料不改变外形包围盒，孔径是否真成实体孔需视觉确认（不假装成功）
            result["note"] = "判据为 OperationType 回读=Z_SUBTRACT + Update + 包围盒回读；内孔是否贯通请用 get_view 视觉确认。";
            return result;
        }
    }

    // ============================================================ 圆锥 / 圆环（TLB 2027 基础图素）

    [ComVisible(false)]
    internal sealed class ToolCreateCone : McpToolBase
    {
        public override string Name { get { return "create_cone"; } }
        public override string Category { get { return "model"; } }
        public override string Description
        {
            get { return "生成圆锥（IZPartFeatureMgr.CreateCylinder 同族 CreateCone(21)）：底面半径/高/半角，轴沿 +Z 过原点。默认半径 10 mm、高 20 mm、半角 20°。"; }
        }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["radius"] = S.Num("底面半径，默认 10", false, 10.0, true),
                ["height"] = S.Num("高度，默认 20", false, 20.0, true),
                ["semi_angle_deg"] = S.Num("半角（度），默认 20，取值 0<angle<90", false, 20.0, true),
                ["unit"] = S.Str("单位：mm（默认）或 m", false, "mm"),
                ["name"] = S.Str("可选：给新建零件命名"),
                ["sizebox"] = S.Bool("是否执行包围盒四勾选 + Reset 共用链，默认 true", false, true, true)
            });
        }

        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = PrimitiveKit.NeedScene(ctx); if (bad != null) return bad;
            double r = PrimitiveKit.ToMeter(args, "radius", 10.0);
            double h = PrimitiveKit.ToMeter(args, "height", 20.0);
            double deg = args.ParamDouble("semi_angle_deg", 20.0);
            if (r <= 0 || h <= 0) return Fail("radius / height 必须为正数");
            if (deg <= 0 || deg >= 90) return Fail("semi_angle_deg 必须在 0~90 度之间");
            double semiAngle = deg * Math.PI / 180.0;   // TLB: Double dSemiAngle（弧度口径）
            string name = args.ParamString("name", "");
            var attempts = new List<object>();
            List<object[]> axis = PrimitiveFactory.PointVariants(0, 0, 0, 0, 0, 1);
            PrimitiveKit.Built b = PrimitiveKit.Build(ctx,
                (fm, v) => fm.CreateCone(r, h, semiAngle, v[0], v[1]),
                axis, "CreatePart+IZPartFeatureMgr.CreateCone(21)+Update(7)", attempts);
            var dims = new Dictionary<string, object>
            {
                ["radiusMm"] = r / PrimitiveKit.MmToMeter,
                ["heightMm"] = h / PrimitiveKit.MmToMeter,
                ["semiAngleDeg"] = deg,
                ["semiAngleRadian"] = semiAngle
            };
            return PrimitiveKit.Finish(ctx, b, "圆锥", name, attempts, dims, args.ParamBool("sizebox", true));
        }
    }

    [ComVisible(false)]
    internal sealed class ToolCreateTorus : McpToolBase
    {
        public override string Name { get { return "create_torus"; } }
        public override string Category { get { return "model"; } }
        public override string Description
        {
            get { return "生成圆环/圆环体（IZPartFeatureMgr.CreateTorus(23)）：大半径(环心到管心)/小半径(管半径)，轴沿 +Z 过原点。默认大半径 20 mm、小半径 5 mm。"; }
        }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["major_radius"] = S.Num("大半径（环心到管心），默认 20", false, 20.0, true),
                ["minor_radius"] = S.Num("小半径（管半径），默认 5，必须 < 大半径", false, 5.0, true),
                ["unit"] = S.Str("单位：mm（默认）或 m", false, "mm"),
                ["name"] = S.Str("可选：给新建零件命名"),
                ["sizebox"] = S.Bool("是否执行包围盒四勾选 + Reset 共用链，默认 true", false, true, true)
            });
        }

        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = PrimitiveKit.NeedScene(ctx); if (bad != null) return bad;
            double major = PrimitiveKit.ToMeter(args, "major_radius", 20.0);
            double minor = PrimitiveKit.ToMeter(args, "minor_radius", 5.0);
            if (major <= 0 || minor <= 0 || minor >= major)
                return Fail("invalid_radius", "须满足 0 < 小半径 < 大半径");
            string name = args.ParamString("name", "");
            var attempts = new List<object>();
            List<object[]> axis = PrimitiveFactory.PointVariants(0, 0, 0, 0, 0, 1);
            PrimitiveKit.Built b = PrimitiveKit.Build(ctx,
                (fm, v) => fm.CreateTorus(major, minor, v[0], v[1]),
                axis, "CreatePart+IZPartFeatureMgr.CreateTorus(23)+Update(7)", attempts);
            var dims = new Dictionary<string, object>
            {
                ["majorRadiusMm"] = major / PrimitiveKit.MmToMeter,
                ["minorRadiusMm"] = minor / PrimitiveKit.MmToMeter
            };
            return PrimitiveKit.Finish(ctx, b, "圆环", name, attempts, dims, args.ParamBool("sizebox", true));
        }
    }

    // ============================================================ tracker 共用链的独立查询/配置工具

    [ComVisible(false)]
    internal sealed class ToolGetPartType : McpToolBase
    {
        public override string Name { get { return "get_part_type"; } }
        public override string Category { get { return "model"; } }
        public override string Description { get { return "回读零件种类掩码（IZPart.GetPartType(51)）：0x10=创新零件、0x20=工程零件。对应 tracker 建模的权威判据；读不到返回第三态，不折算成功。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object> { ["name"] = S.Str("可选：零件名称；不传取场景第一个零件") });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = PrimitiveKit.NeedScene(ctx); if (bad != null) return bad;
            string name = args.ParamString("name", "");
            IZPart part = PrimitiveKit.FindPart(ctx, name);
            if (part == null) return Fail(string.IsNullOrEmpty(name) ? "场景中没有零件" : "未找到零件：" + name);
            var result = new Dictionary<string, object> { ["ok"] = true, ["partName"] = McpToolContext.SafeNameOf(part) };
            PrimitiveKit.ReadPartType(part, result);
            return result;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolSetSizeBoxHandles : McpToolBase
    {
        public override string Name { get { return "set_sizebox_handles"; } }
        public override string Category { get { return "model"; } }
        public override string Description { get { return "包围盒共用链：IZSizeBox.SetDisplayHandle 四勾选 → 回读 → PermitResetSizeBox 预检 → Reset(false) → Update → L/W/H 回读。对应 tracker 基体模型建立第 3.5/3.6 步；失败记第三态。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["name"] = S.Str("可选：零件名称；不传取场景第一个零件"),
                ["reset"] = S.Bool("是否执行 Reset 重置包围盒，默认 true", false, true, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = PrimitiveKit.NeedScene(ctx); if (bad != null) return bad;
            IZPart part = PrimitiveKit.FindPart(ctx, args.ParamString("name", ""));
            if (part == null) return Fail("场景中没有匹配的零件");
            IZSizeBox sb = PrimitiveKit.FindSizeBox(part, 0);
            if (sb == null) return Fail("sizebox_not_found", "特征/零件元素树上未找到 IZSizeBox 表面（第三态，未设置）");
            var result = new Dictionary<string, object> { ["ok"] = true, ["partName"] = McpToolContext.SafeNameOf(part) };
            PrimitiveKit.ConfigureSizeBox(ctx, sb, args.ParamBool("reset", true) ? part : null, result);
            return result;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetSizeBox : McpToolBase
    {
        public override string Name { get { return "get_sizebox"; } }
        public override string Category { get { return "model"; } }
        public override string Description { get { return "只读回读零件的包围盒驱动尺寸（IZSizeBox.Length/Width/Height，mm）与四个显示柄状态，不产生任何写入。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object> { ["name"] = S.Str("可选：零件名称；不传取场景第一个零件") });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = PrimitiveKit.NeedScene(ctx); if (bad != null) return bad;
            IZPart part = PrimitiveKit.FindPart(ctx, args.ParamString("name", ""));
            if (part == null) return Fail("场景中没有匹配的零件");
            IZSizeBox sb = PrimitiveKit.FindSizeBox(part, 0);
            if (sb == null) return Fail("sizebox_not_found", "特征/零件元素树上未找到 IZSizeBox 表面（第三态）");
            var result = new Dictionary<string, object> { ["ok"] = true, ["partName"] = McpToolContext.SafeNameOf(part) };
            PrimitiveKit.ReadSizeBox(sb, result);
            try
            {
                bool l, w, h, b;
                sb.GetDisplayHandle(out l, out w, out h, out b);
                result["sizeboxHandles"] = new List<object> { l, w, h, b };
            }
            catch (Exception ex) { result["sizeboxHandleError"] = "GetDisplayHandle 失败（第三态）：" + ex.Message; }
            return result;
        }
    }
}
