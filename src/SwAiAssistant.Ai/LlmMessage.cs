using System.Collections.Generic;

namespace SwAiAssistant.Ai
{
    /// <summary>一条对话消息。ImagesBase64 非空时走视觉协议。</summary>
    public class LlmMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
        public List<string> ImagesBase64 { get; set; }

        public static LlmMessage System(string content) => new LlmMessage { Role = "system", Content = content };
        public static LlmMessage User(string content) => new LlmMessage { Role = "user", Content = content };
        public static LlmMessage Assistant(string content) => new LlmMessage { Role = "assistant", Content = content };

        public static LlmMessage UserVision(string content, params string[] imagesBase64)
        {
            return new LlmMessage
            {
                Role = "user",
                Content = content,
                ImagesBase64 = new List<string>(imagesBase64 ?? new string[0])
            };
        }
    }

    /// <summary>一次 Chat 请求的可调参数。</summary>
    public class ChatRequestOptions
    {
        /// <summary>要求模型输出严格 JSON（OpenAI response_format / Ollama format=json）。</summary>
        public bool JsonMode { get; set; } = true;

        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }

        /// <summary>
        /// 上下文窗口大小（token）。仅对 Ollama 生效（映射为 options.num_ctx），
        /// 用于覆盖模型默认上下文（视觉模型默认为 4096，图片+长提示词易超限）。
        /// null 表示用模型默认值。
        /// </summary>
        public int? ContextTokens { get; set; }
    }
}
