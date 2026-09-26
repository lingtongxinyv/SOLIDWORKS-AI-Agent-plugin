using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SwAiAssistant.Cad;
using SwAiAssistant.Cad.Documents;
using SwAiAssistant.Cad.Features;
using SwAiAssistant.Cad.Geometry;
using SwAiAssistant.Cad.Materials;
using SwAiAssistant.Cad.Queries;
using SwAiAssistant.Cad.Sketching;
using SwAiAssistant.Core.Logging;
using SwAiAssistant.Planner.Schema;

namespace SwAiAssistant.Planner.Execution
{
    /// <summary>执行进度事件参数（UI 逐步展示）。</summary>
    public class PlanProgressEventArgs : EventArgs
    {
        public int StepIndex { get; set; }
        public int StepCount { get; set; }
        public string StepTitle { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>执行结果报告（供校验闭环与对话展示）。</summary>
    public class ExecutionReport
    {
        public string DocTitle { get; set; } = "";
        public List<string> CreatedFeatures { get; } = new List<string>();
        public double VolumeMm3 { get; set; }
        public double MassKg { get; set; }
        public BoxMm BoundingBox { get; set; }
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// 计划执行管线：把校验通过的 JSON 特征树按序映射到 Cad 原语。
    /// 特征统一 AI__ 前缀命名（供快照/回滚/对话修改定位）。
    /// 全程在调用方 STA 封送内执行（Cad 服务自带封送）。
    /// </summary>
    public class PlanExecutor
    {
        private readonly DocService _docs;
        private readonly SketchService _sketch;
        private readonly FeatureService _features;
        private readonly QueryService _query;
        private readonly GeometryService _geo;
        // 材料服务（M3-T15）：可选注入，未注入时 setMaterial 步骤退回仅记录 note（保持旧行为）
        private readonly MaterialService _materials;
        // AI 特征注册表（T14/T26 接线）：可选注入；注入后每个建成特征自动登记，供对话修改/删除/问答使用
        private readonly FeatureRegistry _registry;

        public event EventHandler<PlanProgressEventArgs> Progress;

        // materials/registry 为可选参数：不破坏既有 5 参构造调用点（SwIaTest 旧代码无需改动）
        public PlanExecutor(DocService docs, SketchService sketch, FeatureService features,
            QueryService query, GeometryService geo, MaterialService materials = null,
            FeatureRegistry registry = null)
        {
            _docs = docs ?? throw new ArgumentNullException(nameof(docs));
            _sketch = sketch ?? throw new ArgumentNullException(nameof(sketch));
            _features = features ?? throw new ArgumentNullException(nameof(features));
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _geo = geo ?? throw new ArgumentNullException(nameof(geo));
            _materials = materials;
            _registry = registry;
        }

        /// <summary>
        /// 执行特征树。askUser 为文档二选一回调（UI 弹窗）；ct 支持取消。
        /// </summary>
        public Task<ExecutionReport> ExecuteAsync(FeatureTree plan,
            Func<DocState, DocChoice> askUser, CancellationToken ct)
        {
            return Task.Run(() => Execute(plan, askUser, ct));
        }

        /// <summary>
        /// 带 AI 修正重试的执行（M4-T18）：执行抛 CadException 时，把 SW 错误文本交给 fixPlan
        /// 修正特征树后整体重试，最多 maxRetries 次（默认 2，即总尝试 ≤3）。
        /// fixPlan(currentPlan, errorText) 返回修正后的特征树；返回 null 表示无法修正，原异常直接抛出。
        /// 修正结果须过 FeatureTreeValidator（未通过视为修正失败，抛原异常）。
        /// 重试经 askUser 重新选文档（测试台传 CreateNew 即在新文档重跑，不残留半成品）。
        /// </summary>
        public ExecutionReport ExecuteWithRetry(FeatureTree plan,
            Func<DocState, DocChoice> askUser, CancellationToken ct,
            Func<FeatureTree, string, FeatureTree> fixPlan, int maxRetries = 2)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (maxRetries < 0) maxRetries = 0;
            var current = plan;
            CadException last = null;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return Execute(current, askUser, ct);
                }
                catch (CadException ex)
                {
                    last = ex;
                    if (fixPlan == null || attempt >= maxRetries) throw;
                    Log.Warn("Planner", $"执行失败（第 {attempt + 1} 次尝试）：{ex.Message}，请求 AI 修正重试…");
                    FeatureTree fixedPlan;
                    try { fixedPlan = fixPlan(current, ex.Message); }
                    catch (Exception fixEx)
                    {
                        Log.Warn("Planner", "AI 修正回调异常：" + fixEx.Message);
                        throw;
                    }
                    if (fixedPlan == null)
                    {
                        Log.Warn("Planner", "AI 无法修正（返回 null），放弃重试。");
                        throw;
                    }
                    var errors = Schema.FeatureTreeValidator.Validate(fixedPlan);
                    if (errors.Count > 0)
                    {
                        Log.Warn("Planner", "AI 修正后的特征树未通过校验：" + errors[0].Message);
                        throw;
                    }
                    current = fixedPlan;
                }
            }
            throw last ?? new CadException("执行失败（未知原因）。");
        }

