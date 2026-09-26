using System;
using System.Net;
using System.Net.Http;

namespace SwAiAssistant.Core.Web
{
    /// <summary>
    /// HttpClient 工厂：统一超时；对本机地址（Ollama）绕过系统代理，
    /// 避免企业代理环境下 127.0.0.1 请求失败。
    /// </summary>
    public static class HttpClientFactory
    {
        public static HttpClient Create(TimeSpan? timeout = null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(60)
            };
            return client;
        }

        /// <summary>是否为本机地址（用于代理绕过判断）。</summary>
        public static bool IsLocalhost(Uri uri)
        {
            if (uri == null) return false;
            if (uri.IsLoopback) return true;
            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
        }
    }
}
