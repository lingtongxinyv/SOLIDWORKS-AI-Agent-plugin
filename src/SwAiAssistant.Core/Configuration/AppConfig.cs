using System;
using System.Collections.Generic;

namespace SwAiAssistant.Core.Configuration
{
    /// <summary>模型端点协议类型。Auto 表示由能力探测自动识别。</summary>
    public enum ModelProtocol
    {
        Auto = 0,
        OpenAiCompatible = 1,
        Ollama = 2
    }

    /// <summary>
    /// 单个 AI 模型配置项。API Key 只存 DPAPI 密文（<see cref="ApiKeyProtected"/>），
    /// 任何情况下不落明文、不写日志。
    /// </summary>
    public class ModelConfigEntry
    {
        /// <summary>稳定标识（能力缓存/调度健康度按键关联）。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>用户起的显示名，如「DeepSeek 云端」。</summary>
        public string Name { get; set; } = "";

        /// <summary>端点根地址，如 https://api.deepseek.com 或 http://127.0.0.1:11434。</summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>DPAPI 密文（Base64）；Ollama 本地模型可为空。</summary>
        public string ApiKeyProtected { get; set; } = "";

        /// <summary>模型名，如 deepseek-chat / qwen2.5:14b。</summary>
        public string Model { get; set; } = "";

        /// <summary>协议类型；Auto 时由 CapabilityProbe 实测识别。</summary>
        public ModelProtocol Protocol { get; set; } = ModelProtocol.Auto;

        /// <summary>是否参与调度。</summary>
        public bool Enabled { get; set; } = true;

        // ---- 连通状态：调度只使用列表内已添加且连通未失败的模型 ----

        /// <summary>最近一次连通测试/实际调用结果：null=未测过（首次调用给一次机会）；true=通过；false=失败（不再参与调度，重做连通测试可恢复）。</summary>
        public bool? LastConnectOk { get; set; }

        /// <summary>连通状态更新时间（UTC ISO 8601，展示用）。</summary>
        public string LastConnectTimeUtc { get; set; } = "";

        // ---- 手动能力兜底：探测失败时以用户勾选为准 ----

        /// <summary>手动声明：具备文本对话能力（默认 true，绝大多数模型都有）。</summary>
        public bool ManualTextCapable { get; set; } = true;

        /// <summary>手动声明：具备视觉（图片输入）能力。</summary>
        public bool ManualVisionCapable { get; set; }

        /// <summary>手动声明上下文长度（token），0 = 未指定。</summary>
        public int ManualContextTokens { get; set; }

        public ModelConfigEntry Clone()
        {
            return (ModelConfigEntry)MemberwiseClone();
        }
    }

    /// <summary>
    /// 全局应用配置（%AppData%\SwAiAssistant\config\app.json）。
    /// Version 字段预留迁移：升级时按版本号逐段迁移。
    /// </summary>
    public class AppConfig
    {
        public int Version { get; set; } = 2;

        /// <summary>已配置的模型列表（云端 + 本地 Ollama 混排）。</summary>
        public List<ModelConfigEntry> Models { get; set; } = new List<ModelConfigEntry>();

        /// <summary>单次 LLM 请求超时（秒）。</summary>
        public int RequestTimeoutSeconds { get; set; } = 60;

        /// <summary>强校验体积/包围盒偏差报警阈值（%）。</summary>
        public double VerifyThresholdPct { get; set; } = 5.0;

        /// <summary>极速模式：true 时 AI 计划不弹确认直接执行。</summary>
        public bool FastMode { get; set; }

        /// <summary>本机 Ollama 服务地址。</summary>
        public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434";

        /// <summary>一键拉取推荐：文本模型（T20 评测后最终定版，先配置化）。</summary>
        public string OllamaRecommendTextModel { get; set; } = "qwen2.5:14b";

        /// <summary>一键拉取推荐：视觉模型。</summary>
        public string OllamaRecommendVisionModel { get; set; } = "qwen2.5vl:7b";
    }
}
