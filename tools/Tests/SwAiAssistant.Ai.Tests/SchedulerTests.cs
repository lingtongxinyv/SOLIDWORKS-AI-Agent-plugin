using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwAiAssistant.Ai;
using SwAiAssistant.Ai.Scheduling;
using SwAiAssistant.Core.Configuration;
using Xunit;

namespace SwAiAssistant.Ai.Tests
{
    public class SchedulerTests
    {
        private static (ConfigService Config, string Dir) TempConfig()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SwAiAssistantTests", Guid.NewGuid().ToString("N"));
            var config = new ConfigService("app.json", dir);
            return (config, dir);
        }

        private static ModelConfigEntry Entry(string id, string name, bool local = false) =>
            new ModelConfigEntry
            {
                Id = id,
                Name = name,
                BaseUrl = local ? "http://127.0.0.1:11434" : "https://api.example.com",
                Model = name,
                Protocol = local ? ModelProtocol.Ollama : ModelProtocol.OpenAiCompatible
            };

        private static ModelProfile Profile(string id, bool local, bool vision = false, bool text = true) =>
            new ModelProfile
            {
                EntryId = id,
                Model = id,
                IsLocal = local,
                VisionCapable = vision,
                TextCapable = text,
                Protocol = local ? ProbeProtocol.Ollama : ProbeProtocol.OpenAiCompatible
            };

        [Fact]
        public void Route_VisionTask_OnlyVisionModels()
        {
            var (config, _) = TempConfig();
            config.Mutate(c =>
            {
                c.Models.Add(Entry("t1", "cloud-text"));
                c.Models.Add(Entry("v1", "cloud-vision"));
                c.Models.Add(Entry("l1", "local-vision", local: true));
            });
            var profiles = new Dictionary<string, ModelProfile>
            {
                ["t1"] = Profile("t1", local: false),
                ["v1"] = Profile("v1", local: false, vision: true),
                ["l1"] = Profile("l1", local: true, vision: true)
            };
            var scheduler = new Scheduler(config, new CapabilityProbe(config, Path.GetDirectoryName(config.FilePath)));

            var chain = scheduler.BuildChain(ModelTask.VisionVerify, profiles);

            Assert.Equal(new[] { "v1", "l1" }, chain.Select(c => c.Entry.Id).ToArray());
        }

        [Fact]
        public void Route_PlanTask_CloudFirstLocalLast()
        {
            var (config, _) = TempConfig();
            config.Mutate(c =>
            {
                c.Models.Add(Entry("l1", "local-text", local: true));
                c.Models.Add(Entry("c1", "cloud-a"));
                c.Models.Add(Entry("c2", "cloud-b"));
            });
            var profiles = new Dictionary<string, ModelProfile>
            {
                ["l1"] = Profile("l1", local: true),
                ["c1"] = Profile("c1", local: false),
                ["c2"] = Profile("c2", local: false)
            };
            var scheduler = new Scheduler(config, new CapabilityProbe(config, Path.GetDirectoryName(config.FilePath)));

            var chain = scheduler.BuildChain(ModelTask.Plan, profiles);

            Assert.Equal(3, chain.Count);
            Assert.True(chain[0].Profile.IsLocal == false);
            Assert.Equal("l1", chain.Last().Entry.Id);
        }

        [Fact]
        public void Route_SkipsCircuitOpenModels()
        {
            var (config, _) = TempConfig();
            config.Mutate(c =>
            {
                c.Models.Add(Entry("bad", "cloud-bad"));
                c.Models.Add(Entry("good", "cloud-good"));
            });
            var profiles = new Dictionary<string, ModelProfile>
            {
                ["bad"] = Profile("bad", local: false),
                ["good"] = Profile("good", local: false)
            };
            var scheduler = new Scheduler(config, new CapabilityProbe(config, Path.GetDirectoryName(config.FilePath)));
            scheduler.HealthOf("bad").RecordFailure();
            scheduler.HealthOf("bad").RecordFailure();
            scheduler.HealthOf("bad").RecordFailure();

            var chain = scheduler.BuildChain(ModelTask.Plan, profiles);

            Assert.Single(chain);
            Assert.Equal("good", chain[0].Entry.Id);
        }

