using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwAiAssistant.Ai;
using SwAiAssistant.Ai.Scheduling;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Logging;
using SwAiAssistant.Planner.Prompts;
using SwAiAssistant.Planner.Schema;

namespace SwAiAssistant.Planner
{
    /// <summary>
    /// 规划服务：组装领域 Prompt + 文档上下文 + 多轮历史 → 经 Scheduler 调模型 →
    /// 解析 JSON → Schema 校验；非法输出带错误定位反馈模型纠正（最多 2 次）。
    /// </summary>
    public class PlannerService
    {
        private const int MaxCorrectionRounds = 2;

        private readonly Scheduler _scheduler;
        private readonly ConfigService _config;
        private readonly List<LlmMessage> _history = new List<LlmMessage>();

        public PlannerService(Scheduler scheduler, ConfigService config = null)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _config = config ?? ConfigService.Default;
        }

        /// <summary>多轮对话历史（用户/助手交替，不含 system）。</summary>
        public IReadOnlyList<LlmMessage> History => _history;

        public void ClearHistory() => _history.Clear();

        /// <summary>
        /// 发起一次规划/对话请求。docContext 为当前文档状态摘要（可空）。
        /// onStream 为流式增量回调（UI 线程自行封送）。
        /// </summary>
        public async Task<PlannerResponse> RequestAsync(string userText, string docContext,
            IReadOnlyDictionary<string, ModelProfile> profiles,
            Action<string> onStream, CancellationToken ct)
        {
            var messages = new List<LlmMessage> { LlmMessage.System(DomainSystemPrompt.Build()) };
            if (!string.IsNullOrEmpty(docContext))
            {
                messages.Add(LlmMessage.System(docContext));
            }
            messages.AddRange(_history);
            messages.Add(LlmMessage.User(userText));

            string raw = null;
            PlannerResponse parsed = null;
            string lastError = null;

            for (int round = 0; round <= MaxCorrectionRounds; round++)
            {
                int currentRound = round;
                raw = await _scheduler.ExecuteAsync(ModelTask.Plan, profiles,
                    (entry, client, c) =>
                    {
                        Log.Info("Planner", $"规划请求 → {entry.Name}（第 {currentRound + 1} 轮）");
                        return client.ChatAsync(messages, new ChatRequestOptions
                        {
                            JsonMode = true,
                            Temperature = 0.2
                        }, onStream, c);
                    }, ct).ConfigureAwait(false);

                Log.Info("Planner", "模型原始输出（截断 800）：" + (raw ?? "").Replace("\r", " ").Replace("\n", " ").Substring(0, Math.Min(800, (raw ?? "").Length)));

                if (PlanJsonParser.TryParse(raw, out parsed, out lastError))
                {
                    foreach (var w in parsed.Warnings)
                    {
                        Log.Warn("Planner", "模型输出值内安全告警：" + w);
                    }
                    _history.Add(LlmMessage.User(userText));
                    _history.Add(LlmMessage.Assistant(raw));
                    TrimHistory();
                    return parsed;
                }

                Log.Warn("Planner", $"第 {round + 1} 轮输出被拒：{lastError}");
                if (round < MaxCorrectionRounds)
                {
                    // 带错误定位让模型纠正
                    messages.Add(LlmMessage.Assistant(raw ?? "<空输出>"));
                    messages.Add(LlmMessage.User(
                        "你的输出未通过校验，错误如下：\n" + lastError +
                        "\n请修正后重新只输出完整 JSON（不要代码、不要解释）。"));
                }
            }

            throw new LlmException(LlmErrorKind.Protocol,
                "模型连续 " + (MaxCorrectionRounds + 1) + " 次未输出合法 JSON 特征树。最后错误：\n" + lastError +
                "\n建议：换个更强的模型，或在设置页降低任务复杂度。");
        }

        /// <summary>
        /// 执行失败后的自动重规划（ExecuteWithRetry 的 fixPlan 回调用，FR-15）：
        /// 把失败特征树 JSON 与 SolidWorks 报错回传模型，要求保持设计意图修正后只输出新 plan。
        /// 修正结果仍过 PlanJsonParser + FeatureTreeValidator；任何环节失败返回 null（由执行器重抛原错误）。
        /// </summary>
        public async Task<FeatureTree> FixPlanAsync(FeatureTree failedPlan, string solidWorksError,
            IReadOnlyDictionary<string, ModelProfile> profiles, CancellationToken ct)
        {
            if (failedPlan == null) return null;
            string failedJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                failedPlan, Newtonsoft.Json.Formatting.None, SwAiAssistant.Core.Text.Json.Settings);

            var messages = new List<LlmMessage>
            {
                LlmMessage.System(DomainSystemPrompt.Build()),
                LlmMessage.User(
                    "在 SolidWorks 中执行下面这个特征树时报错：\n" + (solidWorksError ?? "<未知错误>") +
                    "\n\n请在保持原设计意图（零件用途、总体尺寸、孔径孔位等）不变的前提下修正特征树，"
                    + "可调整草图实体写法、基准面、深度/切除方式等以规避该错误。"
                    + "只输出修正后的完整 JSON（{\"type\":\"plan\",\"plan\":{...}} 格式），不要解释、不要代码块。"
                    + "\n\n原特征树 JSON：\n" + failedJson)
            };

            for (int round = 0; round <= MaxCorrectionRounds; round++)
            {
                int currentRound = round;
                string raw = await _scheduler.ExecuteAsync(ModelTask.Plan, profiles,
                    (entry, client, c) =>
                    {
                        Log.Info("Planner", $"执行失败重规划 → {entry.Name}（第 {currentRound + 1} 轮）");
                        return client.ChatAsync(messages, new ChatRequestOptions
                        {
                            JsonMode = true,
                            Temperature = 0.2
                        }, null, c);
                    }, ct).ConfigureAwait(false);

                if (PlanJsonParser.TryParse(raw, out var parsed, out string parseError)
                    && parsed.Type == PlannerResponseType.Plan && parsed.Plan != null)
                {
                    foreach (var w in parsed.Warnings) Log.Warn("Planner", "重规划值内安全告警：" + w);
                    var errors = Schema.FeatureTreeValidator.Validate(parsed.Plan);
                    if (errors.Count == 0)
                    {
                        Log.Info("Planner", "重规划特征树通过校验，交由执行器重试");
                        return parsed.Plan;
                    }
                    parseError = string.Join("；", errors);
                }
                Log.Warn("Planner", $"重规划第 {round + 1} 轮被拒：{parseError}");
                if (round < MaxCorrectionRounds)
                {
                    messages.Add(LlmMessage.Assistant(raw ?? "<空输出>"));
                    messages.Add(LlmMessage.User(
                        "修正后的输出未通过校验，错误：\n" + parseError +
                        "\n请修正后重新只输出完整 JSON。"));
                }
            }
            Log.Warn("Planner", "重规划连续失败，放弃自动修复");
            return null;
        }

        /// <summary>历史保留最近 12 条（约 6 轮），防止上下文膨胀。</summary>
        private void TrimHistory()
        {
            const int max = 12;
            if (_history.Count > max)
            {
                _history.RemoveRange(0, _history.Count - max);
            }
        }
    }
}