        public ExecutionReport Execute(FeatureTree plan, Func<DocState, DocChoice> askUser, CancellationToken ct)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var report = new ExecutionReport();

            IModelDoc2 doc = _docs.EnsurePartDocument(askUser);
            report.DocTitle = _docs.GetTitle(doc);
            Log.Info("Planner", $"开始执行特征树：{plan.Steps.Count} 步 → 文档「{report.DocTitle}」");

            // 草图步骤 id → SW 草图名；特征步骤 id → AI__ 特征名（阵列/镜像引用用）
            var sketchNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var featureNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < plan.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = plan.Steps[i];
                ReportProgress(i, plan.Steps.Count, step, "开始");
                try
                {
                    ExecuteStep(doc, step, sketchNames, featureNames, report);
                }
                catch (CadException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new CadException($"步骤 {i + 1}「{step.Title}」执行失败：" + ex.Message, ex);
                }
                ReportProgress(i, plan.Steps.Count, step, "完成");
            }

            // 真实回读
            var mp = _query.GetMassProps(doc);
            report.VolumeMm3 = mp.VolumeMm3;
            report.MassKg = mp.MassKg;
            report.BoundingBox = _query.GetBoundingBoxMm(doc);
            Log.Info("Planner", $"执行完成：体积 {report.VolumeMm3:F1} mm³，包围盒 {report.BoundingBox.SizeX:F1}×{report.BoundingBox.SizeY:F1}×{report.BoundingBox.SizeZ:F1}");
            return report;
        }

        private void ExecuteStep(IModelDoc2 doc, PlanStep step,
            Dictionary<string, string> sketchNames, Dictionary<string, string> featureNames,
            ExecutionReport report)
        {
            switch (step.Kind.ToLowerInvariant())
            {
                case "sketch":
                {
                    string name = CreateSketch(doc, step, report);
                    sketchNames[step.Id] = name;
                    report.CreatedFeatures.Add(name);
                    Log.Info("Planner", $"草图建成：{name}（{step.Sketch.Entities.Count} 个实体，{step.Sketch.Plane} 面）");
                    break;
                }
                case "extrudeboss":
                {
                    string sketchName = ResolveSketch(step, sketchNames);
                    var feat = _features.ExtrudeBossMm(doc, sketchName, step.Extrude.DepthMm ?? 0, step.Extrude.Flip);
                    RegisterFeature(doc, step, feat, featureNames, report, sketchName);
                    break;
                }
                case "extrudecut":
                {
                    string sketchName = ResolveSketch(step, sketchNames);
                    IFeature feat;
                    if (step.Extrude.ThroughAll)
                    {
                        feat = _features.ExtrudeCutThroughAll(doc, sketchName);
                    }
                    else
                    {
                        feat = _features.ExtrudeCutBlindMm(doc, sketchName, step.Extrude.DepthMm ?? 0, step.Extrude.Flip);
                    }
                    RegisterFeature(doc, step, feat, featureNames, report, sketchName);
                    break;
                }
                case "revolveboss":
                {
                    string sketchName = ResolveSketch(step, sketchNames);
                    var feat = _features.RevolveMm(doc, sketchName, step.Revolve?.AngleDeg ?? 360);
                    RegisterFeature(doc, step, feat, featureNames, report, sketchName);
                    break;
                }
                case "revolvecut":
                {
                    string sketchName = ResolveSketch(step, sketchNames);
                    var feat = _features.RevolveCutMm(doc, sketchName, step.Revolve?.AngleDeg ?? 360);
                    RegisterFeature(doc, step, feat, featureNames, report, sketchName);
                    break;
                }
                case "rib":
                {
                    string sketchName = ResolveSketch(step, sketchNames);
                    var feat = _features.RibMm(doc, sketchName, step.Rib.ThicknessMm ?? 0);
                    RegisterFeature(doc, step, feat, featureNames, report, sketchName);
                    break;
                }
                case "draft":
                {
                    var feat = _features.DraftMm(doc, _geo, step.Draft.AngleDeg ?? 0);
                    RegisterFeature(doc, step, feat, featureNames, report);
                    break;
                }
                case "shell":
                {
                    var feat = _features.ShellMm(doc, _geo, step.Shell.ThicknessMm ?? 0,
                        step.Shell.RemoveFace ?? "top");
                    RegisterFeature(doc, step, feat, featureNames, report);
                    break;
                }
                case "linearpattern":
                {
                    string seed = ResolveFeature(step, step.Pattern.SourceStepId, featureNames);
                    var feat = _features.LinearPatternMm(doc, _geo, seed,
                        step.Pattern.Direction ?? "x", step.Pattern.Count ?? 2, step.Pattern.SpacingMm ?? 0);
                    RegisterFeature(doc, step, feat, featureNames, report);
                    break;
                }
                case "circularpattern":
                {
                    string seed = ResolveFeature(step, step.Pattern.SourceStepId, featureNames);
                    var feat = _features.CircularPatternMm(doc, _geo, seed,
                        step.Pattern.Count ?? 2, step.Pattern.TotalAngleDeg ?? 360);
                    RegisterFeature(doc, step, feat, featureNames, report);
                    break;
                }
                case "mirror":
                {
                    string seed = ResolveFeature(step, step.Mirror.SourceStepId, featureNames);
                    var feat = _features.MirrorFeature(doc, ParsePlane(step.Mirror.Plane), seed);
                    RegisterFeature(doc, step, feat, featureNames, report);
                    break;
                }
                case "fillet":
                {
                    var target = ParseEdgeTarget(step.Fillet.Target);
                    var feat = _features.FilletMm(doc, _geo, target, step.Fillet.RadiusMm ?? 0);
                    RegisterFeature(doc, step, feat, featureNames, report);
                    break;
                }
                case "chamfer":
                {
                    var target = ParseEdgeTarget(step.Chamfer.Target);
                    var feat = _features.ChamferMm(doc, _geo, target, step.Chamfer.DistanceMm ?? 0);
                    RegisterFeature(doc, step, feat, featureNames, report);
                    break;
                }
                case "holewizard":
                {
                    var positions = step.Hole.Positions.Select(p => new PointMm(p.X, p.Y)).ToList();
                    bool wizard = _features.HoleMm(doc, _sketch, _query, positions,
                        step.Hole.DiameterMm ?? 0, step.Hole.ThroughAll, step.Hole.DepthMm ?? 0,
                        step.Hole.CboreDiameterMm, step.Hole.CboreDepthMm, out string holeNote);
                    if (!string.IsNullOrEmpty(holeNote))
                    {
                        report.Notes.Add(holeNote);
                    }
                    if (step.Hole.Threaded)
                    {
                        // 0.2.0 能力边界（review 中-4）：螺纹孔按攻丝底孔光孔创建，不生成螺纹/装饰线，显式提示
                        string tn = $"螺纹孔「{step.Hole.ThreadSpec}」已按 φ{step.Hole.DiameterMm:0.###} mm 攻丝底孔创建光孔，"
                            + "0.2.0 不自动攻丝/不加螺纹装饰线，请在 SolidWorks 中手工补充螺纹。";
                        report.Notes.Add(tn);
                        Log.Warn("Planner", tn);
                    }
                    string name = AiName(step);
                    report.CreatedFeatures.Add(name + (wizard ? "（异型孔向导）" : "（降级切除）"));
                    try { _registry?.Register(name, "holeWizard", null, null, step.Title); }
                    catch (Exception ex) { Log.Warn("Planner", "注册表登记孔特征失败（不阻断执行）：" + ex.Message); }
                    break;
                }
                case "setmaterial":
                {
                    string want = step.SetMaterial.MaterialName;
                    if (_materials == null)
                    {
                        // 未注入材料服务（旧调用方式）：仅记录计划标注
                        report.Notes.Add("材料服务未注入，材料「" + want + "」未赋值。");
                        Log.Info("Planner", "setMaterial 步骤记录（材料服务未注入）：" + want);
                        break;
                    }
                    var mat = _materials.Guess(want);
                    if (mat == null)
                    {
                        report.Notes.Add($"材料「{want}」未匹配 GB 材料库，保持默认。");
                        Log.Warn("Planner", "材料未匹配 GB 材料库：" + want);
                    }
                    else
                    {
                        _materials.ApplyMaterial(doc, mat);
                        report.Notes.Add($"已赋材料 {mat.Name}（密度 {mat.DensityKgM3:0} kg/m³）。");
                        Log.Info("Planner", $"已赋材料 {mat.Name}（{want} → {mat.Name}）");
                    }
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"步骤类型「{step.Kind}」暂未接入执行管线（M3 里程碑补齐）。请简化需求或分步建模。");
            }
        }

        private string CreateSketch(IModelDoc2 doc, PlanStep step, ExecutionReport report)
        {
            var plane = ParsePlane(step.Sketch.Plane);
            _sketch.SelectPlane(doc, plane);
            _sketch.BeginSketch(doc, true);
            try
            {
                foreach (var e in step.Sketch.Entities)
                {
                    CreateEntity(doc, e);
                }
            }
            finally
            {
                _sketch.EndSketch(doc);
            }
            string name = "AI__" + (string.IsNullOrWhiteSpace(step.Title) ? step.Id : Sanitize(step.Title));
            string swName = _query.GetLatestSketchName(doc);
            _features.RenameFeatureByName(doc, swName, name);

            // 0.2.0 能力边界（review 中-4）：约束/标注 Schema 已支持，执行器暂不施加，显式提示不静默丢弃
            int constraintCount = step.Sketch.Constraints?.Count ?? 0;
            int dimensionCount = step.Sketch.Dimensions?.Count ?? 0;
            if (constraintCount > 0 || dimensionCount > 0)
            {
                string note = $"草图「{name}」含 {constraintCount} 条约束、{dimensionCount} 个标注声明，"
                    + "0.2.0 暂不自动施加（几何已按给定坐标精确创建）；如需约束/标注请在 SolidWorks 中手工补充。";
                report.Notes.Add(note);
                Log.Warn("Planner", note);
            }
            return name;
        }

        private void CreateEntity(IModelDoc2 doc, SketchEntity e)
        {
            switch (e.Type.ToLowerInvariant())
            {
                case "rectcenter":
                {
                    double hw = e.Width.Value / 2.0, hh = e.Height.Value / 2.0;
                    _sketch.CreateCornerRectangleMm(doc,
                        e.Cx.Value - hw, e.Cy.Value - hh, e.Cx.Value + hw, e.Cy.Value + hh);
                    break;
                }
                case "rectcorner":
                    _sketch.CreateCornerRectangleMm(doc, e.X1.Value, e.Y1.Value, e.X2.Value, e.Y2.Value);
                    break;
                case "circle":
                {
                    double r = e.Radius ?? (e.Diameter ?? 0) / 2.0;
                    _sketch.CreateCircleMm(doc, e.Cx.Value, e.Cy.Value, r);
                    break;
                }
                case "arc":
                    // 圆心 + 起终点 + 方向（ccw 默认逆时针）
                    _sketch.CreateArcMm(doc, e.Cx.Value, e.Cy.Value,
                        e.X1.Value, e.Y1.Value, e.X2.Value, e.Y2.Value, e.Ccw ?? true);
                    break;
                case "slot":
                    _sketch.CreateSlotMm(doc, e.Cx.Value, e.Cy.Value,
                        e.Length.Value, e.Width.Value, e.Angle ?? 0);
                    break;
                case "polygon":
                    _sketch.CreatePolygonMm(doc, e.Cx.Value, e.Cy.Value,
                        e.CircumDiameter.Value / 2.0, e.Sides.Value, e.Angle ?? 0);
                    break;
                case "polyline":
                {
                    var pts = e.Points.Select(p => new Point2Mm(p.X, p.Y)).ToList();
                    _sketch.CreatePolylineMm(doc, pts, e.Closed == true);
                    break;
                }
                case "centerline":
                    _sketch.CreateCenterLineMm(doc, e.X1.Value, e.Y1.Value, e.X2.Value, e.Y2.Value);
                    break;
                default:
                    throw new NotSupportedException($"未知草图实体「{e.Type}」。");
            }
        }

        private string ResolveSketch(PlanStep step, Dictionary<string, string> sketchNames)
        {
            if (step.SketchId == null || !sketchNames.TryGetValue(step.SketchId, out string name))
            {
                throw new CadException($"特征步骤「{step.Title}」引用的草图 {step.SketchId} 未建成。");
            }
            return name;
        }

        /// <summary>解析阵列/镜像引用的被变换特征名（须为之前已建成的特征步骤）。</summary>
        private static string ResolveFeature(PlanStep step, string sourceStepId,
            Dictionary<string, string> featureNames)
        {
            if (string.IsNullOrWhiteSpace(sourceStepId)
                || !featureNames.TryGetValue(sourceStepId, out string name))
            {
                throw new CadException($"步骤「{step.Title}」引用的特征步骤 {sourceStepId ?? "<空>"} 未建成。");
            }
            return name;
        }

        /// <summary>特征统一命名 AI__ 前缀并登记（供后续阵列/镜像步骤引用 + 会话注册表）。</summary>
        private void RegisterFeature(IModelDoc2 doc, PlanStep step, IFeature feat,
            Dictionary<string, string> featureNames, ExecutionReport report, string sketchName = null)
        {
            string name = AiName(step);
            _features.Rename(feat, name);
            featureNames[step.Id] = name;
            report.CreatedFeatures.Add(name);
            // T26 接线：自动登记到会话注册表（尺寸全名在对话修改时由 DimensionService 实时枚举解析）
            try { _registry?.Register(name, step.Kind, sketchName, null, step.Title); }
            catch (Exception ex) { Log.Warn("Planner", "注册表登记失败（不阻断执行）：" + ex.Message); }
        }

        private static PlaneKind ParsePlane(string plane)
        {
            switch ((plane ?? "top").ToLowerInvariant())
            {
                case "front": return PlaneKind.Front;
                case "right": return PlaneKind.Right;
                default: return PlaneKind.Top;
            }
        }

        private static EdgeTarget ParseEdgeTarget(string target)
        {
            switch ((target ?? "allEdges").ToLowerInvariant())
            {
                case "verticaledges": return EdgeTarget.VerticalEdges;
                case "topedges": return EdgeTarget.TopEdges;
                case "bottomedges": return EdgeTarget.BottomEdges;
                default: return EdgeTarget.AllEdges;
            }
        }

        private static string AiName(PlanStep step)
        {
            return "AI__" + (string.IsNullOrWhiteSpace(step.Title) ? step.Id : Sanitize(step.Title));
        }

        private static string Sanitize(string title)
        {
            // SW 特征名不允许 \ / : * ? " < > |
            var chars = title.Trim().Select(c => "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray();
            return new string(chars);
        }

        private void ReportProgress(int index, int count, PlanStep step, string state)
        {
            try
            {
                Progress?.Invoke(this, new PlanProgressEventArgs
                {
                    StepIndex = index,
                    StepCount = count,
                    StepTitle = step.Title,
                    Message = $"步骤 {index + 1}/{count}「{step.Title}」{state}"
                });
            }
            catch (Exception ex)
            {
                Log.Warn("Planner", "进度事件订阅异常：" + ex.Message);
            }
        }
    }
}
