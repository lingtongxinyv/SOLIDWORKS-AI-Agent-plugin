using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Text;

namespace SwAiAssistant.Ai
{
    /// <summary>
    /// Ollama 本机协议客户端：/api/tags、/api/chat（NDJSON 流式，images 字段）、
    /// /api/pull（流式进度）、/api/show（模型详情含上下文长度）、服务存活探测。
    /// </summary>
    public class OllamaClient : ModelClientBase
    {
        public OllamaClient(ModelConfigEntry entry, ConfigService config = null, TimeSpan? timeout = null)
            : base(entry, config, timeout)
        {
        }

        // ---- 服务级静态方法（不依赖某个模型配置） ----

        /// <summary>探测 Ollama 服务是否存活（GET / 返回 "Ollama is running"）。</summary>
        public static async Task<bool> IsAliveAsync(string baseUrl, TimeSpan? timeout = null)
        {
            try
            {
                using (var client = Core.Web.HttpClientFactory.Create(timeout ?? TimeSpan.FromSeconds(3)))
                {
                    string url = (baseUrl ?? "").Trim().TrimEnd('/') + "/";
                    using (var response = await client.GetAsync(url).ConfigureAwait(false))
                    {
                        return response.IsSuccessStatusCode;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>GET /api/tags 列出本机已安装模型。</summary>
        public static async Task<IReadOnlyList<string>> ListInstalledAsync(string baseUrl,
            ConfigService config = null, CancellationToken ct = default(CancellationToken))
        {
            var probe = new ModelConfigEntry { Name = "Ollama", BaseUrl = baseUrl, Model = "" };
            using (var client = new OllamaClient(probe, config, TimeSpan.FromSeconds(10)))
            {
                return await client.ListModelsAsync(ct).ConfigureAwait(false);
            }
        }

        // ---- 实例方法 ----

        public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        {
            using (var linked = LinkTimeout(ct))
            {
                var request = new HttpRequestMessage(HttpMethod.Get, NormalizedBase() + "/api/tags");
                ApplyJsonHeaders(request);
                var response = await SendCheckedAsync(request, linked, ct, "列出 Ollama 模型").ConfigureAwait(false);
                using (response)
                {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var root = JObject.Parse(text);
                    var models = root["models"] as JArray ?? new JArray();
                    return models.Select(t => t.Value<string>("name"))
                        .Where(s => !string.IsNullOrEmpty(s)).ToList();
                }
            }
        }

        public override async Task<string> ChatAsync(IReadOnlyList<LlmMessage> messages, ChatRequestOptions options,
            Action<string> onToken, CancellationToken ct)
        {
            options = options ?? new ChatRequestOptions();
            var msgs = messages.Select(m =>
            {
                var o = new Dictionary<string, object>
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content ?? ""
                };
                if (m.ImagesBase64 != null && m.ImagesBase64.Count > 0)
                {
                    o["images"] = m.ImagesBase64.ToArray();
                }
                return (object)o;
            }).ToArray();

            var payload = new Dictionary<string, object>
            {
                ["model"] = Entry.Model,
                ["messages"] = msgs,
                ["stream"] = true
            };
            if (options.JsonMode) payload["format"] = "json";
            var opts = new Dictionary<string, object>();
            if (options.Temperature.HasValue) opts["temperature"] = options.Temperature.Value;
            if (options.MaxTokens.HasValue) opts["num_predict"] = options.MaxTokens.Value;
            if (options.ContextTokens.HasValue) opts["num_ctx"] = options.ContextTokens.Value;
            if (opts.Count > 0) payload["options"] = opts;

            using (var linked = LinkTimeout(ct))
            {
                var request = new HttpRequestMessage(HttpMethod.Post, NormalizedBase() + "/api/chat")
                {
                    Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json")
                };
                ApplyJsonHeaders(request);
                var response = await SendCheckedAsync(request, linked, ct, "Ollama 对话补全").ConfigureAwait(false);
                using (response)
                {
                    var sb = new StringBuilder();
                    await ReadStreamLinesAsync(response, line =>
                    {
                        var chunk = ParseLine(line);
                        string delta = chunk?["message"]?["content"]?.ToString();
                        if (!string.IsNullOrEmpty(delta))
                        {
                            sb.Append(delta);
                            try { onToken?.Invoke(delta); } catch { /* UI 回调异常不打断流 */ }
                        }
                        bool done = chunk?["done"]?.ToObject<bool>() == true;
                        return !done;
                    }, linked.Token).ConfigureAwait(false);
                    return sb.ToString();
                }
            }
        }

        /// <summary>POST /api/show 取模型详情；返回原始 JSON（上下文长度由 CapabilityProbe 解析）。</summary>
        public async Task<JObject> ShowModelAsync(string modelName, CancellationToken ct)
        {
            using (var linked = LinkTimeout(ct))
            {
                var request = new HttpRequestMessage(HttpMethod.Post, NormalizedBase() + "/api/show")
                {
                    Content = new StringContent(Json.Serialize(new { model = modelName }),
                        Encoding.UTF8, "application/json")
                };
                ApplyJsonHeaders(request);
                var response = await SendCheckedAsync(request, linked, ct, "查询模型详情").ConfigureAwait(false);
                using (response)
                {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return JObject.Parse(text);
                }
            }
        }

        /// <summary>
        /// POST /api/pull 流式拉取模型。progress(percent 0-100, statusText)；可经 ct 取消。
        /// </summary>
        public async Task PullAsync(string modelName, Action<int, string> progress, CancellationToken ct)
        {
            // 拉取耗时长，不走请求超时；只受用户取消控制
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var request = new HttpRequestMessage(HttpMethod.Post, NormalizedBase() + "/api/pull")
                {
                    Content = new StringContent(Json.Serialize(new { name = modelName, stream = true }),
                        Encoding.UTF8, "application/json")
                };
                ApplyJsonHeaders(request);
                var response = await SendCheckedAsync(request, linked, ct, "拉取模型 " + modelName)
                    .ConfigureAwait(false);
                using (response)
                {
                    await ReadStreamLinesAsync(response, line =>
                    {
                        var chunk = ParseLine(line);
                        if (chunk == null) return true;
                        string status = chunk["status"]?.ToString() ?? "";
                        long total = chunk["total"]?.ToObject<long>() ?? 0;
                        long completed = chunk["completed"]?.ToObject<long>() ?? 0;
                        int pct = total > 0 ? (int)Math.Min(100, completed * 100L / total) : 0;
                        try { progress?.Invoke(pct, status); } catch { /* 忽略 */ }
                        return !string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);
                    }, linked.Token).ConfigureAwait(false);
                }
            }
        }
    }
}
