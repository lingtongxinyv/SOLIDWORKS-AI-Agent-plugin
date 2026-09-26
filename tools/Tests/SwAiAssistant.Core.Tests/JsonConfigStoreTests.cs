using System.IO;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Security;
using Xunit;

namespace SwAiAssistant.Core.Tests
{
    public class JsonConfigStoreTests
    {
        public class SampleConfig
        {
            public string Name { get; set; } = "default";
            public string EncryptedKey { get; set; } = "";
            public int TimeoutSeconds { get; set; } = 60;
        }

        // 每个测试使用独立临时目录，避免触碰用户真实 %AppData%
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SwAiAssistantTests",
                System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Save_Reload_PreservesValues()
        {
            var store = new JsonConfigStore<SampleConfig>("test.sample.json", NewTempDir());
            store.Mutate(c =>
            {
                c.Name = "测试模型";
                c.TimeoutSeconds = 42;
                c.EncryptedKey = DpapiHelper.Protect("sk-plaintext-must-not-appear-98765");
            });

            var reloaded = new JsonConfigStore<SampleConfig>("test.sample.json",
                Path.GetDirectoryName(store.FilePath));

            Assert.Equal("测试模型", reloaded.Current.Name);
            Assert.Equal(42, reloaded.Current.TimeoutSeconds);
            Assert.Equal("sk-plaintext-must-not-appear-98765",
                DpapiHelper.Unprotect(reloaded.Current.EncryptedKey));
        }

        [Fact]
        public void Save_FileContainsNoPlaintextKey()
        {
            const string secret = "sk-plaintext-must-not-appear-98765";
            var store = new JsonConfigStore<SampleConfig>("test.sample.json", NewTempDir());
            store.Mutate(c => c.EncryptedKey = DpapiHelper.Protect(secret));

            string raw = File.ReadAllText(store.FilePath);

            Assert.DoesNotContain(secret, raw);
            Assert.Contains("encryptedKey", raw);
        }

        [Fact]
        public void MissingFile_ReturnsDefaultInstance()
        {
            var store = new JsonConfigStore<SampleConfig>(
                "test.never-exists.json", NewTempDir());

            Assert.NotNull(store.Current);
            Assert.Equal("default", store.Current.Name);
        }
    }
}
