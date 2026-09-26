using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SwAiAssistant.Planner.Execution
{
    /// <summary>AI 特征元数据记录（对话修改定位 / T16 快照复用）。</summary>
    public sealed class FeatureRecord
    {
        /// <summary>AI 特征名（AI__ 前缀）。</summary>
        public string FeatureName { get; set; } = "";

        /// <summary>特征种类（extrudeBoss / holeWizard / extrudeCut 等，同特征树 Kind）。</summary>
        public string Kind { get; set; } = "";

        /// <summary>关联草图名（无草图的特征为 null）。</summary>
        public string SketchName { get; set; }

        /// <summary>关联尺寸全名列表（如孔径 "D1@草图N"、拉伸深度 "D1@凸台-拉伸N"）。</summary>
        public List<string> DimensionFullNames { get; } = new List<string>();

        /// <summary>创建时间（UTC）。</summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>源指令摘要（用户原始意图一句话）。</summary>
        public string SourceSummary { get; set; } = "";
    }

    /// <summary>
    /// AI 特征注册表：随会话维护「AI 特征名 → 元数据」，支持对话修改与快照（T16 复用）。
    /// 生命周期：由上层会话（TaskPane/对话上下文）持有一个实例，随会话存活；
    ///   测试台每次运行自管实例。
    /// 线程安全：ConcurrentDictionary，执行管线与 UI 线程可并发读写。
    /// </summary>
    public sealed class FeatureRegistry
    {
        /// <summary>AI 特征统一命名前缀（与 PlanExecutor 约定一致）。</summary>
        public const string AiPrefix = "AI__";

        private readonly ConcurrentDictionary<string, FeatureRecord> _records =
            new ConcurrentDictionary<string, FeatureRecord>(StringComparer.Ordinal);

        /// <summary>登记一个 AI 特征（重复登记覆盖旧记录）。</summary>
        public FeatureRecord Register(string featureName, string kind, string sketchName,
            IEnumerable<string> dimensionFullNames, string sourceSummary)
        {
            if (string.IsNullOrWhiteSpace(featureName)) throw new ArgumentNullException(nameof(featureName));
            var rec = new FeatureRecord
            {
                FeatureName = featureName,
                Kind = kind ?? "",
                SketchName = sketchName,
                CreatedAtUtc = DateTime.UtcNow,
                SourceSummary = sourceSummary ?? ""
            };
            if (dimensionFullNames != null)
            {
                rec.DimensionFullNames.AddRange(dimensionFullNames);
            }
            _records[featureName] = rec;
            return rec;
        }

        /// <summary>是否 AI 特征：名以 AI__ 前缀开头，或已登记在册。</summary>
        public bool IsAiFeature(string featureName)
        {
            if (string.IsNullOrWhiteSpace(featureName)) return false;
            return featureName.StartsWith(AiPrefix, StringComparison.Ordinal)
                || _records.ContainsKey(featureName);
        }

        /// <summary>按名查找登记记录；未登记返回 null。</summary>
        public FeatureRecord Find(string featureName)
        {
            if (string.IsNullOrWhiteSpace(featureName)) return null;
            return _records.TryGetValue(featureName, out FeatureRecord rec) ? rec : null;
        }

        /// <summary>全部登记记录（快照用）。</summary>
        public IReadOnlyList<FeatureRecord> All() => _records.Values.ToList();

        /// <summary>移除登记（特征被删除后调用）。</summary>
        public void Remove(string featureName)
        {
            if (string.IsNullOrWhiteSpace(featureName)) return;
            _records.TryRemove(featureName, out _);
        }

        /// <summary>清空全部登记（M4-T19：文档关闭时清理会话，返回清理条数）。</summary>
        public int Clear()
        {
            int n = _records.Count;
            _records.Clear();
            return n;
        }
    }
}
