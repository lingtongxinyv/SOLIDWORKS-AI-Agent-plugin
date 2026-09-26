using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SwAiAssistant.Ai;
using SwAiAssistant.Core.Configuration;
using Xunit;

namespace SwAiAssistant.Ai.Tests
{
    /// <summary>轻量 HttpListener 模拟端点：按路径返回预置响应。</summary>
    public sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        public FakeServer()
        {
            int port = 40000 + Math.Abs(Environment.TickCount % 20000);
            while (true)
            {
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException)
                {
                    port++;
                }
            }
            BaseUrl = $"http://127.0.0.1:{port}";
            Task.Run(Loop);
        }

        public string BaseUrl { get; }

        /// <summary>路径 → (状态码, 响应体)。支持函数动态生成（读取请求体）。</summary>
        public Func<string, string, (int, string)> Handler { get; set; }

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch { break; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string body = "";
                        if (ctx.Request.HasEntityBody)
                        {
                            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                            {
                                body = await reader.ReadToEndAsync().ConfigureAwait(false);
                            }
                        }
                        var (code, text) = Handler(ctx.Request.Url.AbsolutePath, body);
                        ctx.Response.StatusCode = code;
                        byte[] bytes = Encoding.UTF8.GetBytes(text);
                        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                        ctx.Response.Close();
                    }
                    catch { /* 忽略 */ }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { /* 忽略 */ }
        }
    }

    public class OpenAiCompatibleClientTests
    {
        private static ModelConfigEntry EntryOf(string url, string model = "test-model") =>
            new ModelConfigEntry
            {
                Name = "fake",
                BaseUrl = url,
                Model = model,
                Protocol = ModelProtocol.OpenAiCompatible
            };

        private static ConfigService TempConfig()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SwAiAssistantTests", Guid.NewGuid().ToString("N"));
            return new ConfigService("app.json", dir);
        }

        [Fact]
        public async Task ListModels_ParsesDataArray()
        {
            using (var server = new FakeServer())
            {
                server.Handler = (path, _) => path == "/v1/models"
                    ? (200, "{\"data\":[{\"id\":\"m1\"},{\"id\":\"m2\"}]}")
                    : (404, "{}");
                using (var client = new OpenAiCompatibleClient(EntryOf(server.BaseUrl), TempConfig()))
                {
                    var models = await client.ListModelsAsync(CancellationToken.None);
                    Assert.Equal(new[] { "m1", "m2" }, models.ToArray());
                }
            }
        }

        [Fact]
        public async Task Chat_StreamsSseDeltas()
        {
            using (var server = new FakeServer())
            {
                server.Handler = (path, body) =>
                {
                    if (path != "/v1/chat/completions") return (404, "{}");
                    return (200, "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n" +
                                 "data: {\"choices\":[{\"delta\":{\"content\":\"，世界\"}}]}\n" +
                                 "data: [DONE]\n");
                };
                var sb = new StringBuilder();
                using (var client = new OpenAiCompatibleClient(EntryOf(server.BaseUrl), TempConfig()))
                {
                    string full = await client.ChatAsync(
                        new[] { LlmMessage.User("hi") }, new ChatRequestOptions(),
                        d => sb.Append(d), CancellationToken.None);
                    Assert.Equal("你好，世界", full);
                    Assert.Equal("你好，世界", sb.ToString());
                }
            }
        }

        [Fact]
        public async Task Chat_V1Fallback_On404_RetriesBarePath()
        {
            using (var server = new FakeServer())
            {
                int calls = 0;
                server.Handler = (path, _) =>
                {
                    if (path == "/v1/chat/completions") { calls++; return (404, "{}"); }
                    if (path == "/chat/completions")
                    {
                        calls++;
                        return (200, "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\ndata: [DONE]\n");
                    }
                    return (404, "{}");
                };
                using (var client = new OpenAiCompatibleClient(EntryOf(server.BaseUrl), TempConfig()))
                {
                    string full = await client.ChatAsync(
                        new[] { LlmMessage.User("hi") }, new ChatRequestOptions(), null, CancellationToken.None);
                    Assert.Equal("ok", full);
                    Assert.Equal(2, calls);
                }
            }
        }

        [Fact]
        public async Task Auth_401_MappedToChineseMessage()
        {
            using (var server = new FakeServer())
            {
                server.Handler = (_, __) => (401, "{\"error\":\"invalid key\"}");
                using (var client = new OpenAiCompatibleClient(EntryOf(server.BaseUrl), TempConfig()))
                {
                    var ex = await Assert.ThrowsAsync<LlmException>(
                        () => client.ListModelsAsync(CancellationToken.None));
                    Assert.Equal(LlmErrorKind.Auth, ex.Kind);
                    Assert.Contains("Key", ex.Message);
                }
            }
        }

        [Fact]
        public async Task JsonMode_SetsResponseFormat()
        {
            using (var server = new FakeServer())
            {
                string seenBody = null;
                server.Handler = (path, body) =>
                {
                    seenBody = body;
                    return (200, "data: {\"choices\":[{\"delta\":{\"content\":\"{}\"}}]}\ndata: [DONE]\n");
                };
                using (var client = new OpenAiCompatibleClient(EntryOf(server.BaseUrl), TempConfig()))
                {
                    await client.ChatAsync(new[] { LlmMessage.User("x") },
                        new ChatRequestOptions { JsonMode = true }, null, CancellationToken.None);
                    Assert.Contains("json_object", seenBody);
                }
            }
        }
    }

    public class OllamaClientTests
    {
        private static ModelConfigEntry EntryOf(string url, string model = "qwen2.5:14b") =>
            new ModelConfigEntry
            {
                Name = "ollama",
                BaseUrl = url,
                Model = model,
                Protocol = ModelProtocol.Ollama
            };

        private static ConfigService TempConfig()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SwAiAssistantTests", Guid.NewGuid().ToString("N"));
            return new ConfigService("app.json", dir);
        }

        [Fact]
        public async Task ListModels_ParsesTags()
        {
            using (var server = new FakeServer())
            {
                server.Handler = (path, _) => path == "/api/tags"
                    ? (200, "{\"models\":[{\"name\":\"qwen2.5:14b\"},{\"name\":\"qwen2.5vl:7b\"}]}")
                    : (404, "{}");
                using (var client = new OllamaClient(EntryOf(server.BaseUrl), TempConfig()))
                {
                    var models = await client.ListModelsAsync(CancellationToken.None);
                    Assert.Equal(new[] { "qwen2.5:14b", "qwen2.5vl:7b" }, models.ToArray());
                }
            }
        }

        [Fact]
        public async Task Chat_StreamsNdjson()
        {
            using (var server = new FakeServer())
            {
                server.Handler = (path, body) =>
                {
                    Assert.Equal("/api/chat", path);
                    Assert.Contains("\"format\":\"json\"", body);
                    return (200, "{\"message\":{\"content\":\"甲\"},\"done\":false}\n" +
                                 "{\"message\":{\"content\":\"乙\"},\"done\":true}\n");
                };
                var sb = new StringBuilder();
                using (var client = new OllamaClient(EntryOf(server.BaseUrl), TempConfig()))
                {
                    string full = await client.ChatAsync(
                        new[] { LlmMessage.User("hi") }, new ChatRequestOptions { JsonMode = true },
                        d => sb.Append(d), CancellationToken.None);
                    Assert.Equal("甲乙", full);
                    Assert.Equal("甲乙", sb.ToString());
                }
            }
        }

        [Fact]
        public async Task Show_ParsesContextLength()
        {
            using (var server = new FakeServer())
            {
                server.Handler = (path, _) => path == "/api/show"
                    ? (200, "{\"model_info\":{\"qwen2.context_length\":32768,\"other\":1}}")
                    : (404, "{}");
                using (var client = new OllamaClient(EntryOf(server.BaseUrl), TempConfig()))
                {
                    var show = await client.ShowModelAsync("qwen2.5:14b", CancellationToken.None);
                    int? ctx = CapabilityProbe.ParseOllamaContextLength(show);
                    Assert.Equal(32768, ctx);
                }
            }
        }

        [Fact]
        public async Task Pull_ReportsProgressUntilSuccess()
        {
            using (var server = new FakeServer())
            {
                server.Handler = (path, _) =>
                {
                    Assert.Equal("/api/pull", path);
                    return (200,
                        "{\"status\":\"pulling\",\"total\":100,\"completed\":40}\n" +
                        "{\"status\":\"pulling\",\"total\":100,\"completed\":90}\n" +
                        "{\"status\":\"success\"}\n");
                };
                int lastPct = -1;
                string lastStatus = null;
                using (var client = new OllamaClient(EntryOf(server.BaseUrl), TempConfig()))
                {
                    await client.PullAsync("qwen2.5:14b", (p, s) => { lastPct = p; lastStatus = s; },
                        CancellationToken.None);
                }
                Assert.Equal("success", lastStatus);
            }
        }

        [Fact]
        public async Task IsAlive_FalseWhenDown()
        {
            // 未监听端口必然连接拒绝
            bool alive = await OllamaClient.IsAliveAsync("http://127.0.0.1:59999",
                TimeSpan.FromSeconds(2));
            Assert.False(alive);
        }
    }

    public class CapabilityProbeUnitTests
    {
        [Theory]
        [InlineData("gpt-4o", true)]
        [InlineData("qwen2.5vl:7b", true)]
        [InlineData("Qwen2-VL-72B", true)]
        [InlineData("llava:13b", true)]
        [InlineData("deepseek-vl2", true)]
        [InlineData("deepseek-chat", false)]
        [InlineData("qwen2.5:14b", false)]
        [InlineData("gpt-3.5-turbo", false)]
        [InlineData(null, false)]
        public void ClassifyVision_ByName(string model, bool expected)
        {
            Assert.Equal(expected, CapabilityProbe.ClassifyVision(model));
        }

        [Theory]
        [InlineData("gpt-4o", 128000)]
        [InlineData("deepseek-chat", 64000)]
        [InlineData("qwen2.5:14b", 32768)]
        [InlineData("unknown-xyz", 8192)]
        public void GuessContextTokens_ByName(string model, int expected)
        {
            Assert.Equal(expected, CapabilityProbe.GuessContextTokens(model));
        }

        [Fact]
        public async Task Probe_OllamaProfile_FromEndpoints()
        {
            using (var server = new FakeServer())
            {
                server.Handler = (path, _) =>
                {
                    switch (path)
                    {
                        case "/api/tags": return (200, "{\"models\":[{\"name\":\"qwen2.5vl:7b\"}]}");
                        case "/api/show": return (200, "{\"model_info\":{\"qwen2.context_length\":32768}}");
                        default: return (404, "{}");
                    }
                };
                string dir = Path.Combine(Path.GetTempPath(), "SwAiAssistantTests", Guid.NewGuid().ToString("N"));
                var config = new ConfigService("app.json", dir);
                var probe = new CapabilityProbe(config, dir);
                var entry = new ModelConfigEntry
                {
                    Name = "本机", BaseUrl = server.BaseUrl, Model = "qwen2.5vl:7b",
                    Protocol = ModelProtocol.Auto
                };

                var profile = await probe.ProbeAsync(entry, CancellationToken.None);

                Assert.Equal(ProbeProtocol.Ollama, profile.Protocol);
                Assert.True(profile.VisionCapable);
                Assert.True(profile.TextCapable);
                Assert.True(profile.IsLocal);
                Assert.Equal(32768, profile.ContextTokens);
                Assert.Equal("probed", profile.Source);
            }
        }

        [Fact]
        public async Task Probe_FallsBackToManual_OnFailure()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SwAiAssistantTests", Guid.NewGuid().ToString("N"));
            var config = new ConfigService("app.json", dir);
            config.Current.RequestTimeoutSeconds = 5;
            var probe = new CapabilityProbe(config, dir);
            var entry = new ModelConfigEntry
            {
                Name = "坏的", BaseUrl = "http://127.0.0.1:59999", Model = "m",
                Protocol = ModelProtocol.Auto,
                ManualVisionCapable = true, ManualContextTokens = 16000
            };

            var profile = await probe.ProbeAsync(entry, CancellationToken.None);

            Assert.Equal("manual", profile.Source);
            Assert.True(profile.VisionCapable);
            Assert.Equal(16000, profile.ContextTokens);
            Assert.False(string.IsNullOrEmpty(profile.ProbeError));
        }

        [Fact]
        public async Task Probe_UsesCache_WhenNotForced()
        {
            using (var server = new FakeServer())
            {
                int tagCalls = 0;
                server.Handler = (path, _) =>
                {
                    if (path == "/api/tags") { tagCalls++; return (200, "{\"models\":[]}"); }
                    return (404, "{}");
                };
                string dir = Path.Combine(Path.GetTempPath(), "SwAiAssistantTests", Guid.NewGuid().ToString("N"));
                var config = new ConfigService("app.json", dir);
                var probe = new CapabilityProbe(config, dir);
                var entry = new ModelConfigEntry
                {
                    Name = "本机", BaseUrl = server.BaseUrl, Model = "m",
                    Protocol = ModelProtocol.Ollama
                };

                await probe.ProbeAsync(entry, CancellationToken.None);
                await probe.ProbeAsync(entry, CancellationToken.None); // 第二次走缓存

                Assert.Equal(1, tagCalls);
            }
        }
    }
}
