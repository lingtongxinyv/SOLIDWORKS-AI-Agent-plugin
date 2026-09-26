using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SwAiAssistant.Core.Configuration;

namespace SwAiAssistant.AddIn.UI
{
    /// <summary>模型增/改对话框。Key 输入框留空 = 保持已存 Key 不变。</summary>
    public partial class ModelEditDialog : Window
    {
        private readonly ConfigService _config;
        private readonly bool _hadKey;

        public ModelConfigEntry Entry { get; private set; }

        public ModelEditDialog(ConfigService config, ModelConfigEntry existing)
        {
            InitializeComponent();
            _config = config ?? ConfigService.Default;
            Entry = existing?.Clone() ?? new ModelConfigEntry();

            NameBox.Text = Entry.Name;
            BaseUrlBox.Text = Entry.BaseUrl;
            ModelBox.Text = Entry.Model;
            ManualTextBox.IsChecked = Entry.ManualTextCapable;
            ManualVisionBox.IsChecked = Entry.ManualVisionCapable;
            ManualContextBox.Text = Entry.ManualContextTokens.ToString();
            foreach (ComboBoxItem item in ProtocolBox.Items)
            {
                if (string.Equals(item.Tag?.ToString(), Entry.Protocol.ToString(), StringComparison.Ordinal))
                {
                    ProtocolBox.SelectedItem = item;
                    break;
                }
            }

            _hadKey = existing != null && _config.HasApiKey(existing);
            if (_hadKey)
            {
                ApiKeyBox.Text = "";
                KeyHint.Text = "已保存 Key（" + ConfigService.MaskKey(_config.GetApiKey(existing)) +
                    "），留空表示不修改；输入新值则覆盖；输入单个 - 则清除。";
            }
            else
            {
                KeyHint.Text = "Ollama 本机模型可留空。Key 仅以 Windows DPAPI 密文存本机。";
            }

            PopulateProviderPresets();
        }

        // ==================== 预设提供商 ====================

        private sealed class ProviderPreset
        {
            public string DisplayName;   // 下拉显示（含括号说明）
            public string BaseUrl;
            public string SuggestedModel; // 推荐模型名（自动填入）
            public string AltModels;      // 备选模型提示
            public string ProtocolTag;    // 非空时联动协议下拉；null = 不改
        }

        private sealed class ProviderGroup
        {
            public string Header;
            public ProviderPreset[] Items;
        }

