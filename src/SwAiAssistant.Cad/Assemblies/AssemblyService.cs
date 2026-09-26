using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SwAiAssistant.Cad.Session;
using SwAiAssistant.Core.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAiAssistant.Cad.Assemblies
{
    /// <summary>装配体组件快照（POCO，不暴露 COM）。</summary>
    public sealed class ComponentInfo
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IsFixed { get; set; }
        /// <summary>组件原点在装配空间的平移（mm）。</summary>
        public double TxMm { get; set; }
        public double TyMm { get; set; }
        public double TzMm { get; set; }
    }

    /// <summary>单次配合结果。</summary>
    public sealed class MateResult
    {
        public bool Success { get; set; }
        /// <summary>AddMate3 末参 ErrorStatus（0=成功）。</summary>
        public int ErrorCode { get; set; }
        public string MateType { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>单条干涉信息。</summary>
    public sealed class InterferenceItem
    {
        /// <summary>干涉体积（mm³）。</summary>
        public double VolumeMm3 { get; set; }
        /// <summary>参与该干涉的组件名对。</summary>
        public List<string> Components { get; set; } = new List<string>();
    }

    /// <summary>干涉检查报告（含中文文本）。</summary>
    public sealed class InterferenceReport
    {
        public int Count => Items.Count;
        public List<InterferenceItem> Items { get; set; } = new List<InterferenceItem>();
        public string TextReport { get; set; } = "";
    }

    /// <summary>已打开零件快照（用于对话式装配选型）。</summary>
    public sealed class OpenPartInfo
    {
        public string Title { get; set; } = "";
        public string Path { get; set; } = "";
        /// <summary>首个实体包围盒体积代理（m³，用于基座/装配件大小排序）。</summary>
        public double BoxVolumeM3 { get; set; }
    }

    /// <summary>一键对话式装配结果。</summary>
    public sealed class AssemblyBuildResult
    {
        public IModelDoc2 Assembly { get; set; }
        public string BaseTitle { get; set; } = "";
        public string PinTitle { get; set; } = "";
        public List<MateResult> Mates { get; set; } = new List<MateResult>();
        public InterferenceReport Interference { get; set; } = new InterferenceReport();
    }

    /// <summary>
    /// 装配体辅助服务（M7-T23）：
    /// 模板自动发现（gb_assembly.asmdot 优先）→ 新建装配体 → 插入零件（AddComponent5，
    /// 经典前置：组件文件必须已在 SW 会话中打开，否则返回 null——本服务经 GetOpenDocumentByName
    /// 判定并按需 OpenDoc6 静默加载）→ 语义面解析（圆柱面/端面）+ 同轴/重合/距离配合（Mark=1
    /// 多选后 AddMate3）→ 干涉检查（InterferenceDetectionMgr，体积单位 m³ 转 mm³）。
    /// 全部方法经 SwSession.OnSta 封送 STA。
    /// 注意：本机 redist 的 swMateType_e 编号 COINCIDENT=0/CONCENTRIC=1/DISTANCE=5
    /// （与部分网络文档的旧编号不同），一律用枚举名，不写裸数字。
    /// </summary>
    public sealed class AssemblyService
    {
        /// <summary>swUserPreferenceStringValue_e.swDefaultTemplateAssembly（默认装配体模板首选项）。</summary>
        private const int SwDefaultTemplateAssembly = 9;

        private readonly SwSession _session;

        public AssemblyService(SwSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// 装配体模板自动发现：
        /// 1) SW 首选项默认装配体模板（存在性校验）；
        /// 2) ProgramData 按版本年目录回退：gb_assembly.asmdot → 任意 *.asmdot；
        /// 3) 全部失败抛中文 CadException。
        /// </summary>
        public string FindAssemblyTemplate()
        {
            return _session.OnSta(() =>
            {
                try
                {
                    string preferred = _session.App.GetUserPreferenceStringValue(SwDefaultTemplateAssembly);
                    if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred)
                        && preferred.EndsWith(".asmdot", StringComparison.OrdinalIgnoreCase))
                    {
                        return preferred;
                    }

                    int year = RevisionYear();
                    foreach (int y in new[] { year, year - 1 })
                    {
                        string dir = Path.Combine(
                            System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
                            "SOLIDWORKS", $"SOLIDWORKS {y}", "templates");
                        if (!Directory.Exists(dir)) continue;
                        string gb = Path.Combine(dir, "gb_assembly.asmdot");
                        if (File.Exists(gb)) return gb;
                        string any = Directory.GetFiles(dir, "*.asmdot").FirstOrDefault();
                        if (any != null) return any;
                    }
                }
                catch (Exception ex) when (!(ex is CadException))
                {
                    throw CadException.FromCom("查找装配体模板", ex);
                }
                throw new CadException("找不到装配体模板（.asmdot）：请确认 SolidWorks 已安装 GB 装配体模板（gb_assembly.asmdot）。");
            });
        }

        /// <summary>用自动发现的模板新建装配体；新文档成为活动文档。</summary>
        public IModelDoc2 NewAssembly()
        {
            return _session.OnSta(() =>
            {
                string template = FindAssemblyTemplate();
                try
                {
                    object doc = _session.App.NewDocument(template, 0, 0.0, 0.0);
                    if (doc is IModelDoc2 model && model is IAssemblyDoc)
                    {
                        Log.Info("Cad", "已新建装配体：" + SafeTitle(model));
                        return model;
                    }
                    throw new CadException("新建装配体失败：NewDocument 返回空或类型不对（模板可能损坏：" + template + "）。");
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("新建装配体", ex); }
            });
        }

        /// <summary>
        /// 向装配体插入零件/子装配（AddComponent5，定位坐标米）。
        /// 文件必须存在；未在会话中打开时先静默 OpenDoc6（AddComponent5 对未加载文件返回 null）。
        /// </summary>
        public IComponent2 InsertComponent(IModelDoc2 assembly, string partPath,
            double xM = 0.0, double yM = 0.0, double zM = 0.0)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
                throw new CadException("待插入的零件文件不存在：" + (partPath ?? "<空>"));
            if (!(assembly is IAssemblyDoc asm))
                throw new CadException("当前文档不是装配体，无法插入组件。");

            return _session.OnSta(() =>
            {
                EnsureLoaded(partPath);
                try
                {
                    // ConfigOption=0 CurrentSelectedConfig；NewConfigName/ExistingConfigName 空
                    object c = asm.AddComponent5(partPath,
                        (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                        "", false, "", xM, yM, zM);
                    if (c is IComponent2 comp)
                    {
                        Log.Info("Cad", "已插入组件：" + partPath);
                        return comp;
                    }
                    throw new CadException("AddComponent5 返回空（文件可能未加载或配置名无效）：" + partPath);
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("插入组件（" + Path.GetFileName(partPath) + "）", ex); }
            });
        }

        /// <summary>枚举装配体顶层组件（ToplevelOnly=true）。</summary>
        public IList<IComponent2> GetComponents(IModelDoc2 assembly, bool topLevelOnly = true)
        {
            if (!(assembly is IAssemblyDoc asm))
                throw new CadException("当前文档不是装配体。");
            return _session.OnSta(() =>
            {
                var list = new List<IComponent2>();
                if (asm.GetComponents(topLevelOnly) is object[] arr)
                {
                    list.AddRange(arr.OfType<IComponent2>());
                }
                return list;
            });
        }

        /// <summary>组件快照列表（名称/路径/固定状态/平移 mm）。</summary>
        public IList<ComponentInfo> GetComponentInfos(IModelDoc2 assembly)
        {
            return GetComponents(assembly).Select(ToInfo).ToList();
        }

        /// <summary>读取组件原点平移（mm）。</summary>
        public (double Xmm, double Ymm, double Zmm) GetComponentPositionMm(IComponent2 comp)
        {
            return _session.OnSta(() =>
            {
                if (!(comp.Transform2?.ArrayData is double[] t) || t.Length < 12)
                    throw new CadException("读取组件变换失败。");
                return (t[9] * 1000.0, t[10] * 1000.0, t[11] * 1000.0);
            });
        }

        /// <summary>
        /// 同轴配合：在两组件上各找一个半径匹配的圆柱面（给 null 半径则取各自最大圆柱面，
        /// 轴方向不限——GB 模板下拉伸轴常为 Y），加同轴配合。
        /// </summary>
        public MateResult MateConcentric(IModelDoc2 assembly, IComponent2 a, IComponent2 b,
            double? radiusMm = null)
        {
            IFace2 fa = FindCylindricalFace(a, radiusMm);
            IFace2 fb = FindCylindricalFace(b, radiusMm);
            if (fa == null || fb == null)
            {
                return new MateResult
                {
                    Success = false,
                    MateType = "同轴",
                    Message = "语义面解析失败：未在两组件上找到匹配的圆柱面（半径 "
                        + (radiusMm.HasValue ? radiusMm.Value.ToString("0.##") + "mm" : "自动") + "）。"
                };
            }
            return AddMate(assembly, new object[] { fa, fb },
                swMateType_e.swMateCONCENTRIC, 0.0, "同轴");
        }

        /// <summary>
        /// 端面重合配合：两端面必须垂直于各自的配合圆柱面（孔/销）。
        /// wantMaxA/B=true 取圆柱轴正向出口端面，false 取反向端面。
        /// 典型「销装入板孔、销根端面贴板的轴正向出口面」= (板,true)+(销,false)。
        /// radiusMm=null 时取各自最大圆柱面。
        /// </summary>
        public MateResult MateEndFaceCoincident(IModelDoc2 assembly,
            IComponent2 a, bool wantMaxA, IComponent2 b, bool wantMaxB, double? radiusMm = null)
        {
            IFace2 fa = ResolveEndFace(a, wantMaxA, radiusMm, out string errA);
            IFace2 fb = ResolveEndFace(b, wantMaxB, radiusMm, out string errB);
            if (fa == null || fb == null)
            {
                return new MateResult
                {
                    Success = false,
                    MateType = "重合",
                    Message = "语义面解析失败：" + errA + " " + errB
                };
            }
            return AddMate(assembly, new object[] { fa, fb },
                swMateType_e.swMateCOINCIDENT, 0.0, "重合");
        }

        /// <summary>
        /// 距离配合：两圆柱轴端面之间保持 distanceMm 间距（端面方向语义同重合配合）。
        /// </summary>
        public MateResult MateEndFaceDistance(IModelDoc2 assembly,
            IComponent2 a, bool wantMaxA, IComponent2 b, bool wantMaxB, double distanceMm,
            double? radiusMm = null)
        {
            IFace2 fa = ResolveEndFace(a, wantMaxA, radiusMm, out string errA);
            IFace2 fb = ResolveEndFace(b, wantMaxB, radiusMm, out string errB);
            if (fa == null || fb == null)
            {
                return new MateResult
                {
                    Success = false,
                    MateType = "距离",
                    Message = "语义面解析失败：" + errA + " " + errB
                };
            }
            return AddMate(assembly, new object[] { fa, fb },
                swMateType_e.swMateDISTANCE, distanceMm / 1000.0, "距离");
        }

        /// <summary>
        /// 一键「把销同轴装到基座孔、端面重合」：先同轴后端面重合（基座轴正向出口 +
        /// 销反向入口）。任一失败返回对应结果列表（第一个失败即停）。
        /// </summary>
        public IList<MateResult> MatePinIntoHole(IModelDoc2 assembly,
            IComponent2 baseWithHole, IComponent2 pin, double? radiusMm = null)
        {
            var list = new List<MateResult>();
            var r1 = MateConcentric(assembly, baseWithHole, pin, radiusMm);
            list.Add(r1);
            if (!r1.Success) return list;
            list.Add(MateEndFaceCoincident(assembly, baseWithHole, true, pin, false, radiusMm));
            return list;
        }

        /// <summary>找圆柱面再取其轴端平面；失败时 err 填中文原因。</summary>
        private static IFace2 ResolveEndFace(IComponent2 comp, bool wantMax,
            double? radiusMm, out string err)
        {
            err = "";
            IFace2 cyl = FindCylindricalFace(comp, radiusMm);
            if (cyl == null)
            {
                err = "组件「" + SafeName(comp) + "」未找到配合圆柱面。";
                return null;
            }
            IFace2 end = FindEndFace(comp, cyl, wantMax);
            if (end == null) err = "组件「" + SafeName(comp) + "」未找到圆柱轴端面。";
            return end;
        }

        private static string SafeName(IComponent2 c)
        {
            try { return c.Name2; } catch { return "<组件>"; }
        }

        // ==================== 对话式一键编排（T23 UI 入口） ====================

        /// <summary>枚举当前会话中已保存（磁盘可引用）的零件文档，按包围盒体积降序。</summary>
        public IList<OpenPartInfo> GetOpenSavedParts()
        {
            return _session.OnSta(() =>
            {
                var list = new List<OpenPartInfo>();
                foreach (IModelDoc2 doc in _session.GetDocuments())
                {
                    try
                    {
                        if (doc.GetType() != (int)swDocumentTypes_e.swDocPART) continue;
                        string path = doc.GetPathName();
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                        list.Add(new OpenPartInfo
                        {
                            Title = doc.GetTitle(),
                            Path = path,
                            BoxVolumeM3 = FirstBodyBoxVolume(doc)
                        });
                    }
                    catch { /* 单个文档读取失败不阻断枚举 */ }
                }
                return list.OrderByDescending(p => p.BoxVolumeM3).ToList();
            });
        }

        /// <summary>
        /// 对话式一键装配（「把 A 同轴装到 B 的孔并端面重合」）：
        /// 取当前已打开、已保存零件中包围盒最大者为「基座（含孔）」、次大者为「装配件（销/轴）」；
        /// 新建装配体 → 原点插入基座、错开位置插入装配件 → 自动选各自最大圆柱面同轴 +
        /// 端面重合 → 强制重建 → 干涉检查。任何配合失败均在结果 Mates 中体现中文原因。
        /// </summary>
        public AssemblyBuildResult AssembleTwoOpenParts()
        {
            return _session.OnSta(() =>
            {
                var parts = GetOpenSavedParts();
                if (parts.Count < 2)
                {
                    throw new CadException("请先打开并保存至少两个零件（含孔的基座件 + 销轴类装配件），再进行同轴装配。");
                }
                OpenPartInfo basePart = parts[0];
                OpenPartInfo pinPart = parts[1];
                Log.Info("Cad", $"对话式装配选型：基座=「{basePart.Title}」装配件=「{pinPart.Title}」");

                IModelDoc2 asm = NewAssembly();
                IComponent2 baseComp = InsertComponent(asm, basePart.Path, 0, 0, 0);
                // 初始沿 Z 错开 100mm，避免求解前实体重叠；配合求解会精确归位
                IComponent2 pinComp = InsertComponent(asm, pinPart.Path, 0, 0, 0.10);

                var result = new AssemblyBuildResult
                {
                    Assembly = asm,
                    BaseTitle = basePart.Title,
                    PinTitle = pinPart.Title
                };
                result.Mates.AddRange(MatePinIntoHole(asm, baseComp, pinComp, null));
                try { asm.ForceRebuild3(false); } catch (Exception ex)
                {
                    Log.Warn("Cad", "装配重建异常：" + ex.Message);
                }
                result.Interference = CheckInterference(asm, false);
                return result;
            });
        }

        /// <summary>对当前活动装配体执行干涉检查；活动文档不是装配体时抛中文 CadException。</summary>
        public InterferenceReport CheckInterferenceActive()
        {
            return _session.OnSta(() =>
            {
                IModelDoc2 active = _session.GetActiveDocument();
                if (active == null || !(active is IAssemblyDoc))
                {
                    throw new CadException("当前活动文档不是装配体，请先打开一个装配体（.sldasm）再执行干涉检查。");
                }
                return CheckInterference(active, false);
            });
        }

        /// <summary>零件首个实体包围盒体积代理（m³）；无实体返回 0。</summary>
        private static double FirstBodyBoxVolume(IModelDoc2 doc)
        {
            try
            {
                if (!(((IPartDoc)doc).GetBodies2(0, false) is object[] bs) || bs.Length == 0) return 0;
                if (!((bs[0] as IBody2)?.GetBodyBox() is double[] b) || b.Length < 6) return 0;
                return Math.Max(0, b[3] - b[0]) * Math.Max(0, b[4] - b[1]) * Math.Max(0, b[5] - b[2]);
            }
            catch { return 0; }
        }

        /// <summary>
        /// 干涉检查（全部顶层组件）：InterferenceDetectionMgr，
        /// 体积 m³ → mm³；coincident=true 时把相贴面也计为干涉。
        /// </summary>
        public InterferenceReport CheckInterference(IModelDoc2 assembly, bool coincident = false)
        {
            if (!(assembly is IAssemblyDoc asm))
                throw new CadException("当前文档不是装配体。");
            return _session.OnSta(() =>
            {
                var report = new InterferenceReport();
                IInterferenceDetectionMgr mgr = null;
                try
                {
                    mgr = asm.InterferenceDetectionManager;
                    if (mgr == null) throw new CadException("取 InterferenceDetectionManager 返回空。");
                    mgr.TreatCoincidenceAsInterference = coincident;
                    mgr.UseTransform = false; // 组件已在装配中定位，直接按当前位置计算

                    object[] raw = mgr.GetInterferences() as object[]
                        ?? Array.Empty<object>();
                    foreach (object o in raw)
                    {
                        if (!(o is IInterference ii)) continue;
                        var item = new InterferenceItem { VolumeMm3 = ii.Volume * 1e9 };
                        if (ii.Components is object[] cs)
                        {
                            item.Components.AddRange(cs.OfType<IComponent2>().Select(SafeCompName));
                        }
                        report.Items.Add(item);
                    }
                }
                catch (CadException) { throw; }
                catch (Exception ex)
                {
                    throw CadException.FromCom("干涉检查", ex);
                }
                finally
                {
                    try { mgr?.Done(); } catch { }
                }

                var sb = new StringBuilder();
                if (report.Count == 0)
                {
                    sb.Append("干涉检查：未发现干涉（共检查 " + GetComponents(assembly).Count + " 个组件）。");
                }
                else
                {
                    sb.Append($"干涉检查：发现 {report.Count} 处干涉。");
                    for (int i = 0; i < report.Items.Count; i++)
                    {
                        var it = report.Items[i];
                        sb.Append($"\n{i + 1}. {string.Join(" ↔ ", it.Components)}：干涉体积 {it.VolumeMm3:F1} mm³");
                    }
                }
                report.TextReport = sb.ToString();
                Log.Info("Cad", report.TextReport.Replace("\n", " "));
                return report;
            });
        }

        // ==================== 内部实现 ====================

        /// <summary>AddComponent5 前置：文件未打开则静默加载（零件/装配按扩展名分流）。</summary>
        private void EnsureLoaded(string path)
        {
            bool open = false;
            try { open = _session.App.GetOpenDocumentByName(path) != null; }
            catch { open = false; }
            if (open) return;

            int docType = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                ? (int)swDocumentTypes_e.swDocASSEMBLY
                : (int)swDocumentTypes_e.swDocPART;
            int errs = 0, warns = 0;
            object doc;
            try
            {
                doc = _session.App.OpenDoc6(path, docType,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns);
            }
            catch (Exception ex)
            {
                throw CadException.FromCom("静默加载组件文件（" + Path.GetFileName(path) + "）", ex);
            }
            if (doc == null || errs != 0)
            {
                throw new CadException($"组件文件加载失败（OpenDoc6 errors={errs}）：{path}");
            }
        }

        /// <summary>
        /// Mark=1 多选配合实体后 AddMate3。距离配合距离单位米。
        /// 实测本 interop 选择路径：IEntity.Select4(append, Mark=1) 稳定可用，
        /// MultiSelect2 在装配上下文面数组上抛 InvalidCast，故仅作降级。
        /// 对齐方式 0（SW 自动）。注意 swAddMateError_NoError=1（本 interop 编号，
        /// 非通常以为的 0）；失败返回 Success=false 与错误码。
        /// </summary>
        private MateResult AddMate(IModelDoc2 doc, object[] entities,
            swMateType_e mateType, double distanceM, string tag)
        {
            return _session.OnSta(() =>
            {
                const int noError = (int)swAddMateError_e.swAddMateError_NoError; // 实测=1
                var result = new MateResult { MateType = tag };
                try
                {
                    doc.ClearSelection2(true);
                    var sm = doc.SelectionManager as ISelectionMgr;
                    if (sm == null) throw new CadException("取 SelectionManager 失败。");

                    int selected = 0;
                    try
                    {
                        foreach (object e in entities)
                        {
                            SelectData sd = sm.CreateSelectData();
                            sd.Mark = 1; // 配合实体统一 Mark=1
                            ((IEntity)e).Select4(true, sd);
                        }
                        selected = sm.GetSelectedObjectCount();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Cad", "配合面 Select4 选择异常：" + ex.Message);
                    }

                    if (selected < entities.Length)
                    {
                        // 降级：MultiSelect2（部分上下文会抛 InvalidCast，吞掉计数）
                        doc.ClearSelection2(true);
                        try
                        {
                            var sds = new object[entities.Length];
                            for (int i = 0; i < entities.Length; i++)
                            {
                                SelectData sd = sm.CreateSelectData();
                                sd.Mark = 1;
                                sds[i] = sd;
                            }
                            doc.Extension.MultiSelect2(entities, false, sds);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("Cad", "MultiSelect2 降级失败：" + ex.Message);
                        }
                    }

                    int err = 0;
                    var mate = (doc as IAssemblyDoc).AddMate3((int)mateType,
                        (int)swMateAlign_e.swMateAlignALIGNED, false,
                        distanceM, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, false, out err);
                    doc.ClearSelection2(true);
                    result.ErrorCode = err;
                    result.Success = mate != null && err == noError;
                    result.Message = result.Success ? "" : $"AddMate3 失败（err={err}）";
                    if (result.Success) Log.Info("Cad", $"配合已加：{tag}（type={mateType}）");
                    else Log.Warn("Cad", $"配合失败：{tag} AddMate3 err={err}");
                    return result;
                }
                catch (CadException) { throw; }
                catch (Exception ex)
                {
                    throw CadException.FromCom("添加配合（" + tag + "）", ex);
                }
            });
        }

        /// <summary>
        /// 组件实体上找圆柱面；radiusMm 给 null 时取半径最大者。
        /// 轴无关：GB 零件模板的拉伸方向可能沿 Y（实测），不能按固定 Z 轴过滤。
        /// </summary>
        public static IFace2 FindCylindricalFace(IComponent2 comp, double? radiusMm)
        {
            IFace2 best = null;
            double bestR = -1;
            double target = (radiusMm ?? 0.0) / 1000.0;
            const double tolM = 0.0005; // 0.5mm 半径容差
            foreach (IFace2 f in EnumCompFaces(comp))
            {
                if (!(f.GetSurface() is ISurface s) || !s.IsCylinder()) continue;
                if (!(s.CylinderParams is double[] p) || p.Length < 7) continue;
                if (radiusMm.HasValue)
                {
                    if (Math.Abs(p[6] - target) <= tolM) { best = f; break; }
                }
                else if (p[6] > bestR) { bestR = p[6]; best = f; }
            }
            return best;
        }

        /// <summary>
        /// 圆柱轴端平面：法向平行于圆柱轴的平面中，取轴对应坐标（与轴方向符号无关）
        /// 最大/最小者。wantMax=true 取轴正向出口端面。
        /// 实测本 interop 的 PlaneParams 布局为 [NX,NY,NZ, PX,PY,PZ]（法向在前，
        /// 与部分网络文档顺序相反），务必按下标 0-2 法向、3-5 根点读取。
        /// </summary>
        public static IFace2 FindEndFace(IComponent2 comp, IFace2 cylinder, bool wantMax)
        {
            int axisIdx = DominantAxisIndex(cylinder);
            IFace2 best = null;
            double bestV = wantMax ? double.MinValue : double.MaxValue;
            double[] axis = CylinderAxis(cylinder);
            foreach (IFace2 f in EnumCompFaces(comp))
            {
                if (!(f.GetSurface() is ISurface s) || !s.IsPlane()) continue;
                if (!(s.PlaneParams is double[] p) || p.Length < 6) continue;
                // 法向 [0..2]：与圆柱轴平行（符号任意）
                if (Math.Abs(p[0] * axis[0] + p[1] * axis[1] + p[2] * axis[2]) < 0.9) continue;
                double v = p[3 + axisIdx]; // 根点 [3..5] 在主轴坐标上的值
                if (wantMax ? v > bestV : v < bestV) { bestV = v; best = f; }
            }
            return best;
        }

        /// <summary>圆柱面轴向（单位向量，CylinderParams [3..5]）；非圆柱面返回 +Z。</summary>
        private static double[] CylinderAxis(IFace2 f)
        {
            if (f?.GetSurface() is ISurface s && s.IsCylinder()
                && s.CylinderParams is double[] p && p.Length >= 7)
            {
                return new[] { p[3], p[4], p[5] };
            }
            return new[] { 0.0, 0.0, 1.0 };
        }

        /// <summary>圆柱轴的主轴下标（0=X,1=Y,2=Z）；非圆柱面返回 2。</summary>
        private static int DominantAxisIndex(IFace2 f)
        {
            double[] a = CylinderAxis(f);
            int idx = 2;
            double max = Math.Abs(a[2]);
            if (Math.Abs(a[0]) > max) { idx = 0; max = Math.Abs(a[0]); }
            if (Math.Abs(a[1]) > max) { idx = 1; }
            return idx;
        }

        /// <summary>枚举组件实体全部面（轻化先强制还原，IGetBody 优先）。</summary>
        private static IEnumerable<IFace2> EnumCompFaces(IComponent2 comp)
        {
            if (comp == null) yield break;
            IBody2 body = null;
            try
            {
                int supp = comp.GetSuppression2();
                if (supp == 1 || supp == 4) // Lightweight / FullyLightweight
                {
                    comp.SetSuppression2(2); // swComponentFullyResolved
                }
                try { body = comp.IGetBody(); }
                catch { }
                if (body == null) body = comp.GetBody() as IBody2;
            }
            catch (Exception ex)
            {
                Log.Warn("Cad", "读取组件实体失败：" + ex.Message);
                yield break;
            }
            if (body == null || !(body.GetFaces() is object[] fs)) yield break;
            foreach (object o in fs)
            {
                if (o is IFace2 f) yield return f;
            }
        }

        private static ComponentInfo ToInfo(IComponent2 c)
        {
            var info = new ComponentInfo();
            try { info.Name = c.Name2; } catch { }
            try { info.Path = c.GetPathName(); } catch { }
            try { info.IsFixed = c.IsFixed(); } catch { }
            try
            {
                if (c.Transform2?.ArrayData is double[] t && t.Length >= 12)
                {
                    info.TxMm = t[9] * 1000.0;
                    info.TyMm = t[10] * 1000.0;
                    info.TzMm = t[11] * 1000.0;
                }
            }
            catch { }
            return info;
        }

        private static string SafeCompName(IComponent2 c)
        {
            try { return c.Name2; } catch { return "<组件>"; }
        }

        private int RevisionYear()
        {
            try
            {
                int major = int.Parse(_session.Revision.Split('.')[0]);
                return 1992 + major;
            }
            catch
            {
                return DateTime.Now.Year;
            }
        }

        private static string SafeTitle(IModelDoc2 doc)
        {
            try { return doc.GetTitle(); } catch { return "<未知>"; }
        }
    }
}
