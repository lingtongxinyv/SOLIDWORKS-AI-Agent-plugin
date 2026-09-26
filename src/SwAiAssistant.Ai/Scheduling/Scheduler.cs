using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Logging;

namespace SwAiAssistant.Ai.Scheduling
{
    /// <summary>
    /// 任务调度器：按能力画像 + 健康度为每类任务从「模型列表中已添加且连通未失败」的
    /// 模型中选出有序候选链，依次尝试；全部失败即报错，绝不自行调用列表外的模型
    /// （不再自动追加本机 Ollama 降级候选）。
    /// 路由规则：
    ///  - VisionVerify：仅视觉模型，云端视觉优先，本地视觉兜底；
    ///  - Plan/Edit：云端文本优先（低延迟优先），本地文本兜底；
    ///  - QA/NumberCheck/Fallback：同上；
    ///  - 连通测试/上次调用失败（LastConnectOk=false）的候选跳过；
    ///  - 熔断（连续失败 3 次 60s 内）的候选跳过。
    /// </summary>
    public class Scheduler
    {
        private readonly ConfigService _config;
        private readonly CapabilityProbe _probe;
        private readonly Dictionary<string, ModelHealth> _health =
            new Dictionary<string, ModelHealth>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        /// <summary>无任何可用模型时触发（界面提示去配置）。</summary>
        public event EventHandler<string> NoModelAvailable;

        public Scheduler(ConfigService config = null, CapabilityProbe probe = null)
        {
            _config = config ?? ConfigService.Default;
            _probe = probe ?? new CapabilityProbe(_config);
        }

        public CapabilityProbe Probe => _probe;

        public ModelHealth HealthOf(string entryId)
        {
            lock (_sync)
            {
                if (!_health.TryGetValue(entryId, out var h))
                {
                    h = new ModelHealth();
                    _health[entryId] = h;
                }
                return h;
            }
        }

        /// <summary>
        /// 为一个任务构建有序候选链（entry + profile 对）。
        /// 纯路由逻辑，不发起网络请求；画像缺失时视为未知能力（云端按文本处理）。
        /// </summary>
        public List<(ModelConfigEntry Entry, ModelProfile Profile)> BuildChain(
            ModelTask task, IReadOnlyDictionary<string, ModelProfile> profiles)
        {
            var candidates = new List<(ModelConfigEntry, ModelProfile)>();
            foreach (var entry in _config.EnabledModels())
            {
                profiles.TryGetValue(entry.Id, out var profile);
                if (!IsCapable(task, profile)) continue;
                if (entry.LastConnectOk == false)
                {
                    Log.Info("Ai", $"调度跳过连通未通过的模型：{entry.Name}（可在设置页重新连通测试恢复）");
                    continue;
                }
                if (HealthOf(entry.Id).IsCircuitOpen)
                {
                    Log.Info("Ai", $"调度跳过熔断模型：{entry.Name}");
                    continue;
                }
                candidates.Add((entry, profile));
            }

            // 云端在前、本地在后；同组内连通已通过的优先，再按最近延迟升序（未知延迟排最后）
            return candidates
                .OrderBy(c => c.Item2?.IsLocal == true ? 1 : 0)
                .ThenBy(c => c.Item1.LastConnectOk == true ? 0 : 1)
                .ThenBy(c => HealthOf(c.Item1.Id).LastLatencyMs ?? double.MaxValue)
                .ToList();
        }

        private static bool IsCapable(ModelTask task, ModelProfile p)
        {
            switch (task)
            {
                case ModelTask.VisionVerify:
                    return p?.VisionCapable == true;
                case ModelTask.Plan:
                case ModelTask.Edit:
                case ModelTask.QA:
                case ModelTask.NumberCheck:
                case ModelTask.Fallback:
                    return p == null || p.TextCapable; // 画像缺失给一次机会
                default:
                    return true;
            }
        }

        /// <summary>是否存在可处理该任务的候选（用于 UI 降级提示）。</summary>
        public bool HasCandidate(ModelTask task, IReadOnlyDictionary<string, ModelProfile> profiles)
        {
            return BuildChain(task, profiles).Count > 0;
        }

        /// <summary>
        /// 执行一个 LLM 任务：按候选链逐个尝试，成功即返回；全部失败即报错，
        /// 绝不追加列表外的模型。invoke(entry, client, ct) 由调用方完成实际 Chat 调用。
        /// 每次实际调用的结果会回写模型的连通状态（成功 true / 失败 false）并持久化。
        /// </summary>
        public async Task<T> ExecuteAsync<T>(ModelTask task,
            IReadOnlyDictionary<string, ModelProfile> profiles,
            Func<ModelConfigEntry, IModelClient, CancellationToken, Task<T>> invoke,
            CancellationToken ct)
        {
            var chain = BuildChain(task, profiles);
            var decision = new RouteDecision { Task = task };

            Exception lastError = null;

            for (int i = 0; i < chain.Count; i++)
            {
                var (entry, profile) = chain[i];
                decision.TriedEntries.Add(entry.Name);
                var sw = Stopwatch.StartNew();
                try
                {
                    using (var client = _probe.CreateClient(entry, profile))
                    {
                        var result = await invoke(entry, client, ct).ConfigureAwait(false);
                        sw.Stop();
                        HealthOf(entry.Id).RecordSuccess(sw.Elapsed.TotalMilliseconds);
                        MarkConnectResult(entry, true);
                        decision.SelectedEntryId = entry.Id;
                        decision.SelectedName = entry.Name;
                        Log.Info("Ai", $"任务 {task} 命中模型「{entry.Name}」（{sw.ElapsedMilliseconds} ms，第 {i + 1} 候选）");
                        return result;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    sw.Stop();
                    HealthOf(entry.Id).RecordFailure();
                    MarkConnectResult(entry, false);
                    lastError = ex;
                    Log.Warn("Ai", $"任务 {task} 模型「{entry.Name}」失败：{ex.Message}");
                }
            }

            if (chain.Count == 0)
            {
                try { NoModelAvailable?.Invoke(this, task.ToString()); } catch { /* 忽略 */ }
                throw new LlmException(LlmErrorKind.Unknown,
                    task == ModelTask.VisionVerify
                        ? "没有可用的视觉模型：调度只会使用设置页模型列表中已添加且连通测试通过的模型。请到设置页添加视觉模型、勾选视觉能力或完成连通测试。本次将跳过视觉复核。"
                        : "没有可用模型：调度只会使用设置页模型列表中已添加且连通测试通过的模型。请到设置页添加模型并点「连通测试」。");
            }
            throw new LlmException(LlmErrorKind.Unknown,
                $"全部 {chain.Count} 个候选模型均失败，失败模型已标记为暂不可调度（可在设置页重新连通测试恢复）。最后错误：{lastError?.Message}", lastError);
        }

        /// <summary>
        /// 把实际调用结果回写为模型连通状态并持久化（状态未变化时不重复落盘）。
        /// 写盘异常只记日志，不影响本次任务结果。
        /// </summary>
        private void MarkConnectResult(ModelConfigEntry entry, bool ok)
        {
            try
            {
                if (entry.LastConnectOk == ok) return;
                entry.LastConnectOk = ok;
                entry.LastConnectTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
                _config.Save();
            }
            catch (Exception ex)
            {
                Log.Warn("Ai", $"连通状态回写失败（{entry.Name}）：" + ex.Message);
            }
        }
    }
}