        /// <summary>
        /// 预设提供商清单（OpenAI 兼容端点，2026-09 核对）。
        /// 端点/模型名可能随服务商升级变化，用户可自行手改。
        /// </summary>
        private static readonly ProviderGroup[] ProviderGroups =
        {
            new ProviderGroup
            {
                Header = "── 国内云端服务商 ──",
                Items = new[]
                {
                    new ProviderPreset{ DisplayName="DeepSeek", BaseUrl="https://api.deepseek.com/v1",
                        SuggestedModel="deepseek-chat", AltModels="deepseek-reasoner（推理）" },
                    new ProviderPreset{ DisplayName="阿里云百炼（通义千问 Qwen）", BaseUrl="https://dashscope.aliyuncs.com/compatible-mode/v1",
                        SuggestedModel="qwen-plus", AltModels="qwen-max、qwen-turbo、qwen3-vl-plus（视觉）" },
                    new ProviderPreset{ DisplayName="智谱 AI（GLM）", BaseUrl="https://open.bigmodel.cn/api/paas/v4",
                        SuggestedModel="glm-4.5", AltModels="glm-4-plus、glm-4-flash、glm-4v-plus（视觉）" },
                    new ProviderPreset{ DisplayName="Moonshot AI（Kimi）", BaseUrl="https://api.moonshot.cn/v1",
                        SuggestedModel="kimi-k2.6", AltModels="kimi-k3（旗舰）、kimi-k2.7-code、kimi-k2.7-code-highspeed；注意须用 API 模型 ID（小写加点，如 kimi-k2.6），旧 moonshot-v1-* 已于 2026-08 退役" },
                    new ProviderPreset{ DisplayName="字节火山方舟（豆包）", BaseUrl="https://ark.cn-beijing.volces.com/api/v3",
                        SuggestedModel="doubao-seed-1-6-250615", AltModels="或填方舟控制台的接入点 ID（ep- 开头）；视觉 doubao-1.5-vision-pro-32k" },
                    new ProviderPreset{ DisplayName="百度千帆（文心 ERNIE）", BaseUrl="https://qianfan.baidubce.com/v2",
                        SuggestedModel="ernie-4.0-turbo-8k", AltModels="ernie-3.5-8k、ernie-speed-128k、deepseek-v3" },
                    new ProviderPreset{ DisplayName="腾讯混元", BaseUrl="https://api.hunyuan.cloud.tencent.com/v1",
                        SuggestedModel="hunyuan-turbos-latest", AltModels="hunyuan-t1-latest、hunyuan-vision（视觉）" },
                    new ProviderPreset{ DisplayName="MiniMax", BaseUrl="https://api.minimax.chat/v1",
                        SuggestedModel="MiniMax-Text-01", AltModels="abab6.5s-chat、MiniMax-VL-01（视觉）" },
                    new ProviderPreset{ DisplayName="讯飞星火", BaseUrl="https://spark-api-open.xf-yun.com/v1",
                        SuggestedModel="generalv3.5", AltModels="4.0Ultra、max-32k、lite" },
                    new ProviderPreset{ DisplayName="阶跃星辰 StepFun", BaseUrl="https://api.stepfun.com/v1",
                        SuggestedModel="step-2-16k", AltModels="step-2-mini、step-1v-8k（视觉）" },
                    new ProviderPreset{ DisplayName="零一万物（Yi）", BaseUrl="https://api.lingyiwanwu.com/v1",
                        SuggestedModel="yi-lightning", AltModels="yi-large、yi-vision-v2（视觉）" },
                    new ProviderPreset{ DisplayName="百川智能", BaseUrl="https://api.baichuan-ai.com/v1",
                        SuggestedModel="baichuan4-turbo", AltModels="baichuan4、baichuan3-turbo" },
                    new ProviderPreset{ DisplayName="硅基流动 SiliconFlow（多模型聚合）", BaseUrl="https://api.siliconflow.cn/v1",
                        SuggestedModel="deepseek-ai/DeepSeek-V3", AltModels="Qwen/Qwen2.5-72B-Instruct、deepseek-ai/DeepSeek-R1、Qwen/Qwen2.5-VL-72B-Instruct（视觉）" },
                }
            },
            new ProviderGroup
            {
                Header = "── 国外云端服务商 ──",
                Items = new[]
                {
                    new ProviderPreset{ DisplayName="OpenAI", BaseUrl="https://api.openai.com/v1",
                        SuggestedModel="gpt-4o", AltModels="gpt-4o-mini、gpt-4.1、o3-mini" },
                    new ProviderPreset{ DisplayName="Anthropic（Claude，OpenAI 兼容层）", BaseUrl="https://api.anthropic.com/v1",
                        SuggestedModel="claude-sonnet-4-20250514", AltModels="claude-opus-4-1-20250805、claude-3-5-haiku-20241022" },
                    new ProviderPreset{ DisplayName="Google Gemini（OpenAI 兼容层）", BaseUrl="https://generativelanguage.googleapis.com/v1beta/openai/",
                        SuggestedModel="gemini-2.5-flash", AltModels="gemini-2.5-pro、gemini-2.0-flash" },
                    new ProviderPreset{ DisplayName="xAI（Grok）", BaseUrl="https://api.x.ai/v1",
                        SuggestedModel="grok-3", AltModels="grok-4、grok-2-vision-1212（视觉）" },
                    new ProviderPreset{ DisplayName="Mistral", BaseUrl="https://api.mistral.ai/v1",
                        SuggestedModel="mistral-large-latest", AltModels="mistral-small-latest、pixtral-large-latest（视觉）" },
                    new ProviderPreset{ DisplayName="Groq（超低延迟聚合）", BaseUrl="https://api.groq.com/openai/v1",
                        SuggestedModel="llama-3.3-70b-versatile", AltModels="meta-llama/llama-4-scout-17b-16e-instruct、meta-llama/llama-4-maverick-17b-128e-instruct" },
                    new ProviderPreset{ DisplayName="OpenRouter（多模型聚合）", BaseUrl="https://openrouter.ai/api/v1",
                        SuggestedModel="openai/gpt-4o-mini", AltModels="anthropic/claude-sonnet-4、deepseek/deepseek-chat-v3-0324、google/gemini-2.0-flash-001" },
                    new ProviderPreset{ DisplayName="Together AI", BaseUrl="https://api.together.xyz/v1",
                        SuggestedModel="meta-llama/Llama-3.3-70B-Instruct-Turbo", AltModels="Qwen/Qwen2.5-72B-Instruct-Turbo" },
                    new ProviderPreset{ DisplayName="DeepInfra", BaseUrl="https://api.deepinfra.com/v1/openai",
                        SuggestedModel="meta-llama/Llama-3.3-70B-Instruct", AltModels="Qwen/Qwen2.5-72B-Instruct、deepseek-ai/DeepSeek-V3" },
                    new ProviderPreset{ DisplayName="Fireworks AI", BaseUrl="https://api.fireworks.ai/inference/v1",
                        SuggestedModel="accounts/fireworks/models/llama-v3p3-70b-instruct", AltModels="accounts/fireworks/models/qwen2p5-72b-instruct" },
                    new ProviderPreset{ DisplayName="Cerebras", BaseUrl="https://api.cerebras.ai/v1",
                        SuggestedModel="llama-3.3-70b", AltModels="llama3.1-8b、qwen-3-32b" },
                    new ProviderPreset{ DisplayName="Perplexity", BaseUrl="https://api.perplexity.ai",
                        SuggestedModel="sonar", AltModels="sonar-pro、sonar-reasoning-pro" },
                    new ProviderPreset{ DisplayName="Cohere（OpenAI 兼容层）", BaseUrl="https://api.cohere.ai/compatibility/v1",
                        SuggestedModel="command-r-plus", AltModels="command-a-03-2025" },
                    new ProviderPreset{ DisplayName="Azure OpenAI（占位，需改资源名）", BaseUrl="https://YOUR-RESOURCE.openai.azure.com/openai/v1",
                        SuggestedModel="你的部署名", AltModels="在 Azure AI Foundry 部署后填部署名（deployment name）" },
                }
            },
            new ProviderGroup
            {
                Header = "── 本机服务 ──",
                Items = new[]
                {
                    new ProviderPreset{ DisplayName="Ollama（本机）", BaseUrl="http://127.0.0.1:11434",
                        SuggestedModel="qwen2.5:7b", AltModels="qwen2.5vl:7b（视觉）、deepseek-r1:7b", ProtocolTag="Ollama" },
                    new ProviderPreset{ DisplayName="LM Studio（本机）", BaseUrl="http://127.0.0.1:1234/v1",
                        SuggestedModel="local-model", AltModels="以 LM Studio 中加载的模型为准" },
                    new ProviderPreset{ DisplayName="vLLM（本机）", BaseUrl="http://127.0.0.1:8000/v1",
                        SuggestedModel="served-model-name", AltModels="以启动参数 --served-model-name 为准" },
                }
            },
        };

