using System;
using System.IO;
using System.Linq;
using SwAiAssistant.Core.Logging;
using Xunit;

namespace SwAiAssistant.Core.Tests
{
    public class LogTests
    {
        // 每个测试使用独立临时目录，避免触碰用户真实 %AppData%
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SwAiAssistantTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Info_WritesToDatedLogFile()
        {
            string dir = NewTempDir();
            Log.DirectoryOverride = dir;
            Log.MinimumLevel = LogLevel.Debug;
            string token = "UNITTEST_TOKEN_" + Guid.NewGuid().ToString("N");

            Log.Info("Tests", token);

            string today = DateTime.Now.ToString("yyyyMMdd");
            string file = Path.Combine(dir, "app-" + today + ".log");
            Assert.True(File.Exists(file), "日志文件未创建：" + file);

            string content = ReadAllTextShared(file);
            Assert.Contains(token, content);
            Assert.Contains("[INFO]", content);
            Assert.Contains("[Tests]", content);
        }

        /// <summary>共享读打开 + 短重试，规避杀软/索引器瞬时占用。</summary>
        private static string ReadAllTextShared(string file)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (IOException) when (attempt < 5)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        [Fact]
        public void Debug_BelowMinimumLevel_IsFiltered()
        {
            string dir = NewTempDir();
            Log.DirectoryOverride = dir;
            Log.MinimumLevel = LogLevel.Error;
            string token = "UNITTEST_FILTERED_" + Guid.NewGuid().ToString("N");

            Log.Debug("Tests", token);

            string today = DateTime.Now.ToString("yyyyMMdd");
            string file = Path.Combine(dir, "app-" + today + ".log");
            bool found = File.Exists(file) && File.ReadLines(file).Any(l => l.Contains(token));
            Assert.False(found, "低于最低级别的日志不应落盘");

            Log.MinimumLevel = LogLevel.Debug;
        }
    }
}
