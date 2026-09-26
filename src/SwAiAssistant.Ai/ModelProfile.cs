using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAiAssistant.Ai
{
    /// <summary>探测出的模型协议。</summary>
    public enum ProbeProtocol
    {
        Unknown = 0,
        OpenAiCompatible,
        Ollama
    }

    /// <summary>
    /// 一个模型的能力画像：协议、文本/视觉、上下文长度、本地或云端、来源。
    /// </summary>
    public class ModelProfile
    {
        /// <summary>对应 ModelConfigEntry.Id。</summary>
        public string EntryId { get; set; } = "";
        public string Model { get; set; } = "";
        public ProbeProtocol Protocol { get; set; } = ProbeProtocol.Unknown;
        public bool TextCapable { get; set; } = true;
        public bool VisionCapable { get; set; }
        public int ContextTokens { get; set; } = 8192;
        public bool IsLocal { get; set; }

        /// <summary>画像来源：probed（接口实测）/ manual（用户勾选兜底）/ failed（探测失败）。</summary>
        public string Source { get; set; } = "probed";

        /// <summary>探测失败时的中文原因。</summary>
        public string ProbeError { get; set; }

        /// <summary>缓存时间（Utc）。</summary>
        public DateTime CachedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>界面能力标签，如「文本·视觉·128K·本地」。</summary>
        public string CapabilityTags()
        {
            var tags = new List<string>();
            if (TextCapable) tags.Add("文本");
            if (VisionCapable) tags.Add("视觉");
            if (ContextTokens >= 1024) tags.Add(ContextTokens / 1024 + "K");
            tags.Add(IsLocal ? "本地" : "云端");
            return string.Join("·", tags);
        }
    }

    /// <summary>能力缓存落盘结构（capabilities.json）。</summary>
    public class CapabilityCacheData
    {
        public int Version { get; set; } = 1;
        public List<ModelProfile> Profiles { get; set; } = new List<ModelProfile>();
    }
}
