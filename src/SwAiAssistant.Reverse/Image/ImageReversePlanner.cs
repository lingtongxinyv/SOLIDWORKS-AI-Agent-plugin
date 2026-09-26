using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAiAssistant.Ai;
using SwAiAssistant.Ai.Scheduling;
using SwAiAssistant.Core.Logging;
using SwAiAssistant.Planner;
using SwAiAssistant.Planner.Prompts;
using SwAiAssistant.Planner.Schema;

namespace SwAiAssistant.Reverse.Image
{
    /// <summary>图片/PDF 反建结果：候选特征树 + 识别元信息；NeedClarify 时只含中文追问。</summary>
    public sealed class ImageReversePlanResult
    {
        public FeatureTree Tree { get; set; }

        /// <summary>模型自评置信度 0~1；-1 表示模型未提供。</summary>
        public double Confidence { get; set; } = -1;

        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>一句话识别摘要（气泡/卡片展示）。</summary>
        public string Summary { get; set; } = "";

        /// <summary>图纸信息不足，模型要求澄清（不出候选树、不建模）。</summary>
        public bool NeedClarify { get; set; }
        public string ClarifyText { get; set; } = "";

        /// <summary>实际送给模型的图片路径（PDF 渲染/缩放后的临时文件）。</summary>
        public string PreparedImagePath { get; set; } = "";

        public string ConfidenceText => Confidence < 0 ? "未知" : ((int)Math.Round(Math.Max(0, Math.Min(1, Confidence)) * 100)) + "%";
    }

    /// <summary>
    /// 图片/PDF 图纸半自动反建（M9，实验功能）：照片/截图/PDF 首页 →
    /// 视觉模型输出与 DXF 路径同构的 FeatureTree JSON（复用领域 Prompt 与 PlanJsonParser 红线解析，
    /// 非法输出带错误纠正 2 轮）。本服务绝不建模：调用方必须经人工确认后才复用 PlanExecutor 执行。
    /// </summary>
    public sealed class ImageReversePlanner
    {
        private const int MaxCorrectionRounds = 2;
        private const int MaxImageSide = 1600;

        /// <summary>计划卡片顶部固定实验提示（{0}=置信度文本）。</summary>
        public const string BannerFormat =
            "⚠ 实验功能 · 图片/PDF 图纸识别候选（置信度：{0}）。AI 识别结果，请逐项核对尺寸后再点「执行」；"
            + "未点执行前 SolidWorks 不会有任何变化。";

        public static string FormatBanner(double confidence)
        {
            string text = confidence < 0
                ? "未知"
                : ((int)Math.Round(Math.Max(0, Math.Min(1, confidence)) * 100)) + "%";
            return string.Format(BannerFormat, text);
        }

        private readonly Scheduler _scheduler;

        public ImageReversePlanner(Scheduler scheduler)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        /// <summary>是否存在视觉候选模型（UI 据此给「先配置视觉模型」引导）。</summary>
        public bool HasVisionCandidate(IReadOnlyDictionary<string, ModelProfile> profiles)
        {
            return _scheduler.HasCandidate(ModelTask.VisionVerify, profiles);
        }

        /// <summary>
        /// 识别一张图纸（jpg/jpeg/png/bmp/gif/pdf）。成功返回候选（Tree 非空）或澄清结果；
        /// 无视觉模型/连续非法输出抛 LlmException；PDF 不可用抛含中文引导的 InvalidOperationException。
        /// </summary>
        public async Task<ImageReversePlanResult> PlanAsync(string sourcePath,
            IReadOnlyDictionary<string, ModelProfile> profiles, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("图纸文件不存在：" + sourcePath, sourcePath);
            }
            if (!HasVisionCandidate(profiles))
            {
                throw new LlmException(LlmErrorKind.Unknown,
                    "没有可用的视觉模型：请在设置页添加具备视觉能力的模型（推荐一键拉取 qwen2.5vl），"
                    + "或为已有视觉模型勾选「视觉能力」后重试。");
            }

