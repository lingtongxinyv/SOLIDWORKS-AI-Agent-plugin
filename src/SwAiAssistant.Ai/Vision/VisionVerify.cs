using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwAiAssistant.Ai.Scheduling;

namespace SwAiAssistant.Ai.Vision
{
    /// <summary>
    /// 视觉复核（M4-T18）：把多视角截图 + 设计意图摘要送视觉模型审查
    /// （孔位/孔数、基准面方向、漏特征/多余特征），返回中文结论文本。
    /// 经 Scheduler 的 VisionVerify 任务链选模型（仅 VisionCapable 候选，云端优先本地兜底）；
    /// 无视觉模型时由调用方按 HasVisionCandidate=false 走降级说明（纯文本模型自动跳过视觉项）。
    /// </summary>
    public sealed class VisionVerify
    {
        private readonly Scheduler _scheduler;

        public VisionVerify(Scheduler scheduler)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        /// <summary>是否存在视觉候选模型（UI 降级提示用）。</summary>
        public bool HasVisionCandidate(IReadOnlyDictionary<string, ModelProfile> profiles)
        {
            return _scheduler.HasCandidate(ModelTask.VisionVerify, profiles);
        }

        /// <summary>
        /// 送截图复核。planSummary 为设计意图（零件名/步骤/关键尺寸一句话）；
        /// pngPaths 为截图文件列表（建议等轴测+三视图）。返回模型结论文本。
        /// 无视觉模型/全部失败抛 LlmException（调用方捕获后记降级说明）。
        /// </summary>
        public async Task<string> ReviewAsync(string planSummary, IList<string> pngPaths,
            IReadOnlyDictionary<string, ModelProfile> profiles, CancellationToken ct)
        {
            if (pngPaths == null || pngPaths.Count == 0)
            {
                throw new ArgumentException("截图列表不能为空。", nameof(pngPaths));
            }
            var images = new List<string>();
            foreach (string path in pngPaths.Where(File.Exists))
            {
                images.Add(Convert.ToBase64String(File.ReadAllBytes(path)));
            }
            if (images.Count == 0)
            {
                throw new IOException("截图文件全部缺失，无法视觉复核。");
            }

            var messages = new List<LlmMessage>
            {
                LlmMessage.System(
                    "你是 SolidWorks 建模质检员。根据零件截图核对实物与设计意图是否一致，"
                    + "重点检查：①孔的数量与位置；②特征方向/基准面是否正确；③明显的漏特征或多余特征；"
                    + "④整体形状比例是否合理。截图依次为等轴测/前视/上视/右视（可能不足四张）。"
                    + "用中文回答，格式：第一行「结论：通过/存疑/不通过」，随后逐条列出发现的问题（无问题写「未见明显异常」），"
                    + "最后一行一句话建议。不要臆测截图中看不到的内容。"),
                LlmMessage.UserVision("设计意图：" + (planSummary ?? "（未提供）"), images.ToArray())
            };
            var options = new ChatRequestOptions { JsonMode = false, Temperature = 0.1, MaxTokens = 800, ContextTokens = 8192 };

            string reply = await _scheduler.ExecuteAsync(ModelTask.VisionVerify, profiles,
                (entry, client, c) => client.ChatAsync(messages, options, null, c), ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(reply) ? "（视觉模型无回复）" : reply.Trim();
        }
    }
}
