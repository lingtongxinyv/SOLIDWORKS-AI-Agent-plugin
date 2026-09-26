using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAiAssistant.Ai.Scheduling
{
    /// <summary>任务类型（路由依据）。</summary>
    public enum ModelTask
    {
        /// <summary>建模规划（需较强文本+JSON 能力）。</summary>
        Plan,
        /// <summary>对话式修改规划。</summary>
        Edit,
        /// <summary>数据问答。</summary>
        QA,
        /// <summary>截图视觉复核（必须视觉能力）。</summary>
        VisionVerify,
        /// <summary>数值复核/纠错等轻量文本任务。</summary>
        NumberCheck,
        /// <summary>兜底：任意可用文本模型。</summary>
        Fallback
    }

    /// <summary>单个模型的运行时健康度（进程内，不落盘）。</summary>
    public class ModelHealth
    {
        public int ConsecutiveFailures { get; private set; }
        public double? LastLatencyMs { get; private set; }
        public DateTime LastFailureUtc { get; private set; } = DateTime.MinValue;

        /// <summary>连续失败 3 次进入熔断，60 秒冷却后再试。</summary>
        public bool IsCircuitOpen
        {
            get
            {
                if (ConsecutiveFailures < 3) return false;
                return (DateTime.UtcNow - LastFailureUtc).TotalSeconds < 60;
            }
        }

        public void RecordSuccess(double latencyMs)
        {
            ConsecutiveFailures = 0;
            LastLatencyMs = latencyMs;
        }

        public void RecordFailure()
        {
            ConsecutiveFailures++;
            LastFailureUtc = DateTime.UtcNow;
        }
    }

    /// <summary>一次调度的路由决策记录（日志/界面可解释）。</summary>
    public class RouteDecision
    {
        public ModelTask Task { get; set; }
        public List<string> TriedEntries { get; } = new List<string>();
        public string SelectedEntryId { get; set; }
        public string SelectedName { get; set; }
        public string Note { get; set; } = "";
    }
}
