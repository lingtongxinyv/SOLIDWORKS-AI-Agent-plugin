using System;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SwAiAssistant.Cad;
using SwAiAssistant.Cad.Documents;
using SwAiAssistant.Cad.Features;
using SwAiAssistant.Cad.Queries;
using SwAiAssistant.Core.Logging;
using SwAiAssistant.Planner.Schema;

namespace SwAiAssistant.Planner.Execution
{
    /// <summary>
    /// 修改意图类型。自然语言理解在 LLM 侧完成，这里只接收结构化意图做确定性路由。
    /// </summary>
    public enum EditIntent
    {
        /// <summary>按尺寸全名改尺寸（如孔径 6→8）。</summary>
        ChangeDimension,

        /// <summary>改特征定义参数（如拉伸深度——本质是 D1@特征 尺寸，走 ChangeDimension 路径）。</summary>
        ChangeFeatureParam,

        /// <summary>追加新特征（增量特征树片段）。</summary>
        AddFeature,

        /// <summary>删除 AI 特征（仅 AI__ 前缀/已登记特征可删，防误删用户特征）。</summary>
        DeleteFeature
    }

    /// <summary>结构化修改请求（EditPlanner 的确定性输入）。</summary>
    public sealed class EditRequest
    {
        /// <summary>修改意图。</summary>
        public EditIntent Intent { get; set; }

        /// <summary>目标特征名（deleteFeature / changeFeatureParam 用）。</summary>
        public string TargetFeatureName { get; set; }

        /// <summary>尺寸全名（changeDimension / changeFeatureParam 用，如 "D1@草图2"）。</summary>
        public string DimensionFullName { get; set; }

        /// <summary>新值（毫米）。</summary>
        public double NewValueMm { get; set; }

        /// <summary>addFeature 时的增量特征树片段。</summary>
        public FeatureTree NewFeatureTree { get; set; }
    }

    /// <summary>
    /// 修改意图路由（T14）：把结构化修改请求映射到确定性 COM 调用。
    /// 红线：不解析自然语言、不执行模型生成代码；LLM 只产出 EditRequest / JSON 特征树。
    /// </summary>
    public sealed class EditPlanner
    {
        private readonly PlanExecutor _executor;
        private readonly DimensionService _dims;
        private readonly FeatureRegistry _registry;
        private readonly DocService _docs;
        private readonly FeatureService _features;

        public EditPlanner(PlanExecutor executor, DimensionService dims, FeatureRegistry registry,
            DocService docs, FeatureService features)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _dims = dims ?? throw new ArgumentNullException(nameof(dims));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _docs = docs ?? throw new ArgumentNullException(nameof(docs));
            _features = features ?? throw new ArgumentNullException(nameof(features));
        }

        /// <summary>应用一条修改请求，返回执行报告（addFeature 直接透传执行管线报告）。</summary>
        public ExecutionReport Apply(IModelDoc2 doc, EditRequest req, CancellationToken ct)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (req == null) throw new ArgumentNullException(nameof(req));
            ct.ThrowIfCancellationRequested();

            switch (req.Intent)
            {
                case EditIntent.ChangeDimension:
                case EditIntent.ChangeFeatureParam:
                    return ApplyDimensionChange(doc, req);

                case EditIntent.DeleteFeature:
                    return ApplyDelete(doc, req);

                case EditIntent.AddFeature:
                {
                    if (req.NewFeatureTree == null)
                    {
                        throw new CadException("addFeature 请求缺少增量特征树（NewFeatureTree）。");
                    }
                    // 增量特征树继续建模在当前活动零件上
                    return _executor.Execute(req.NewFeatureTree, _ => DocChoice.ContinueCurrent, ct);
                }

                default:
                    throw new CadException($"未知修改意图「{req.Intent}」。");
            }
        }

        /// <summary>
        /// 改尺寸 / 改特征参数：changeFeatureParam（如拉伸深度）本质是改特征的 D1 尺寸，
        /// 与 changeDimension 同路径。最小修改语义（TR-14.1）：只写该尺寸 + 一次强制重建，
        /// 不重建非相关特征。
        /// </summary>
        private ExecutionReport ApplyDimensionChange(IModelDoc2 doc, EditRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.DimensionFullName))
            {
                throw new CadException("改尺寸请求缺少尺寸全名（DimensionFullName）。");
            }
            _dims.SetDimensionMm(doc, req.DimensionFullName, req.NewValueMm);
            Log.Info("Planner", $"对话修改·尺寸：「{req.DimensionFullName}」= {req.NewValueMm} mm");
            var report = new ExecutionReport();
            report.DocTitle = _docs.GetTitle(doc);
            report.Notes.Add($"尺寸「{req.DimensionFullName}」已改为 {req.NewValueMm} mm（仅该尺寸驱动特征重建）。");
            return report;
        }

        /// <summary>
        /// 删除 AI 特征：仅当注册表判定为 AI 特征才允许删除（防误删用户特征）。
        /// </summary>
        private ExecutionReport ApplyDelete(IModelDoc2 doc, EditRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.TargetFeatureName))
            {
                throw new CadException("deleteFeature 请求缺少目标特征名（TargetFeatureName）。");
            }
            if (!_registry.IsAiFeature(req.TargetFeatureName))
            {
                throw new CadException(
                    $"拒绝删除「{req.TargetFeatureName}」：仅允许删除 AI 创建的特征（AI__ 前缀或已登记），防误删用户特征。");
            }
            // 删除/重建均经 Cad 层包装（内部 STA 封送），Planner 不直调 COM
            _features.DeleteFeature(doc, req.TargetFeatureName);
            _docs.ForceRebuild(doc, false);
            _registry.Remove(req.TargetFeatureName);
            Log.Info("Planner", $"对话修改·删除：AI 特征「{req.TargetFeatureName}」已删除");
            var report = new ExecutionReport();
            report.DocTitle = _docs.GetTitle(doc);
            report.Notes.Add($"已删除 AI 特征「{req.TargetFeatureName}」。");
            return report;
        }
    }
}
