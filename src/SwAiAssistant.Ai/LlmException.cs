using System;

namespace SwAiAssistant.Ai
{
    /// <summary>LLM 调用失败的分类（界面据此给出中文原因与建议）。</summary>
    public enum LlmErrorKind
    {
        Unknown = 0,
        /// <summary>网络不可达/DNS/连接拒绝。</summary>
        Network,
        /// <summary>401/403：Key 错误或无权限。</summary>
        Auth,
        /// <summary>请求超时。</summary>
        Timeout,
        /// <summary>404：端点路径或模型名不存在。</summary>
        NotFound,
        /// <summary>400/422：请求参数或协议不被接受（含模型不支持 JSON 模式等）。</summary>
        Protocol,
        /// <summary>服务端 5xx 或限流 429。</summary>
        Server
    }

    /// <summary>LLM 调用异常：中文消息 + 分类 + 可选原始摘要（进日志，不进 UI）。</summary>
    public class LlmException : Exception
    {
        public LlmErrorKind Kind { get; }

        public LlmException(LlmErrorKind kind, string message, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
        }
    }
}