        [Fact]
        public async Task Execute_FallsThroughChain_OnFailure()
        {
            var (config, _) = TempConfig();
            config.Mutate(c =>
            {
                c.Models.Add(Entry("c1", "cloud-a"));
                c.Models.Add(Entry("c2", "cloud-b"));
            });
            var profiles = new Dictionary<string, ModelProfile>
            {
                ["c1"] = Profile("c1", local: false),
                ["c2"] = Profile("c2", local: false)
            };
            var scheduler = new Scheduler(config, new CapabilityProbe(config, Path.GetDirectoryName(config.FilePath)));
            var tried = new List<string>();

            string result = await scheduler.ExecuteAsync(ModelTask.Plan, profiles,
                (entry, client, ct) =>
                {
                    tried.Add(entry.Id);
                    if (entry.Id == "c1") throw new LlmException(LlmErrorKind.Network, "模拟断网");
                    return Task.FromResult("ok:" + entry.Id);
                }, CancellationToken.None);

            Assert.Equal("ok:c2", result);
            Assert.Equal(new[] { "c1", "c2" }, tried.ToArray());
        }

        [Fact]
        public void Route_SkipsConnectFailed_KeepsUntested()
        {
            var (config, _) = TempConfig();
            config.Mutate(c =>
            {
                c.Models.Add(Entry("bad", "cloud-connect-failed"));
                c.Models.Add(Entry("new", "cloud-untested"));
                c.Models.Add(Entry("ok", "cloud-connect-ok"));
            });
            var profiles = new Dictionary<string, ModelProfile>
            {
                ["bad"] = Profile("bad", local: false),
                ["new"] = Profile("new", local: false),
                ["ok"] = Profile("ok", local: false)
            };
            // bad=连通测试失败；new=未测过（给一次机会）；ok=已通过
            config.EnabledModels().First(m => m.Id == "bad").LastConnectOk = false;
            config.EnabledModels().First(m => m.Id == "ok").LastConnectOk = true;
            var scheduler = new Scheduler(config, new CapabilityProbe(config, Path.GetDirectoryName(config.FilePath)));

            var chain = scheduler.BuildChain(ModelTask.Plan, profiles);

            Assert.Equal(new[] { "ok", "new" }, chain.Select(c => c.Entry.Id).ToArray());
        }

        [Fact]
        public async Task Execute_MarksConnectResult_AndPersists()
        {
            var (config, dir) = TempConfig();
            config.Mutate(c => c.Models.Add(Entry("c1", "cloud-a")));
            var profiles = new Dictionary<string, ModelProfile> { ["c1"] = Profile("c1", local: false) };
            var scheduler = new Scheduler(config, new CapabilityProbe(config, Path.GetDirectoryName(config.FilePath)));

            // 失败 → LastConnectOk=false 持久化
            Task<string> Fail(ModelConfigEntry e, IModelClient c, CancellationToken ct) =>
                throw new LlmException(LlmErrorKind.Network, "模拟断网");
            await Assert.ThrowsAsync<LlmException>(() => scheduler.ExecuteAsync(
                ModelTask.Plan, profiles, Fail, CancellationToken.None));
            Assert.False(config.EnabledModels().Single(m => m.Id == "c1").LastConnectOk);

            // 重新打开配置仍为 false（已落盘）
            var reopened = new ConfigService("app.json", dir);
            Assert.False(reopened.EnabledModels().Single(m => m.Id == "c1").LastConnectOk);

            // 连通测试恢复 true 后重新参与调度并成功回写
            reopened.EnabledModels().Single(m => m.Id == "c1").LastConnectOk = true;
            var scheduler2 = new Scheduler(reopened, new CapabilityProbe(reopened, Path.GetDirectoryName(reopened.FilePath)));
            string result = await scheduler2.ExecuteAsync(ModelTask.Plan, profiles,
                (e, c, ct) => Task.FromResult("ok"), CancellationToken.None);
            Assert.Equal("ok", result);
            Assert.True(reopened.EnabledModels().Single(m => m.Id == "c1").LastConnectOk);
        }

