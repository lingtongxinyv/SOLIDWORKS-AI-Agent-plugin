using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Logging;

namespace SwAiAssistant.Ai
{
    /// <summary>
    /// 能力自动探测：协议识别 → 模型清单校验 → 视觉/上下文画像 → 手动勾选兜底合并 → 缓存。
    /// 纯函数部分（<see cref="ClassifyVision"/>、<see cref="GuessContextTokens"/>、
    /// <see cref="ParseOllamaContextLength"/>）可单测。
    /// </summary>
    public class CapabilityProbe
    {
        /// <summary>视觉模型名特征库（小写匹配）。</summary>
        private static readonly string[] VisionKeywords =
        {
            "vision", "-vl", "vl:", "gpt-4o", "gpt-4-vision", "llava", "pixtral",
            "minicpm-v", "glm-4v", "qwen-vl", "internvl", "moondream", "bakllava",
            "cogvlm", "yi-vl", "claude", "gemini", "qwen2.5vl", "qwen2-vl", "qwen3-vl"
        };

        private readonly ConfigService _config;
        private readonly Core.Configuration.JsonConfigStore<CapabilityCacheData> _cache;

        public CapabilityProbe(ConfigService config = null, string cacheDirectory = null)
        {
            _config = config ?? ConfigService.Default;
            _cache = new Core.Configuration.JsonConfigStore<CapabilityCacheData>(
                "capabilities.json", cacheDirectory);
        }

        /// <summary>读缓存（不联网）；无缓存返回 null。</summary>
        public ModelProfile GetCached(string entryId, string model)
        {
            return _cache.Current.Profiles.FirstOrDefault(p =>
                p.EntryId == entryId && p.Model == model);
        }

        /// <summary>使某配置项的缓存失效（配置被修改后调用）。</summary>
        public void Invalidate(string entryId)
        {
            _cache.Mutate(d => d.Profiles.RemoveAll(p => p.EntryId == entryId));
        }