            string prepared = PrepareImage(sourcePath);
            string base64 = Convert.ToBase64String(File.ReadAllBytes(prepared));
            Log.Info("Planner", $"图片反建：{sourcePath} → 送图 {prepared}（{base64.Length * 3 / 4 / 1024} KB base64）");

            string kind = Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "PDF 首页渲染图" : "照片/截图";
            var messages = new List<LlmMessage>
            {
                LlmMessage.System(DomainSystemPrompt.BuildForImageReverse()),
                LlmMessage.System(ImageRules),
                LlmMessage.UserVision(
                    "这是一张板类/平板类零件工程图纸的" + kind + "。请识别外轮廓形状与尺寸、全部内孔（圆/方）的位置与直径、"
                    + "以及板厚（厚度/板厚/THICKNESS/δ/t= 标注或侧视厚度尺寸），输出 FeatureTree JSON；"
                    + "若板厚或总体尺寸无任何标注、无法合理确定，输出 clarify 说明缺什么。",
                    base64)
            };

            string lastError = null;
            for (int round = 0; round <= MaxCorrectionRounds; round++)
            {
                int currentRound = round;
                string raw = await _scheduler.ExecuteAsync(ModelTask.VisionVerify, profiles,
                    (entry, client, c) =>
                    {
                        Log.Info("Planner", $"图片反建请求 → {entry.Name}（第 {currentRound + 1} 轮）");
                        return client.ChatAsync(messages, new ChatRequestOptions
                        {
                            JsonMode = true,
                            Temperature = 0.2,
                            MaxTokens = 2000,
                            ContextTokens = 8192
                        }, null, c);
                    }, ct).ConfigureAwait(false);

                Log.Info("Planner", "图片反建原始输出（截断 800）："
                    + (raw ?? "").Replace("\r", " ").Replace("\n", " ")
                        .Substring(0, Math.Min(800, (raw ?? "").Length)));

                if (TryBuildResult(raw, out var result, out lastError))
                {
                    result.PreparedImagePath = prepared;
                    return result;
                }
                Log.Warn("Planner", $"图片反建第 {round + 1} 轮输出被拒：{lastError}");
                if (round < MaxCorrectionRounds)
                {
                    messages.Add(LlmMessage.Assistant(raw ?? "<空输出>"));
                    messages.Add(LlmMessage.User(
                        "你的输出未通过校验，错误如下：\n" + lastError +
                        "\n请修正后重新只输出完整 JSON（不要代码、不要解释）。"));
                }
            }

            throw new LlmException(LlmErrorKind.Protocol,
                "视觉模型连续 " + (MaxCorrectionRounds + 1) + " 次未输出合法特征树 JSON。最后错误：\n" + lastError +
                "\n建议：换用读图能力更强的视觉模型（推荐 qwen2.5vl），或换更清晰、正视、带尺寸标注的图纸照片。");
        }

        /// <summary>PDF→首页 PNG；位图→等比缩放到长边 ≤1600 并统一重编码 JPEG（降负载/兼容怪异编码）。</summary>
        private static string PrepareImage(string sourcePath)
        {
            string workDir = Path.Combine(Path.GetTempPath(), "SwAiAssistant", "img");
            Directory.CreateDirectory(workDir);
            string token = Guid.NewGuid().ToString("N");

            string rasterPath;
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext == ".pdf")
            {
                if (!PdfToImage.IsSupported)
                {
                    throw new InvalidOperationException(PdfToImage.PdfGuidance);
                }
                rasterPath = Path.Combine(workDir, token + ".png");
                // WinRT 渲染不依赖同步上下文；桌面调用方直接阻塞等待即可
                PdfToImage.RenderFirstPageAsync(sourcePath, rasterPath, (uint)MaxImageSide)
                    .GetAwaiter().GetResult();
            }
            else
            {
                rasterPath = sourcePath;
            }