        [Fact]
        public async Task Execute_AllFailed_NeverAppendsOutsideModels()
        {
            var (config, _) = TempConfig();
            config.Mutate(c =>
            {
                c.Models.Add(Entry("c1", "cloud-a"));
                c.Models.Add(Entry("c2", "cloud-b"));
            });
            var profiles = new Dictionary<string, ModelProfile>
            {
                ["c1"] = Profile("c1", local: false),
                ["c2"] = Profile("c2", local: false)
            };
            var scheduler = new Scheduler(config, new CapabilityProbe(config, Path.GetDirectoryName(config.FilePath)));
            var tried = new List<string>();

            Task<string> AlwaysFail(ModelConfigEntry entry, IModelClient client, CancellationToken ct)
            {
                tried.Add(entry.Id);
                throw new LlmException(LlmErrorKind.Network, "模拟全失败");
            }

            var ex = await Assert.ThrowsAsync<LlmException>(() => scheduler.ExecuteAsync(
                ModelTask.Plan, profiles, AlwaysFail, CancellationToken.None));

            // 只尝试了列表内已添加的模型，绝不追加 ollama-auto-* 等列表外候选
            Assert.Equal(new[] { "c1", "c2" }, tried.ToArray());
            Assert.DoesNotContain("ollama-auto", ex.Message);
            Assert.Contains("全部 2 个候选模型均失败", ex.Message);
        }

        [Fact]
        public async Task Execute_NoCandidate_ThrowsChineseGuidance()
        {
            var (config, _) = TempConfig();
            var profiles = new Dictionary<string, ModelProfile>();
            var scheduler = new Scheduler(config, new CapabilityProbe(config, Path.GetDirectoryName(config.FilePath)));
            bool noModelFired = false;
            scheduler.NoModelAvailable += (_, __) => noModelFired = true;

            var ex = await Assert.ThrowsAsync<LlmException>(() => scheduler.ExecuteAsync(
                ModelTask.Plan, profiles, (e, c, ct) => Task.FromResult("x"), CancellationToken.None));

            Assert.Contains("设置页", ex.Message);
            Assert.True(noModelFired);
        }

        [Fact]
        public async Task Execute_VisionTaskNoVisionModel_SkipsWithExplanation()
        {
            var (config, _) = TempConfig();
            config.Mutate(c => c.Models.Add(Entry("t1", "cloud-text")));
            var profiles = new Dictionary<string, ModelProfile> { ["t1"] = Profile("t1", local: false) };
            var scheduler = new Scheduler(config, new CapabilityProbe(config, Path.GetDirectoryName(config.FilePath)));

            var ex = await Assert.ThrowsAsync<LlmException>(() => scheduler.ExecuteAsync(
                ModelTask.VisionVerify, profiles, (e, c, ct) => Task.FromResult("x"), CancellationToken.None));

            Assert.Contains("视觉模型", ex.Message);
            Assert.Contains("跳过视觉复核", ex.Message);
        }

        [Fact]
        public void Health_RecoveryAfterSuccess()
        {
            var (config, _) = TempConfig();
            var scheduler = new Scheduler(config, new CapabilityProbe(config, Path.GetDirectoryName(config.FilePath)));
            var h = scheduler.HealthOf("x");
            h.RecordFailure();
            h.RecordFailure();
            Assert.False(h.IsCircuitOpen);
            h.RecordFailure();
            Assert.True(h.IsCircuitOpen);
            h.RecordSuccess(123);
            Assert.False(h.IsCircuitOpen);
            Assert.Equal(123, h.LastLatencyMs);
        }
    }
}