        /// <summary>
        /// 完整探测一个配置项：识别协议 → 列模型 → 画像；失败时回退手动勾选画像并记录原因。
        /// </summary>
        public async Task<ModelProfile> ProbeAsync(ModelConfigEntry entry, CancellationToken ct,
            bool forceRefresh = false)
        {
            if (!forceRefresh)
            {
                var cached = GetCached(entry.Id, entry.Model);
                if (cached != null) return cached;
            }

            ModelProfile profile = null;
            try
            {
                profile = await ProbeOnlineAsync(entry, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Ai", $"能力探测失败：{entry.Name}（{ex.Message}）");
                profile = ManualProfile(entry, ex.Message);
            }

            _cache.Mutate(d =>
            {
                d.Profiles.RemoveAll(p => p.EntryId == entry.Id && p.Model == entry.Model);
                d.Profiles.Add(profile);
            });
            return profile;
        }

        private async Task<ModelProfile> ProbeOnlineAsync(ModelConfigEntry entry, CancellationToken ct)
        {
            var protocol = await DetectProtocolAsync(entry, ct).ConfigureAwait(false);
            if (protocol == ProbeProtocol.Unknown)
            {
                throw new LlmException(LlmErrorKind.Network,
                    "两种协议均无法连接，请检查 Base URL 与网络。");
            }

            var profile = new ModelProfile
            {
                EntryId = entry.Id,
                Model = entry.Model,
                Protocol = protocol,
                IsLocal = Core.Web.HttpClientFactory.IsLocalhost(new Uri(Normalize(entry.BaseUrl))),
                Source = "probed",
                CachedAtUtc = DateTime.UtcNow
            };

            using (var client = CreateClient(entry, protocol))
            {
                var models = await client.ListModelsAsync(ct).ConfigureAwait(false);
                profile.TextCapable = true;

                if (protocol == ProbeProtocol.Ollama)
                {
                    // Ollama：/api/show 取真实上下文长度；视觉靠名称特征
                    try
                    {
                        var ollama = (OllamaClient)client;
                        var show = await ollama.ShowModelAsync(entry.Model, ct).ConfigureAwait(false);
                        profile.ContextTokens = ParseOllamaContextLength(show)
                            ?? GuessContextTokens(entry.Model);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Ai", $"Ollama /api/show 失败（{entry.Model}）：{ex.Message}");
                        profile.ContextTokens = GuessContextTokens(entry.Model);
                    }
                }
                else
                {
                    profile.ContextTokens = GuessContextTokens(entry.Model);
                    // 云端：若清单为空不排除可用（部分端点不开放 /models）
                    if (models.Count > 0 && !string.IsNullOrEmpty(entry.Model)
                        && !models.Contains(entry.Model))
                    {
                        Log.Warn("Ai", $"端点模型清单中未见「{entry.Model}」（共 {models.Count} 个），仍按可用处理。");
                    }
                }
            }

            profile.VisionCapable = ClassifyVision(entry.Model);
            ApplyManualOverrides(entry, profile);
            return profile;
        }

        /// <summary>协议识别：先按配置；Auto 时 Ollama /api/tags 优先，其次 OpenAI /models。</summary>
        public async Task<ProbeProtocol> DetectProtocolAsync(ModelConfigEntry entry, CancellationToken ct)
        {
            switch (entry.Protocol)
            {
                case ModelProtocol.Ollama: return ProbeProtocol.Ollama;
                case ModelProtocol.OpenAiCompatible: return ProbeProtocol.OpenAiCompatible;
            }

            // Auto：本机地址优先试 Ollama；云端地址优先试 OpenAI，但两组都会尝试
            bool local = Core.Web.HttpClientFactory.IsLocalhost(new Uri(Normalize(entry.BaseUrl)));
            var order = local
                ? new[] { ProbeProtocol.Ollama, ProbeProtocol.OpenAiCompatible }
                : new[] { ProbeProtocol.OpenAiCompatible, ProbeProtocol.Ollama };

            foreach (var proto in order)
            {
                try
                {
                    using (var client = CreateClient(entry, proto))
                    {
                        await client.ListModelsAsync(ct).ConfigureAwait(false);
                        return proto;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 换下一个协议 */ }
            }
            return ProbeProtocol.Unknown;
        }

        /// <summary>按画像创建客户端（调度器/设置页共用入口）。</summary>
        public IModelClient CreateClient(ModelConfigEntry entry, ProbeProtocol protocol)
        {
            return protocol == ProbeProtocol.Ollama
                ? (IModelClient)new OllamaClient(entry, _config)
                : new OpenAiCompatibleClient(entry, _config);
        }

        /// <summary>按配置项声明/画像协议创建客户端。</summary>
        public IModelClient CreateClient(ModelConfigEntry entry, ModelProfile profile = null)
        {
            var proto = profile?.Protocol ?? ToProbeProtocol(entry.Protocol);
            return CreateClient(entry, proto == ProbeProtocol.Unknown ? ProbeProtocol.OpenAiCompatible : proto);
        }

        public static ProbeProtocol ToProbeProtocol(ModelProtocol p)
        {
            switch (p)
            {
                case ModelProtocol.Ollama: return ProbeProtocol.Ollama;
                case ModelProtocol.OpenAiCompatible: return ProbeProtocol.OpenAiCompatible;
                default: return ProbeProtocol.Unknown;
            }
        }

        private static string Normalize(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) return "http://127.0.0.1/";
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
            return url;
        }

        /// <summary>视觉能力名称特征判定（纯函数）。</summary>
        public static bool ClassifyVision(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            string n = modelName.ToLowerInvariant();
            return VisionKeywords.Any(k => n.Contains(k));
        }

        /// <summary>上下文长度名称启发（纯函数；Ollama 优先用 /api/show 实测值）。</summary>
        public static int GuessContextTokens(string modelName)
        {
            string n = (modelName ?? "").ToLowerInvariant();
            if (n.Contains("gpt-4o") || n.Contains("gpt-4.1") || n.Contains("gpt-4-turbo")) return 128000;
            if (n.Contains("gpt-3.5")) return 16385;
            if (n.Contains("deepseek")) return 64000;
            if (n.Contains("kimi") || n.Contains("moonshot")) return 131072;
            if (n.Contains("qwen") && (n.Contains("turbo") || n.Contains("plus") || n.Contains("max"))) return 131072;
            if (n.Contains("qwen")) return 32768;
            if (n.Contains("glm-4")) return 131072;
            if (n.Contains("claude")) return 200000;
            if (n.Contains("gemini")) return 1048576;
            if (n.Contains("llama3") || n.Contains("llama-3")) return 8192;
            if (n.Contains("mistral") || n.Contains("mixtral")) return 32768;
            return 8192;
        }

        /// <summary>从 /api/show 响应解析上下文长度（model_info 中 *context_length 键）。</summary>
        public static int? ParseOllamaContextLength(JObject showResponse)
        {
            var info = showResponse?["model_info"] as JObject;
            if (info == null) return null;
            foreach (var prop in info.Properties())
            {
                if (prop.Name.EndsWith("context_length", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(prop.Value?.ToString(), out int v) && v > 0) return v;
                }
            }
            // 部分版本放在 details / parameters 里无结构化字段，放弃精确值
            return null;
        }

        /// <summary>探测失败时以手动勾选生成兜底画像。</summary>
        private static ModelProfile ManualProfile(ModelConfigEntry entry, string error)
        {
            var p = new ModelProfile
            {
                EntryId = entry.Id,
                Model = entry.Model,
                Protocol = ToProbeProtocol(entry.Protocol),
                TextCapable = entry.ManualTextCapable,
                VisionCapable = entry.ManualVisionCapable,
                ContextTokens = entry.ManualContextTokens > 0 ? entry.ManualContextTokens : 8192,
                IsLocal = Core.Web.HttpClientFactory.IsLocalhost(new Uri(Normalize(entry.BaseUrl))),
                Source = entry.ManualTextCapable || entry.ManualVisionCapable ? "manual" : "failed",
                ProbeError = error,
                CachedAtUtc = DateTime.UtcNow
            };
            return p;
        }

        /// <summary>手动勾选始终可补开能力（探测为否但用户声明为是时取用户值）。</summary>
        private static void ApplyManualOverrides(ModelConfigEntry entry, ModelProfile profile)
        {
            if (entry.ManualVisionCapable && !profile.VisionCapable)
            {
                profile.VisionCapable = true;
            }
            if (!entry.ManualTextCapable)
            {
                profile.TextCapable = false;
            }
            if (entry.ManualContextTokens > 0)
            {
                profile.ContextTokens = entry.ManualContextTokens;
            }
        }
    }
}
