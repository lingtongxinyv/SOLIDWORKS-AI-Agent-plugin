using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Logging;
using SwAiAssistant.Core.Web;

namespace SwAiAssistant.Ai
{
    /// <summary>
    /// 模型客户端基类：HttpClient 生命周期、超时链接取消、HTTP 错误→中文 LlmException 映射。
    /// </summary>
    public abstract class ModelClientBase : IModelClient
    {
        private readonly ConfigService _config;
        private readonly TimeSpan _timeout;
        private HttpClient _http;

        protected ModelClientBase(ModelConfigEntry entry, ConfigService config, TimeSpan? timeout = null)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            _config = config ?? ConfigService.Default;
            _timeout = timeout ?? TimeSpan.FromSeconds(Math.Max(5, _config.Current.RequestTimeoutSeconds));
        }

        public ModelConfigEntry Entry { get; }

        /// <inheritdoc/>
        public abstract Task<System.Collections.Generic.IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);

        /// <inheritdoc/>
        public abstract Task<string> ChatAsync(
            System.Collections.Generic.IReadOnlyList<LlmMessage> messages,
            ChatRequestOptions options, Action<string> onToken, CancellationToken ct);

        protected ConfigService Config => _config;

        /// <summary>明文 Key（只在拼请求头瞬间存在内存；禁止写日志）。</summary>
        protected string ApiKey => _config.GetApiKey(Entry);

        protected HttpClient Http
        {
            get
            {
                if (_http == null)
                {
                    _http = HttpClientFactory.Create(Timeout.InfiniteTimeSpan);
                }
                return _http;
            }
        }

        /// <summary>规范化 BaseUrl：去掉末尾斜杠；空/非法地址抛中文异常。</summary>
        protected string NormalizedBase()
        {
            string url = (Entry.BaseUrl ?? "").Trim().TrimEnd('/');
            if (url.Length == 0)
            {
                throw new LlmException(LlmErrorKind.Network, $"模型「{Entry.Name}」未填写 Base URL。");
            }
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
            }
            return url;
        }

        /// <summary>把配置超时与用户取消链接成一个 ct；disposing 后抛 Timeout 或 OperationCanceled。</summary>
        protected CancellationTokenSource LinkTimeout(CancellationToken ct)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_timeout);
            return linked;
        }

        protected static bool WasTimeout(CancellationTokenSource linked, CancellationToken userCt, Exception ex)
        {
            if (userCt.IsCancellationRequested) return false;
            if (linked != null && linked.IsCancellationRequested) return true;
            return ex is TaskCanceledException || ex is OperationCanceledException;
        }

        /// <summary>
        /// 发送请求并按状态码映射中文异常；读取响应体摘要（截断 300 字符）供日志。
        /// </summary>
        protected async Task<HttpResponseMessage> SendCheckedAsync(HttpRequestMessage request,
            CancellationTokenSource linked, CancellationToken userCt, string action)
        {
            try
            {
                var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                string body = await ReadBodySnippetAsync(response).ConfigureAwait(false);
                var ex = MapHttpError(request, response, body, action);
                Log.Warn("Ai", $"{action} 失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}；{body}");
                response.Dispose();
                throw ex;
            }
            catch (LlmException)
            {
                throw;
            }
            catch (Exception ex) when (WasTimeout(linked, userCt, ex))
            {
                if (userCt.IsCancellationRequested) throw new OperationCanceledException("已取消。", ex, userCt);
                throw new LlmException(LlmErrorKind.Timeout,
                    $"{action}超时（{_timeout.TotalSeconds:F0} 秒无响应）：{Entry.Name}，请检查网络或调大超时。", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new LlmException(LlmErrorKind.Network,
                    $"{action}无法连接 {SafeUrl(request.RequestUri)}：{ExPlain(ex)}。请检查地址与网络。", ex);
            }
        }

        private LlmException MapHttpError(HttpRequestMessage request, HttpResponseMessage response,
            string body, string action)
        {
            int code = (int)response.StatusCode;
            string url = SafeUrl(request.RequestUri);
            switch (code)
            {
                case 401:
                case 403:
                    return new LlmException(LlmErrorKind.Auth,
                        $"{action}鉴权失败（HTTP {code}）：{Entry.Name} 的 API Key 错误或无权限，请在设置页检查。");
                case 404:
                    string hint404 = Snippet(body, 120);
                    return new LlmException(LlmErrorKind.NotFound,
                        $"{action}找不到资源（HTTP 404）：{url}。请检查 Base URL 路径与模型名「{Entry.Model}」。"
                        + (hint404.Length > 0 ? "服务端返回：" + hint404 : ""));
                case 400:
                case 422:
                    return new LlmException(LlmErrorKind.Protocol,
                        $"{action}请求被拒（HTTP {code}）：{Snippet(body, 120)}");
                case 429:
                    return new LlmException(LlmErrorKind.Server,
                        $"{action}被限流（HTTP 429）：{Entry.Name}，请稍后重试或更换模型。");
                default:
                    if (code >= 500)
                    {
                        return new LlmException(LlmErrorKind.Server,
                            $"{action}服务端错误（HTTP {code}）：{Entry.Name}，{Snippet(body, 120)}");
                    }
                    return new LlmException(LlmErrorKind.Unknown,
                        $"{action}失败（HTTP {code}）：{Snippet(body, 120)}");
            }
        }

        private static async Task<string> ReadBodySnippetAsync(HttpResponseMessage response)
        {
            try
            {
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return Snippet(text, 300);
            }
            catch
            {
                return "";
            }
        }

        protected static string Snippet(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        protected static string SafeUrl(Uri uri) => uri == null ? "" : uri.ToString();

        private static string ExPlain(HttpRequestException ex)
        {
            // net48 的 InnerException 常为 WinHttpException/WebException，取其消息更直白
            Exception cur = ex;
            while (cur.InnerException is HttpRequestException || cur.InnerException is System.Net.WebException)
            {
                cur = cur.InnerException;
            }
            return cur.Message;
        }

        /// <summary>通用流式逐行读取：每行交给 lineHandler（返回 false 停止）。</summary>
        protected static async Task ReadStreamLinesAsync(HttpResponseMessage response,
            Func<string, bool> lineHandler, CancellationToken ct)
        {
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                try
                {
                    while (!reader.EndOfStream)
                    {
                        ct.ThrowIfCancellationRequested();
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        if (line.Length == 0) continue;
                        if (!lineHandler(line)) break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (LlmException) { throw; }
                catch (Exception ex)
                {
                    // .NET Framework 上请求超时回收连接或服务端中断后，流读取表现为
                    // IOException（「读取操作失败，请参见内部异常。」），归一为可操作提示。
                    if (IsTimeoutLike(ex))
                    {
                        throw new LlmException(LlmErrorKind.Timeout,
                            "响应读取中断（多为请求超时所致）：思考型模型响应较慢，可在设置页把「请求超时」调大（如 300 秒）后重试，或换响应更快的模型。", ex);
                    }
                    throw new LlmException(LlmErrorKind.Network,
                        "读取响应流失败：连接被网络或服务端中断。" + InnerText(ex), ex);
                }
            }
        }

        /// <summary>判断异常链中是否含超时/连接回收类根因（HttpClient 超时回收连接后流读取的内层异常）。</summary>
        private static bool IsTimeoutLike(Exception ex)
        {
            for (Exception cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is OperationCanceledException) return true;
                if (cur is System.Net.WebException w)
                {
                    if (w.Status == System.Net.WebExceptionStatus.RequestCanceled
                        || w.Status == System.Net.WebExceptionStatus.Timeout
                        || w.Status == System.Net.WebExceptionStatus.ConnectionClosed)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>取异常链最内层消息（供文案携带真实原因）。</summary>
        private static string InnerText(Exception ex)
        {
            Exception cur = ex;
            while (cur.InnerException != null) cur = cur.InnerException;
            return cur == ex ? "" : "内部异常：" + cur.Message;
        }

        protected static JObject ParseLine(string line)
        {
            try
            {
                return JObject.Parse(line);
            }
            catch
            {
                return null;
            }
        }

        protected void ApplyJsonHeaders(HttpRequestMessage request)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public void Dispose()
        {
            try { _http?.Dispose(); } catch { /* 忽略 */ }
            _http = null;
        }
    }
}
