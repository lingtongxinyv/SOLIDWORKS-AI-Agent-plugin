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
    /// OpenAI 兼容协议客户端：POST {base}/chat/completions（SSE 流式）、GET {base}/models。
    /// BaseUrl 未以 /v1 结尾时自动补 /v1；若 /v1 路径 404 则回退裸路径（兼容 DeepSeek 双形态端点）。
    /// </summary>
    public class OpenAiCompatibleClient : ModelClientBase
    {
        private bool? _useV1Prefix; // null=未探测；404 时自动翻转重试一次

        public OpenAiCompatibleClient(ModelConfigEntry entry, ConfigService config = null, TimeSpan? timeout = null)
            : base(entry, config, timeout)
        {
        }

        private string EndpointBase()
        {
            string b = NormalizedBase();
            // BaseUrl 已含 /v1 时直接用；否则按探测策略补 /v1（默认补，404 后翻转为裸路径）
            if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) || b.Contains("/v1/")) return b;
            return (_useV1Prefix ?? true) ? b + "/v1" : b;
        }

        public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        {
            using (var linked = LinkTimeout(ct))
            {
                var response = await SendWithV1FallbackAsync(
                    ep => new HttpRequestMessage(HttpMethod.Get, ep + "/models"), linked, ct, "列出模型")
                    .ConfigureAwait(false);
                using (response)
                {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var root = JObject.Parse(text);
                    var data = root["data"] as JArray;
                    if (data == null)
                    {
                        throw new LlmException(LlmErrorKind.Protocol,
                            "模型列表响应缺少 data 字段，端点可能不是 OpenAI 兼容协议。");
                    }
                    return data.Select(t => t.Value<string>("id"))
                        .Where(s => !string.IsNullOrEmpty(s)).ToList();
                }
            }
        }

        public override async Task<string> ChatAsync(IReadOnlyList<LlmMessage> messages, ChatRequestOptions options,
            Action<string> onToken, CancellationToken ct)
        {
            options = options ?? new ChatRequestOptions();
            var payload = BuildChatPayload(messages, options);
            using (var linked = LinkTimeout(ct))
            {
                var response = await SendWithV1FallbackAsync(
                    ep =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Post, ep + "/chat/completions");
                        req.Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json");
                        return req;
                    }, linked, ct, "对话补全").ConfigureAwait(false);

                using (response)
                {
                    var sb = new StringBuilder();
                    await ReadStreamLinesAsync(response, line =>
                    {
                        if (!line.StartsWith("data:", StringComparison.Ordinal)) return true;
                        string data = line.Substring(5).Trim();
                        if (data == "[DONE]") return false;
                        var chunk = ParseLine(data);
                        string delta = chunk?["choices"]?[0]?["delta"]?["content"]?.ToString();
                        if (!string.IsNullOrEmpty(delta))
                        {
                            sb.Append(delta);
                            try { onToken?.Invoke(delta); } catch { /* UI 回调异常不打断流 */ }
                        }
                        return true;
                    }, linked.Token).ConfigureAwait(false);
                    return sb.ToString();
                }
            }
        }

        /// <summary>带 /v1 前缀回退的请求：404 时翻转前缀策略重试一次。</summary>
        private async Task<HttpResponseMessage> SendWithV1FallbackAsync(
            Func<string, HttpRequestMessage> factory, CancellationTokenSource linked,
            CancellationToken userCt, string action)
        {
            HttpRequestMessage request = factory(EndpointBase());
            ApplyAuth(request);
            ApplyJsonHeaders(request);
            try
            {
                return await SendCheckedAsync(request, linked, userCt, action).ConfigureAwait(false);
            }
            catch (LlmException ex) when (ex.Kind == LlmErrorKind.NotFound && !EndpointBaseTouched())
            {
                // 翻转 /v1 前缀策略重试一次（DeepSeek 等裸端点）
                _useV1Prefix = !_useV1Prefix.GetValueOrDefault(true);
                request.Dispose();
                HttpRequestMessage retry = factory(EndpointBase());
                ApplyAuth(retry);
                ApplyJsonHeaders(retry);
                return await SendCheckedAsync(retry, linked, userCt, action).ConfigureAwait(false);
            }
        }

        private bool EndpointBaseTouched() => NormalizedBase().Contains("/v1");

        private void ApplyAuth(HttpRequestMessage request)
        {
            string key = ApiKey;
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }
        }

        private object BuildChatPayload(IReadOnlyList<LlmMessage> messages, ChatRequestOptions options)
        {
            // 注意：协议字段含下划线（image_url/max_tokens），必须经 JObject 原样输出，
            // 匿名类型属性会被全局 camelCase 序列化器改名，禁用。
            var msgs = new JArray();
            foreach (var m in messages)
            {
                if (m.ImagesBase64 != null && m.ImagesBase64.Count > 0)
                {
                    var parts = new JArray { new JObject { ["type"] = "text", ["text"] = m.Content ?? "" } };
                    foreach (var b64 in m.ImagesBase64)
                    {
                        parts.Add(new JObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JObject { ["url"] = "data:image/png;base64," + b64 }
                        });
                    }
                    msgs.Add(new JObject { ["role"] = m.Role, ["content"] = parts });
                }
                else
                {
                    msgs.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content ?? "" });
                }
            }

            var payload = new JObject
            {
                ["model"] = Entry.Model,
                ["messages"] = msgs,
                ["stream"] = true
            };
            if (options.JsonMode)
            {
                payload["response_format"] = new JObject { ["type"] = "json_object" };
            }
            if (options.Temperature.HasValue) payload["temperature"] = options.Temperature.Value;
            if (options.MaxTokens.HasValue) payload["max_tokens"] = options.MaxTokens.Value;
            return payload;
        }
    }
}
