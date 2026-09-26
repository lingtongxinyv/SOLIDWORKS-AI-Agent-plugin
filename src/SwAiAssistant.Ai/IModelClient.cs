using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SwAiAssistant.Core.Configuration;

namespace SwAiAssistant.Ai
{
    /// <summary>
    /// 一个已配置模型的客户端（OpenAI 兼容或 Ollama 协议）。
    /// 实现类必须线程安全（每个实例内部独占 HttpClient）。
    /// </summary>
    public interface IModelClient : IDisposable
    {
        /// <summary>对应的配置项（不含明文 Key）。</summary>
        ModelConfigEntry Entry { get; }

        /// <summary>列出端点上可用模型名。</summary>
        Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);

        /// <summary>
        /// 对话补全。onToken 非空时以流式增量回调（每次传入新增文本片段）；
        /// 返回完整文本。取消经 ct；超时由实现按配置实现并以 <see cref="LlmErrorKind.Timeout"/> 抛出。
        /// </summary>
        Task<string> ChatAsync(IReadOnlyList<LlmMessage> messages, ChatRequestOptions options,
            Action<string> onToken, CancellationToken ct);
    }
}