        private void PopulateProviderPresets()
        {
            ProviderBox.Items.Clear();
            var placeholder = new ComboBoxItem { Content = "— 选择预设（可不选）", Tag = null, IsEnabled = true };
            ProviderBox.Items.Add(placeholder);
            foreach (var group in ProviderGroups)
            {
                ProviderBox.Items.Add(new ComboBoxItem
                {
                    Content = group.Header,
                    IsEnabled = false,
                    Foreground = System.Windows.Media.Brushes.Gray
                });
                foreach (var p in group.Items)
                {
                    ProviderBox.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p });
                }
            }
            ProviderBox.SelectedIndex = 0;
        }

        private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProviderBox?.SelectedItem is ComboBoxItem ci && ci.Tag is ProviderPreset p)
            {
                BaseUrlBox.Text = p.BaseUrl;
                if (NameBox != null && NameBox.Text.Trim().Length == 0)
                {
                    int paren = p.DisplayName.IndexOf('（');
                    NameBox.Text = paren > 0 ? p.DisplayName.Substring(0, paren) : p.DisplayName;
                }
                if (ModelBox != null && ModelBox.Text.Trim().Length == 0)
                {
                    ModelBox.Text = p.SuggestedModel;
                }
                if (ModelLabel != null)
                {
                    ModelLabel.Text = "模型名（推荐 " + p.SuggestedModel + "；备选：" + p.AltModels + "）";
                }
                if (p.ProtocolTag != null && ProtocolBox != null)
                {
                    foreach (ComboBoxItem item in ProtocolBox.Items)
                    {
                        if (string.Equals(item.Tag?.ToString(), p.ProtocolTag, StringComparison.Ordinal))
                        {
                            ProtocolBox.SelectedItem = item;
                            break;
                        }
                    }
                }
            }
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            Entry.Name = NameBox.Text.Trim();
            Entry.BaseUrl = BaseUrlBox.Text.Trim();
            Entry.Model = ModelBox.Text.Trim();
            Entry.ManualTextCapable = ManualTextBox.IsChecked == true;
            Entry.ManualVisionCapable = ManualVisionBox.IsChecked == true;
            int.TryParse(ManualContextBox.Text.Trim(), out int ctx);
            Entry.ManualContextTokens = Math.Max(0, ctx);
            if (ProtocolBox.SelectedItem is ComboBoxItem pi
                && Enum.TryParse(pi.Tag?.ToString(), out ModelProtocol proto))
            {
                Entry.Protocol = proto;
            }

            if (Entry.Name.Length == 0) { ErrorText.Text = "请填写名称。"; return; }
            if (Entry.BaseUrl.Length == 0) { ErrorText.Text = "请填写 Base URL。"; return; }
            if (Entry.Model.Length == 0) { ErrorText.Text = "请填写模型名。"; return; }

            string keyInput = ApiKeyBox.Text;
            if (keyInput == "-")
            {
                _config.SetApiKey(Entry, "");
            }
            else if (!string.IsNullOrEmpty(keyInput))
            {
                _config.SetApiKey(Entry, keyInput.Trim());
            }
            else if (!_hadKey)
            {
                _config.SetApiKey(Entry, "");
            }

            // 配置已变化，旧连通状态作废，需重新连通测试
            Entry.LastConnectOk = null;
            Entry.LastConnectTimeUtc = "";

            DialogResult = true;
            Close();
        }
    }
}