            string outPath = Path.Combine(workDir, token + ".jpg");
            using (var src = System.Drawing.Image.FromFile(rasterPath))
            {
                int w = src.Width, h = src.Height;
                double scale = Math.Min(1.0, (double)MaxImageSide / Math.Max(w, h));
                int nw = Math.Max(1, (int)Math.Round(w * scale));
                int nh = Math.Max(1, (int)Math.Round(h * scale));
                using (var bmp = new Bitmap(nw, nh, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, nw, nh);
                    }
                    bmp.Save(outPath, ImageFormat.Jpeg);
                }
            }
            if (!string.Equals(rasterPath, sourcePath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rasterPath, outPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(rasterPath);
            }
            return outPath;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 临时文件清理失败可忽略 */ }
        }

        private static bool TryBuildResult(string raw, out ImageReversePlanResult result, out string error)
        {
            result = null;
            error = null;
            if (!PlanJsonParser.TryParse(raw, out var parsed, out error))
            {
                return false;
            }

            if (parsed.Type != PlannerResponseType.Plan)
            {
                result = new ImageReversePlanResult
                {
                    NeedClarify = true,
                    ClarifyText = string.IsNullOrWhiteSpace(parsed.Text) ? "模型未能从图纸中识别出足够信息。" : parsed.Text
                };
                return true;
            }

            var tree = parsed.Plan;
            if (tree == null || tree.Steps == null
                || !tree.Steps.Any(s => string.Equals(s.Kind, "extrudeBoss", StringComparison.OrdinalIgnoreCase)))
            {
                error = "识别结果缺少拉伸凸台步骤（extrudeBoss），无法构成板类零件。";
                return false;
            }
            if (!tree.Steps.Any(s => string.Equals(s.Kind, "sketch", StringComparison.OrdinalIgnoreCase)))
            {
                error = "识别结果缺少草图步骤（sketch）。";
                return false;
            }

            var r = new ImageReversePlanResult { Tree = tree };
            try
            {
                var root = JObject.Parse(parsed.RawJson);
                var rec = root["recognition"] as JObject;
                if (rec != null)
                {
                    var conf = rec["confidence"];
                    if (conf != null && (conf.Type == JTokenType.Float || conf.Type == JTokenType.Integer))
                    {
                        r.Confidence = conf.Value<double>();
                    }
                    var warns = rec["warnings"] as JArray;
                    if (warns != null)
                    {
                        foreach (var w in warns.Select(t => t?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)))
                        {
                            r.Warnings.Add(w);
                        }
                    }
                    r.Summary = rec["summary"]?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Planner", "recognition 字段解析失败（不影响候选树）：" + ex.Message);
            }
            result = r;
            return true;
        }

        private const string ImageRules =
            "【图片/PDF 图纸反建专项规则】\n"
            + "你看到的是一张板类/平板类零件工程图纸的照片、截图或 PDF 首页渲染图（可能正视、轻微透视、手绘或拍照）。\n"
            + "在遵守上面特征树 Schema 的前提下，还必须：\n"
            + "1. 尺寸真值仲裁：只相信图面标注的数字（尺寸引线数值、直径符号 φ 后的数、文字注明的「厚度/板厚/壁厚/THICKNESS/THK/δ/t=」与侧视厚度尺寸）；"
            + "照片像素比例、纸张/手指等参照物一律不能作为尺寸依据。\n"
            + "2. 视图映射：多视图时只取最能反映外轮廓与孔分布的主视图（平板大面，映射为 top 草图）；忽略侧视图、轴测图、阴影、底色、背景杂物与纸边。\n"
            + "3. 坐标：把外轮廓包围盒中心作为草图原点（中心对称时 cx=0,cy=0）；平板一律 top 草图 + extrudeBoss，板厚作为 depthMm。\n"
            + "4. 轮廓与孔：轴对齐矩形外轮廓优先 rectCenter；圆形外轮廓用 circle；其余用最贴合的闭合 polyline（读不准的曲线用直线段近似）。"
            + "圆通孔每孔一条独立草图 circle + extrudeCut {throughAll:true}；方孔用闭合 polyline 小矩形 + extrudeCut throughAll。数清孔的数量与对称位置。\n"
            + "5. 比例歧义：标注数字之间明显矛盾时，选与外轮廓标注最自洽的一组，并把矛盾写进 warnings；完全没有尺寸标注、只能靠像素猜测时禁止编造。\n"
            + "6. 信息不足（读不出板厚，或外轮廓/孔位没有任何尺寸导致整体无法定量）时，返回 {\"type\":\"clarify\",\"text\":\"中文说明缺什么、建议用户补什么标注\"}。\n"
            + "7. 成功输出 {\"type\":\"plan\",\"plan\":{...}} 的同时，在根对象附带 recognition 字段："
            + "{\"thicknessMm\":数字,\"holes\":孔数整数,\"confidence\":0到1,\"warnings\":[\"中文注意事项\"],\"summary\":\"一句话识别摘要\"}。"
            + "confidence 标准：正视清晰且长宽厚/孔径标注齐全 0.9 左右；个别尺寸靠对称或相邻标注推断 0.6~0.8；只能靠像素比例估尺寸不得超过 0.5。\n"
            + "8. materialGuess 无法判断时必须为 null，禁止照抄字段说明里的枚举字符串。\n"
            + "【嵌套层级红线（最容易出错，务必照做）】\n"
            + "- 草图实体必须放在步骤的 \"sketch\":{\"plane\":...,\"entities\":[...]} 里面，严禁直接挂在 step 上；\n"
            + "- extrudeBoss / extrudeCut 必须是 steps 数组中各自独立的步骤对象，用 \"sketchId\" 引用前面的草图，"
            + "严禁挂到 plan 根对象、严禁与别的步骤挤在同一个对象里；每个孔是一对独立的 sketch+extrudeCut 步骤；\n"
            + "- 输出必须是一个完整、闭合的 JSON 对象，禁止重复片段、禁止半截对象。\n"
            + "【输出骨架示例】（100×60 板、厚 8、两个 φ12 孔；实际尺寸以你从图中读到的为准）\n"
            + "{\"type\":\"plan\",\"plan\":{\"version\":\"1.0\",\"units\":\"mm\",\"part\":{\"name\":\"板类零件\",\"materialGuess\":null},\"steps\":["
            + "{\"id\":\"s1\",\"kind\":\"sketch\",\"title\":\"外轮廓草图\",\"sketch\":{\"plane\":\"top\",\"entities\":[{\"type\":\"rectCenter\",\"cx\":0,\"cy\":0,\"width\":100,\"height\":60}]}},"
            + "{\"id\":\"s2\",\"kind\":\"extrudeBoss\",\"title\":\"板体拉伸\",\"sketchId\":\"s1\",\"extrude\":{\"depthMm\":8}},"
            + "{\"id\":\"s3\",\"kind\":\"sketch\",\"title\":\"孔1草图\",\"sketch\":{\"plane\":\"top\",\"entities\":[{\"type\":\"circle\",\"cx\":-30,\"cy\":0,\"diameter\":12}]}},"
            + "{\"id\":\"s4\",\"kind\":\"extrudeCut\",\"title\":\"孔1切除\",\"sketchId\":\"s3\",\"extrude\":{\"throughAll\":true}},"
            + "{\"id\":\"s5\",\"kind\":\"sketch\",\"title\":\"孔2草图\",\"sketch\":{\"plane\":\"top\",\"entities\":[{\"type\":\"circle\",\"cx\":30,\"cy\":0,\"diameter\":12}]}},"
            + "{\"id\":\"s6\",\"kind\":\"extrudeCut\",\"title\":\"孔2切除\",\"sketchId\":\"s5\",\"extrude\":{\"throughAll\":true}}"
            + "]},\"recognition\":{\"thicknessMm\":8,\"holes\":2,\"confidence\":0.9,\"warnings\":[],\"summary\":\"矩形板100×60×8，两个φ12通孔\"}}\n"
            + "仍然只输出一个 JSON 对象，禁止输出任何代码、markdown 围栏与解释文字。";
    }
}
