using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Logging;

namespace SwAiAssistant.Ai
{
    /// <summary>
    /// Ollama 本机服务管理：存活探测、一键修复（清残留进程 + 拉起 serve）、
    /// 一键拉取推荐模型（流式进度、可取消）。
    /// </summary>
    public class OllamaServiceManager
    {
        private readonly ConfigService _config;

        public OllamaServiceManager(ConfigService config = null)
        {
            _config = config ?? ConfigService.Default;
        }

        public string BaseUrl => _config.Current.OllamaBaseUrl;

        /// <summary>服务是否存活。</summary>
        public Task<bool> IsAliveAsync() => OllamaClient.IsAliveAsync(BaseUrl);

        /// <summary>定位 ollama.exe：PATH → 常见安装目录。找不到返回 null。</summary>
        public string FindOllamaExe()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Ollama", "ollama.exe"),
                @"D:\Apps\ollama\ollama.exe",
                @"C:\Program Files\Ollama\ollama.exe",
                @"D:\Program Files\Ollama\ollama.exe"
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            // PATH 查找
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string p = Path.Combine(dir.Trim(), "ollama.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* 忽略 */ }
            return null;
        }

        /// <summary>
        /// 一键修复：若已存活直接返回；否则结束残留 ollama 进程 → 拉起 ollama serve →
        /// 最多等待 30s 直到存活。progress 回报中文步骤。
        /// </summary>
        public async Task<bool> RepairAsync(Action<string> progress, CancellationToken ct)
        {
            if (await IsAliveAsync().ConfigureAwait(false))
            {
                progress?.Invoke("Ollama 服务正常，无需修复。");
                return true;
            }

            progress?.Invoke("正在结束残留的 ollama 进程…");
            Log.Info("Ollama", "一键修复：结束残留进程");
            foreach (var name in new[] { "ollama", "ollama app", "ollama-app" })
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }
                foreach (var p in procs)
                {
                    try
                    {
                        Log.Info("Ollama", $"结束进程 {p.ProcessName}(PID {p.Id})");
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Ollama", $"结束进程失败（{p.ProcessName}）：{ex.Message}");
                    }
                    finally { p.Dispose(); }
                }
            }

            string exe = FindOllamaExe();
            if (exe == null)
            {
                progress?.Invoke("未找到 ollama.exe：请先安装 Ollama（https://ollama.com）。");
                return false;
            }

            progress?.Invoke("正在拉起 ollama serve…");
            Log.Info("Ollama", "拉起服务：" + exe);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "serve",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                progress?.Invoke("拉起服务失败：" + ex.Message);
                Log.Error("Ollama", "拉起 serve 失败", ex);
                return false;
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsAliveAsync().ConfigureAwait(false))
                {
                    progress?.Invoke("Ollama 服务已恢复。");
                    Log.Info("Ollama", "一键修复成功");
                    return true;
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            progress?.Invoke("等待 30 秒服务仍未就绪，请查看 Ollama 自身日志。");
            return false;
        }

        /// <summary>
        /// 一键拉取推荐模型（文本 + 视觉，可配置），逐个拉取。
        /// progress(模型名, percent, statusText)。
        /// </summary>
        public async Task PullRecommendedAsync(Action<string, int, string> progress, CancellationToken ct)
        {
            if (!await IsAliveAsync().ConfigureAwait(false))
            {
                throw new LlmException(LlmErrorKind.Network,
                    "Ollama 服务未运行，请先点「一键修复」。");
            }
            var entry = new ModelConfigEntry
            {
                Name = "Ollama", BaseUrl = BaseUrl, Protocol = ModelProtocol.Ollama
            };
            using (var client = new OllamaClient(entry, _config, Timeout.InfiniteTimeSpan))
            {
                foreach (var model in new[]
                {
                    _config.Current.OllamaRecommendTextModel,
                    _config.Current.OllamaRecommendVisionModel
                })
                {
                    if (string.IsNullOrWhiteSpace(model)) continue;
                    ct.ThrowIfCancellationRequested();
                    Log.Info("Ollama", "开始拉取模型：" + model);
                    await client.PullAsync(model,
                        (pct, status) => progress?.Invoke(model, pct, status), ct).ConfigureAwait(false);
                    Log.Info("Ollama", "拉取完成：" + model);
                }
            }
        }
    }
}
