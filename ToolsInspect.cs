// 工具集：inspect 类 —— 对应 caxa3dmytoolsaddin 子插件 caxa3dButton（内核/特征/元素调试）、
// caxa3dsheetmaster（钣金 K 因子）、caxa3dAttributeManageraddin / caxa3dBatchMaster（批量属性写入）。
//
// 通道取证（2027 TLB members.csv，全部 RCW 直调）：
//   IZPart.GetBody(8) → IZBody：GetFaceIds(8)/GetFacesCount(14)/GetBoundingBox(13)/GetTightBoundingBox(21)
//   IZBodyFace.GetFaceBoundingBox(9, Int32 lFaceId)
//   IZPartFeatureMgr.GetFeaturesCount(7)/GetFeatureAt(9) → IZFeature.get·set_OperationType(9/10)
//   IZPart.get·set_KernelType(11/12)；IZSheetMetalPart.get_KFactor(17)/get_Thickness(10)/
//     get_MinBendRadius(19)/GetStockProperties(14, ★7 个 out 必须走 RCW)
//   IZCustomPropMgr.SetValueByName(10)/AddCustomProp(14)/RemoveByName(13)/GetValueByName(9)
//
// 实证口径：
//   ★ GetStockProperties 有 7 个 out，裸槽位直调会破坏栈导致闪退（caxa3dsheetmaster/Probe.cs:185）
//   ★ SetValueByName 传空串在本版本返回 E_INVALIDARG，清空必须走 RemoveByName（caxa3dBatchMaster/CaxaPropWrite.cs:7）
//   ★ 共享表唯一判据 IsSharedMgr()==true；三条候选都失败即第三态，不拿零件级表兜底（规则第 14 节 7/9）
//   ★ 判成功只看写后回读，不看 API 返回值（集合体第九节约定 15）
//   ★ K 因子公式（caxa3dsheetmaster/KFactorCalcForm.cs:10,226）：K=(4R+4T-2X)/(π·T)-R/T，纯计算零 COM
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    [ComVisible(false)]
    internal static class InspectKit
    {
        /// <summary>定位零件并切到 IZPart 表面。</summary>
        public static Dictionary<string, object> Part(McpToolContext ctx, JValue args, out IZElement e, out IZPart part)
        {
            string err;
            e = Elem.Find(ctx, args, out err);
            if (e == null) { part = null; return Fail(err); }
            part = e as IZPart;
            if (part == null) return Fail("part_surface_missing", "目标元素不是零件(IZPart)，第三态");
            return null;
        }

        private static Dictionary<string, object> Fail(string reason)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = reason };
        }

        private static Dictionary<string, object> Fail(string code, string reason)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["code"] = code, ["error"] = reason };
        }

        public static Dictionary<string, object> LocatorSchema()
        {
            return new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"), ["name"] = S.Str("元素名称（用于定位）"),
                ["last_created"] = S.Bool("取最后一个顶层元素", false, false, true)
            };
        }

        /// <summary>把包围盒/点数组转成 mm 尺寸列表（CAXA 内部为米）。</summary>
        public static List<object> ToMm(object raw)
        {
            var r = new List<object>();
            double[] d = McpToolContext.ToDoubleArray(raw);
            if (d == null) return r;
            for (int i = 0; i < d.Length && i < 12; i++) r.Add(d[i] * 1000.0);
            return r;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetBodyInfo : McpToolBase
    {
        public override string Name { get { return "get_body_info"; } }
        public override string Category { get { return "inspect"; } }
        public override string Description { get { return "读取零件内核几何信息：体类型/流形性、面/边/顶点数、面 Id 列表、包围盒与紧致盒（mm）。对应 caxa3dButton 内核与元素调试工具。"; } }
        public override object InputSchema()
        {
            var p = InspectKit.LocatorSchema();
            p["max_faces"] = S.Num("最多逐面回读包围盒的面数，默认 8", false, 8.0, true);
            return S.Schema(p);
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZElement e; IZPart part;
            var bad = InspectKit.Part(ctx, args, out e, out part); if (bad != null) return bad;
            IZBody body = part.GetBody(true) as IZBody;
            if (body == null) return Fail("body_surface_missing", "GetBody(true) 未返回 IZBody 表面（第三态）");
            var d = new Dictionary<string, object> { ["ok"] = true, ["element"] = McpToolContext.SafeName(e) };
            try { d["bodyType"] = body.BodyType.ToString(); } catch { }
            try { d["manifold"] = body.Manifold; } catch { }
            try { d["facesCount"] = body.GetFacesCount(); } catch { }
            try { d["edgesCount"] = body.GetEdgesCount(); } catch { }
            try { d["verticesCount"] = body.GetVerticesCount(); } catch { }
            try { d["lumpsCount"] = body.GetLumpsCount(); } catch { }
            try { d["bboxMm"] = InspectKit.ToMm(body.GetBoundingBox()); } catch (Exception ex) { d["bboxError"] = ex.Message; }
            try { d["tightBBoxMm"] = InspectKit.ToMm(body.GetTightBoundingBox()); } catch (Exception ex) { d["tightBBoxError"] = ex.Message; }
            var ids = new List<object>();
            try
            {
                foreach (object o in McpToolContext.UnpackArray(body.GetFaceIds())) ids.Add(ToolGetCustomProps.Scalar(o));
                d["faceIds"] = ids;
            }
            catch (Exception ex) { d["faceIdsError"] = ex.Message; }
            IZBodyFace bf = body as IZBodyFace;
            int max = args.ParamInt("max_faces", 8); if (max < 0) max = 0; if (max > 50) max = 50;
            if (bf != null && max > 0)
            {
                var faces = new List<object>();
                for (int i = 0; i < ids.Count && i < max; i++)
                {
                    int faceId;
                    try { faceId = Convert.ToInt32(ids[i], CultureInfo.InvariantCulture); }
                    catch { continue; }
                    try
                    {
                        faces.Add(new Dictionary<string, object>
                        { ["faceId"] = faceId, ["bboxMm"] = InspectKit.ToMm(bf.GetFaceBoundingBox(faceId)) });
                    }
                    catch (Exception ex)
                    {
                        faces.Add(new Dictionary<string, object> { ["faceId"] = faceId, ["error"] = ex.Message });
                    }
                }
                d["faceDetails"] = faces;
            }
            else d["faceDetailsNote"] = bf == null ? "当前 body 不支持 IZBodyFace 表面（第三态）" : "max_faces=0，跳过逐面回读";
            return d;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetFeatureInfo : McpToolBase
    {
        public override string Name { get { return "get_feature_info"; } }
        public override string Category { get { return "inspect"; } }
        public override string Description { get { return "枚举零件特征（GetFeaturesCount/GetFeatureAt）并回读每个特征的 OperationType（增料/除料）。对应 caxa3dButton.FeatureTool。"; } }
        public override object InputSchema()
        {
            var p = InspectKit.LocatorSchema();
            p["max_features"] = S.Num("最多枚举特征数，默认 50", false, 50.0, true);
            return S.Schema(p);
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZElement e; IZPart part;
            var bad = InspectKit.Part(ctx, args, out e, out part); if (bad != null) return bad;
            IZPartFeatureMgr fm = e as IZPartFeatureMgr;
            if (fm == null) return Fail("feature_mgr_surface_missing", "目标不支持 IZPartFeatureMgr 表面（第三态）");
            int n; try { n = fm.GetFeaturesCount(); } catch (Exception ex) { return Fail("feature_count_failed", "GetFeaturesCount 失败：" + ex.Message); }
            int max = args.ParamInt("max_features", 50); if (max < 0) max = 0; if (max > 500) max = 500;
            var feats = new List<object>();
            for (int i = 0; i < n && i < max; i++)
            {
                var f = new Dictionary<string, object> { ["index"] = i };
                try
                {
                    IZFeature ft = fm.GetFeatureAt(i);
                    f["name"] = McpToolContext.SafeNameOf(ft);
                    try { f["operationType"] = ft.OperationType.ToString(); } catch (Exception ex) { f["operationTypeError"] = ex.Message; }
                }
                catch (Exception ex) { f["error"] = ex.Message; }
                feats.Add(f);
            }
            return Ok("element", McpToolContext.SafeName(e), "totalCount", n, "enumerated", feats.Count, "features", feats);
        }
    }

    [ComVisible(false)]
    internal sealed class ToolSetOperationType : McpToolBase
    {
        public override string Name { get { return "set_operation_type"; } }
        public override string Category { get { return "inspect"; } }
        public override string Description { get { return "切换零件某个特征的增料/除料类型（IZFeature.OperationType），写后 Update 并回读校验。对应 caxa3dButton.FeatureTool 增料/除料切换。"; } }
        public override object InputSchema()
        {
            var p = InspectKit.LocatorSchema();
            p["index"] = S.Num("特征索引，默认 0（第一个特征）", false, 0.0, true);
            p["operation"] = S.Enum("目标类型", new[] { "add", "subtract" }, true);
            return S.Schema(p, "operation");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZElement e; IZPart part;
            var bad = InspectKit.Part(ctx, args, out e, out part); if (bad != null) return bad;
            IZPartFeatureMgr fm = e as IZPartFeatureMgr;
            if (fm == null) return Fail("feature_mgr_surface_missing", "目标不支持 IZPartFeatureMgr 表面（第三态）");
            int idx = args.ParamInt("index", 0);
            int n; try { n = fm.GetFeaturesCount(); } catch (Exception ex) { return Fail("feature_count_failed", ex.Message); }
            if (idx < 0 || idx >= n) return Fail("index_out_of_range", "特征索引越界：共 " + n + " 个特征");
            IZFeature ft;
            try { ft = fm.GetFeatureAt(idx); } catch (Exception ex) { return Fail("feature_at_failed", ex.Message); }
            string want = args.ParamString("operation", "").ToLowerInvariant();
            bool subtract = want == "subtract" || want == "cut" || want == "sub";
            if (want != "add" && want != "union" && want != "boss" && !subtract)
                return Fail("bad_operation", "operation 只能是 add / subtract");
            // 不写死枚举成员名：运行时按名称匹配（同 eZImageFormat 的自适应手法）
            eZOperationType chosen = ft.OperationType; bool matched = false;
            foreach (eZOperationType v in Enum.GetValues(typeof(eZOperationType)))
            {
                string name = v.ToString().ToLowerInvariant();
                bool isSub = name.Contains("subtract") || name.Contains("cut") || name.Contains("sub");
                bool isAdd = name.Contains("add") || name.Contains("union") || name.Contains("boss") || name.Contains("addmaterial");
                if (subtract && isSub) { chosen = v; matched = true; break; }
                if (!subtract && isAdd) { chosen = v; matched = true; break; }
            }
            if (!matched) return Fail("enum_member_not_found", "eZOperationType 里未找到匹配 " + want + " 的成员（第三态）");
            string before; try { before = ft.OperationType.ToString(); } catch { before = null; }
            try { ft.OperationType = chosen; part.Update(); }
            catch (Exception ex) { return Fail("operation_write_failed", "写 OperationType / Update 失败：" + ex.Message); }
            string after; try { after = ft.OperationType.ToString(); } catch { after = null; }
            return Ok("element", McpToolContext.SafeName(e), "index", idx, "requested", want,
                      "enumMember", chosen.ToString(), "before", before, "readBack", after,
                      "verified", after == chosen.ToString());
        }
    }

    [ComVisible(false)]
    internal sealed class ToolSetKernelType : McpToolBase
    {
        public override string Name { get { return "set_kernel_type"; } }
        public override string Category { get { return "inspect"; } }
        public override string Description { get { return "切换零件内核（IZPart.KernelType：Parasolid / ACIS），写后回读校验。对应 caxa3dButton.KernelTool。"; } }
        public override object InputSchema()
        {
            var p = InspectKit.LocatorSchema();
            p["kernel"] = S.Enum("目标内核", new[] { "parasolid", "acis" }, true);
            return S.Schema(p, "kernel");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZElement e; IZPart part;
            var bad = InspectKit.Part(ctx, args, out e, out part); if (bad != null) return bad;
            string want = args.ParamString("kernel", "").ToLowerInvariant();
            eZKernelType chosen = part.KernelType; bool matched = false;
            foreach (eZKernelType v in Enum.GetValues(typeof(eZKernelType)))
            {
                string name = v.ToString().ToLowerInvariant();
                if ((want == "parasolid" && name.Contains("parasolid")) || (want == "acis" && name.Contains("acis")))
                { chosen = v; matched = true; break; }
            }
            if (!matched) return Fail("enum_member_not_found", "eZKernelType 里未找到匹配 " + want + " 的成员（第三态）");
            string before; try { before = part.KernelType.ToString(); } catch { before = null; }
            try { part.KernelType = chosen; part.Update(); }
            catch (Exception ex) { return Fail("kernel_write_failed", "写 KernelType / Update 失败：" + ex.Message); }
            string after; try { after = part.KernelType.ToString(); } catch { after = null; }
            return Ok("element", McpToolContext.SafeName(e), "requested", want, "enumMember", chosen.ToString(),
                      "before", before, "readBack", after, "verified", after == chosen.ToString());
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetSheetMetalInfo : McpToolBase
    {
        public override string Name { get { return "get_sheet_metal_info"; } }
        public override string Category { get { return "inspect"; } }
        public override string Description { get { return "读取钣金件参数：K 因子、板厚、最小折弯半径、板料属性（GetStockProperties 7 个 out 走 RCW）。对应 caxa3dsheetmaster。非钣金件返回第三态。"; } }
        public override object InputSchema() { return S.Schema(InspectKit.LocatorSchema()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string err; IZElement e = Elem.Find(ctx, args, out err);
            if (e == null) return Fail(err);
            IZSheetMetalPart sm = e as IZSheetMetalPart;
            if (sm == null) return Fail("not_sheet_metal", "目标不是钣金件（IZSheetMetalPart 表面不可用，第三态）");
            var d = new Dictionary<string, object> { ["ok"] = true, ["element"] = McpToolContext.SafeName(e) };
            try { d["kFactor"] = sm.KFactor; } catch (Exception ex) { d["kFactorError"] = ex.Message; }
            try { d["thickness"] = sm.Thickness; } catch (Exception ex) { d["thicknessError"] = ex.Message; }
            try { d["minBendRadius"] = sm.MinBendRadius; } catch (Exception ex) { d["minBendRadiusError"] = ex.Message; }
            try
            {
                // ★ 7 个 out 必须走 RCW 直调，裸槽位直调会破坏栈闪退（Probe.cs:185 实证）
                string name, code; double weight, thick, kf, mbr; int gauge;
                sm.GetStockProperties(out name, out code, out weight, out gauge, out thick, out kf, out mbr);
                d["stock"] = new Dictionary<string, object>
                {
                    ["name"] = name, ["code"] = code, ["weight"] = weight, ["gauge"] = gauge,
                    ["thickness"] = thick, ["kFactor"] = kf, ["minBendRadius"] = mbr
                };
            }
            catch (Exception ex) { d["stockError"] = "GetStockProperties 失败（第三态）：" + ex.Message; }
            return d;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolCalcKFactor : McpToolBase
    {
        public override string Name { get { return "calc_k_factor"; } }
        public override string Category { get { return "inspect"; } }
        public override string Description { get { return "纯计算钣金 K 因子（不读写 CAXA）：K=(4R+4T-2X)/(π·T)-R/T；可顺带算折弯补偿 BA。对应 caxa3dsheetmaster.KFactorCalcForm。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["thickness"] = S.Num("板厚 T（mm），默认 3", false, 3.0, true),
                ["inner_radius"] = S.Num("内折弯半径 R（mm），默认 3", false, 3.0, true),
                ["setback"] = S.Num("折弯扣除 X（mm），默认 6", false, 6.0, true),
                ["bend_angle"] = S.Num("折弯角 A（度），默认 90；用于计算 BA", false, 90.0, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            double T = args.ParamDouble("thickness", 3.0);
            double R = args.ParamDouble("inner_radius", 3.0);
            double X = args.ParamDouble("setback", 6.0);
            double A = args.ParamDouble("bend_angle", 90.0);
            if (T <= 0) return Fail("bad_thickness", "板厚 T 必须为正数");
            double K = (4.0 * R + 4.0 * T - 2.0 * X) / (Math.PI * T) - R / T;
            double BA = (Math.PI / 180.0) * A * (R + K * T);
            // 自校验：tracker 示例 T=3,R=3,X=6 应得 0.2732（KFactorCalcForm.cs:101）
            return Ok("formula", "K=(4R+4T-2X)/(PI*T)-R/T", "thicknessMm", T,
                      "innerRadiusMm", R, "setbackMm", X, "bendAngleDeg", A,
                      "kFactor", K, "bendAllowanceMm", BA,
                      "note", "纯计算工具，不读写 CAXA；示例自检 T=3,R=3,X=6 ⇒ K≈0.2732");
        }
    }

    [ComVisible(false)]
    internal sealed class ToolBatchSetCustomProps : McpToolBase
    {
        public override string Name { get { return "batch_set_custom_props"; } }
        public override string Category { get { return "inspect"; } }
        public override string Description { get { return "批量向共享自定义属性表写入/清空多项（对应 caxa3dAttributeManageraddin / caxa3dBatchMaster）。空值走 RemoveByName，新属性走 AddCustomProp 回退，逐项写后回读；共享表铁律 IsSharedMgr 严格校验。"; } }
        public override object InputSchema()
        {
            var p = InspectKit.LocatorSchema();
            p["props"] = S.Array("待写属性数组：[{name,value}]；value 为空字符串表示清空该属性",
                new Dictionary<string, object>
                {
                    ["name"] = S.Str("属性名", true),
                    ["value"] = S.Str("属性值；空串＝删除该属性")
                }, "name");
            p["stop_on_error"] = S.Bool("遇错是否停止，默认 false（继续处理其余项）", false, false, true);
            return S.Schema(p, "props");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string err; IZElement e = Elem.Find(ctx, args, out err);
            if (e == null) return Fail(err);
            JValue list = args["props"];
            if (list.Kind != JKind.Array || list.Arr == null || list.Arr.Count == 0)
                return Fail("bad_props", "props 必须是非空数组 [{\"name\":\"...\",\"value\":\"...\"}]");
            string diag; IZCustomPropMgr mgr = Elem.SharedMgr(e, out diag);
            if (mgr == null) return new Dictionary<string, object>
            { ["ok"] = false, ["error"] = "未取得共享属性表，按共享表铁律拒绝批量写入", ["diagnostic"] = diag };
            bool stop = args.ParamBool("stop_on_error", false);
            var results = new List<object>();
            int okCount = 0;
            foreach (JValue item in list.Arr)
            {
                string pn = item.ParamString("name", "");
                string val = item.ParamString("value", "");
                var one = new Dictionary<string, object> { ["name"] = pn, ["value"] = val };
                if (string.IsNullOrEmpty(pn)) { one["ok"] = false; one["error"] = "属性名为空"; results.Add(one); if (stop) break; continue; }
                var attempts = new List<object>();
                try
                {
                    if (val.Length == 0)
                    {
                        // ★ 实测：SetValueByName 传空串返回 E_INVALIDARG，清空必须走 RemoveByName
                        mgr.RemoveByName(pn);
                        attempts.Add(new Dictionary<string, object> { ["channel"] = "RemoveByName(13)", ["ok"] = true });
                        object after = null; bool still = false;
                        try { after = mgr.GetValueByName(pn); still = after != null && Convert.ToString(after).Length > 0; } catch { }
                        one["ok"] = !still; one["channel"] = "RemoveByName(13)"; one["verified"] = !still;
                    }
                    else
                    {
                        try
                        {
                            mgr.SetValueByName(pn, val);
                            attempts.Add(new Dictionary<string, object> { ["channel"] = "SetValueByName(10)", ["ok"] = true });
                            one["channel"] = "SetValueByName(10)";
                        }
                        catch (Exception ex)
                        {
                            attempts.Add(new Dictionary<string, object> { ["channel"] = "SetValueByName(10)", ["ok"] = false, ["error"] = ex.Message });
                            mgr.AddCustomProp(pn, val, true);
                            attempts.Add(new Dictionary<string, object> { ["channel"] = "AddCustomProp(14,replace=true)", ["ok"] = true });
                            one["channel"] = "AddCustomProp(14)";
                        }
                        object rb = null;
                        try { rb = mgr.GetValueByName(pn); } catch { }
                        one["readBack"] = ToolGetCustomProps.Scalar(rb);
                        bool verified = string.Equals(Convert.ToString(rb), val, StringComparison.Ordinal);
                        one["verified"] = verified;
                        one["ok"] = verified;
                    }
                }
                catch (Exception ex)
                {
                    attempts.Add(new Dictionary<string, object> { ["channel"] = "write", ["ok"] = false, ["error"] = ex.Message });
                    one["ok"] = false; one["error"] = ex.Message;
                }
                one["attempts"] = attempts;
                results.Add(one);
                if (one.ContainsKey("ok") && Convert.ToBoolean(one["ok"])) okCount++;
                else if (stop) break;
            }
            return Ok("element", McpToolContext.SafeName(e), "requested", list.Arr.Count,
                      "succeeded", okCount, "results", results, "sharedTableDiagnostic", diag);
        }
    }
}
