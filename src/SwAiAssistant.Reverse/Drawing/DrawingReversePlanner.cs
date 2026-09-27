using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAiAssistant.Ai;
using SwAiAssistant.Ai.Scheduling;
using SwAiAssistant.Cad.Drawings;
using SwAiAssistant.Core.Logging;
using SwAiAssistant.Planner;
using SwAiAssistant.Planner.Prompts;
using SwAiAssistant.Planner.Schema;

namespace SwAiAssistant.Reverse.Drawing
{
    /// <summary>工程图反建结果：候选特征树 + 提取元信息；NeedClarify 时只含中文追问。</summary>
    public sealed class DrawingReversePlanResult
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

        /// <summary>实际送给视觉模型的图纸图片路径（纯文本路径时为空）。</summary>
        public string PreparedImagePath { get; set; } = "";

        /// <summary>本次规划是否消耗了视觉模型。</summary>
        public bool UsedVision { get; set; }

        public string ConfidenceText => Confidence < 0
            ? "未知"
            : ((int)Math.Round(Math.Max(0, Math.Min(1, Confidence)) * 100)) + "%";
    }

    /// <summary>
    /// SolidWorks 原生工程图半自动反建：ExtractDrawing 的确定性事实（标注 + 可见圆边模型坐标）
    /// 文本优先——事实足以定量时走文本规划模型，不消耗视觉模型；
    /// 事实不足且存在视觉模型时才附图纸截图走视觉路径；仍不足则让模型 clarify（UI 向用户追问）。
    /// 本服务绝不建模：调用方必须经人工确认后才复用 PlanExecutor 执行；不打开工程图引用的源零件。
    /// </summary>
    public sealed class DrawingReversePlanner
    {
        private const int MaxCorrectionRounds = 2;

        /// <summary>计划卡片顶部固定提示（{0}=置信度文本，{1}=规划路径文本）。</summary>
        public const string BannerFormat =
            "⚠ 按图建模候选（{0} · {1}）：依据当前工程图的标注与视图几何反建，"
            + "请逐项核对草图尺寸、孔径与未标注的孔位后再点「执行」；未点执行前 SolidWorks 不会有任何变化。";

        public static string FormatBanner(double confidence, bool usedVision)
        {
            string conf = confidence < 0
                ? "未知"
                : ((int)Math.Round(Math.Max(0, Math.Min(1, confidence)) * 100)) + "%";
            return string.Format(BannerFormat, conf, usedVision ? "视觉模型读图" : "确定性尺寸文本");
        }

        private readonly Scheduler _scheduler;

        public DrawingReversePlanner(Scheduler scheduler)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        /// <summary>是否存在视觉候选模型（文本不足时作为补充路径）。</summary>
        public bool HasVisionCandidate(IReadOnlyDictionary<string, ModelProfile> profiles)
        {
            return _scheduler.HasCandidate(ModelTask.VisionVerify, profiles);
        }

        /// <summary>是否存在文本规划候选模型。</summary>
        public bool HasPlanCandidate(IReadOnlyDictionary<string, ModelProfile> profiles)
        {
            return _scheduler.HasCandidate(ModelTask.Plan, profiles);
        }

        /// <summary>
        /// 依据工程图提取事实生成候选计划。sheetImagePath 可为空（纯文本路径）；
        /// 无任何可用模型/连续非法输出抛 LlmException。
        /// </summary>
        public async Task<DrawingReversePlanResult> PlanAsync(
            DrawingExtract extract,
            IReadOnlyDictionary<string, ModelProfile> profiles,
            string sheetImagePath,
            CancellationToken ct)
        {
            if (extract == null) throw new ArgumentNullException(nameof(extract));
            if (!HasPlanCandidate(profiles) && !HasVisionCandidate(profiles))
            {
                throw new LlmException(LlmErrorKind.Unknown,
                    "没有可用的模型：请先在设置页添加并连通一个规划模型（文本能力）；"
                    + "若图纸标注不全，还需添加具备视觉能力的模型。");
            }

            string facts = BuildFactText(extract);
            bool textSufficient = TextLooksSufficient(extract);
            bool useVision = !textSufficient
                && HasVisionCandidate(profiles)
                && !string.IsNullOrEmpty(sheetImagePath)
                && File.Exists(sheetImagePath);

            string base64 = "";
            if (useVision)
            {
                byte[] bytes = File.ReadAllBytes(sheetImagePath);
                base64 = Convert.ToBase64String(bytes);
                Log.Info("Planner", $"工程图反建：文本事实不足 → 视觉路径，送图 {sheetImagePath}"
                    + $"（{base64.Length * 3 / 4 / 1024} KB base64）");
            }
            else
            {
                Log.Info("Planner", "工程图反建：纯文本路径（事实"
                    + (textSufficient ? "判定充足" : "判定不足，但无可用视觉模型/图纸图") + "）");
            }

            var messages = new List<LlmMessage>
            {
                LlmMessage.System(DomainSystemPrompt.Build()),
                LlmMessage.System(DrawingRules),
                BuildUserMessage(extract.Title, facts, useVision, base64)
            };

            ModelTask task = useVision ? ModelTask.VisionVerify : ModelTask.Plan;
            string lastError = null;
            for (int round = 0; round <= MaxCorrectionRounds; round++)
            {
                int currentRound = round;
                string raw = await _scheduler.ExecuteAsync(task, profiles,
                    (entry, client, c) =>
                    {
                        Log.Info("Planner", $"工程图反建请求 → {entry.Name}"
                            + $"（{(useVision ? "视觉" : "文本")}，第 {currentRound + 1} 轮）");
                        return client.ChatAsync(messages, new ChatRequestOptions
                        {
                            JsonMode = true,
                            Temperature = 0.2,
                            MaxTokens = 3000,
                            ContextTokens = 8192
                        }, null, c);
                    }, ct).ConfigureAwait(false);

                Log.Info("Planner", "工程图反建原始输出（截断 800）："
                    + (raw ?? "").Replace("\r", " ").Replace("\n", " ")
                        .Substring(0, Math.Min(800, (raw ?? "").Length)));

                if (TryBuildResult(raw, useVision, sheetImagePath, out var result, out lastError))
                {
                    return result;
                }
                Log.Warn("Planner", $"工程图反建第 {round + 1} 轮输出被拒：{lastError}");
                if (round < MaxCorrectionRounds)
                {
                    messages.Add(LlmMessage.Assistant(raw ?? "<空输出>"));
                    messages.Add(LlmMessage.User(
                        "你的输出未通过校验，错误如下：\n" + lastError +
                        "\n请修正后重新只输出完整 JSON（不要代码、不要解释）。"));
                }
            }

            throw new LlmException(LlmErrorKind.Protocol,
                "模型连续 " + (MaxCorrectionRounds + 1) + " 次未输出合法特征树 JSON。最后错误：\n" + lastError +
                "\n建议：换用 JSON 遵循能力更强的模型，或在对话中补充缺失的尺寸后让我重新按图建模。");
        }

        private static LlmMessage BuildUserMessage(string title, string facts,
            bool useVision, string base64)
        {
            string head =
                "下面是 SolidWorks 原生工程图「" + title + "」经确定性 API 提取的全部结构化事实。"
                + "这些事实是反建的唯一依据：不要打开、不要引用工程图的源模型文件，全部零件按事实重新构建。\n\n"
                + facts + "\n\n"
                + "请完成多视图归并并输出 FeatureTree JSON；关键尺寸仍缺失、无法定量时输出 clarify。";
            return useVision
                ? LlmMessage.UserVision(head + "\n另附当前图纸截图，用于核对标注未覆盖的形状与位置。", base64)
                : LlmMessage.User(head);
        }

        /// <summary>
        /// 文本事实充足性启发：≥2 标注且有可见圆边（盘/板/孔系可定量），或仅线性标注 ≥4（箱体类）。
        /// </summary>
        private static bool TextLooksSufficient(DrawingExtract extract)
        {
            int dims = extract.Sheets.SelectMany(s => s.Views).Sum(v => v.Dimensions.Count);
            int circles = extract.Sheets.SelectMany(s => s.Views).Sum(v => v.Circles.Count);
            if (dims >= 2 && circles >= 1) return true;
            if (dims >= 4) return true;
            return false;
        }

        // ==================== 事实文本构建 ====================

        private static string BuildFactText(DrawingExtract extract)
        {
            var sb = new StringBuilder();
            foreach (DrawingSheetExtract sheet in extract.Sheets)
            {
                sb.AppendLine($"图纸「{sheet.Name}」 幅面 {Fmt(sheet.WidthMm)}×{Fmt(sheet.HeightMm)} mm");
                foreach (DrawingViewExtract view in sheet.Views)
                {
                    string type = ViewTypeText(view.ViewType);
                    sb.AppendLine($"- 视图「{view.Name}」（{type}，方向："
                        + (string.IsNullOrEmpty(view.Orientation) ? "<投影/无命名方向>" : view.Orientation)
                        + $"，比例 1:{Fmt(view.Scale)}，图纸位置 ({Fmt(view.PosXMm)},{Fmt(view.PosYMm)}) mm）");
                    sb.AppendLine($"  视图轮廓框（含尺寸标注，仅供粗略参考，不可作精确尺寸）："
                        + $"约 {Fmt(view.OutlineWModelMm)}×{Fmt(view.OutlineHModelMm)} mm");
                    if (view.Dimensions.Count > 0)
                    {
                        sb.AppendLine("  尺寸标注（只信这些数值）：");
                        foreach (DrawingDimensionExtract d in view.Dimensions)
                        {
                            string pfx = NormalizeTokens(d.Prefix);
                            string sfx = NormalizeTokens(d.Suffix);
                            sb.AppendLine("    " + (string.IsNullOrEmpty(pfx) ? "" : pfx + " ")
                                + Fmt(d.ValueMm)
                                + (string.IsNullOrEmpty(sfx) ? "" : " " + sfx)
                                + $"（{d.Kind}；尺寸名 {d.FullName}）");
                        }
                    }
                    if (view.Circles.Count > 0)
                    {
                        sb.AppendLine("  可见圆边（模型空间坐标 mm；圆心 / 平面轴向 / 半径）：");
                        foreach (DrawingCircleExtract c in DedupCircles(view.Circles))
                        {
                            sb.AppendLine($"    圆心({Fmt(c.CenterXMm)},{Fmt(c.CenterYMm)},{Fmt(c.CenterZMm)}) "
                                + $"轴({Fmt(c.AxisX)},{Fmt(c.AxisY)},{Fmt(c.AxisZ)}) R{Fmt(c.RadiusMm)} "
                                + $"（⌀{Fmt(c.RadiusMm * 2)}）");
                        }
                    }
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static IEnumerable<DrawingCircleExtract> DedupCircles(List<DrawingCircleExtract> circles)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (DrawingCircleExtract c in circles.OrderByDescending(c => c.RadiusMm))
            {
                string key = Fmt(c.CenterXMm) + "|" + Fmt(c.CenterYMm) + "|"
                    + Fmt(c.CenterZMm) + "|" + Fmt(c.RadiusMm);
                if (seen.Add(key)) yield return c;
            }
        }

        private static string ViewTypeText(int t)
        {
            switch (t)
            {
                case 4: return "投影视图";
                case 7: return "命名模型视图";
                default: return "视图类型 " + t.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>SW 尺寸文本格式 token → 工程符号。</summary>
        private static string NormalizeTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "<MOD-DIAM>", "⌀" },
                { "<MOD-RADIUS>", "R" },
                { "<MOD-DEGREE>", "°" },
                { "<MOD-PM>", "±" },
                { "<MOD-SQUARE>", "□" },
                { "<MOD-COUNTERBORE>", "沉孔" },
                { "<MOD-COUNTERSINK>", "锥沉孔" },
                { "<MOD-DEPTH>", "深" }
            };
            foreach (var kv in map) text = text.Replace(kv.Key, kv.Value);
            return text.Trim();
        }

        /// <summary>数值格式化：近整数取整，否则三位有效小数。</summary>
        private static string Fmt(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 0.0005)
            {
                return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            }
            return Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }

        // ==================== 输出校验 ====================

        private static bool TryBuildResult(string raw, bool usedVision, string imagePath,
            out DrawingReversePlanResult result, out string error)
        {
            result = null;
            error = null;
            if (!PlanJsonParser.TryParse(raw, out var parsed, out error))
            {
                return false;
            }

            if (parsed.Type != PlannerResponseType.Plan)
            {
                result = new DrawingReversePlanResult
                {
                    UsedVision = usedVision,
                    PreparedImagePath = usedVision ? imagePath : "",
                    NeedClarify = true,
                    ClarifyText = string.IsNullOrWhiteSpace(parsed.Text)
                        ? "模型未能从工程图事实中识别出足够信息。"
                        : parsed.Text
                };
                return true;
            }

            var tree = parsed.Plan;
            if (tree == null || tree.Steps == null
                || !tree.Steps.Any(s => string.Equals(s.Kind, "sketch", StringComparison.OrdinalIgnoreCase)))
            {
                error = "反建结果缺少草图步骤（sketch）。";
                return false;
            }
            if (!tree.Steps.Any(s => string.Equals(s.Kind, "extrudeBoss", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Kind, "revolveBoss", StringComparison.OrdinalIgnoreCase)))
            {
                error = "反建结果缺少基体步骤（extrudeBoss 或 revolveBoss）。";
                return false;
            }

            var r = new DrawingReversePlanResult
            {
                Tree = tree,
                UsedVision = usedVision,
                PreparedImagePath = usedVision ? imagePath : ""
            };
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
                        foreach (var w in warns.Select(t => t?.ToString())
                            .Where(s => !string.IsNullOrWhiteSpace(s)))
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

        private const string DrawingRules =
            "【SolidWorks 原生工程图反建专项规则】\n"
            + "输入是 SolidWorks 工程图经 API 确定性提取的事实：每张图纸有多个模型视图，视图含方向、比例、"
            + "尺寸标注（前缀/数值/种类）与可见圆边（模型空间坐标：圆心、平面轴向、半径）。遵守上面完整特征树 Schema 的前提下，还必须：\n"
            + "1. 尺寸真值仲裁：只允许使用事实中「尺寸标注」列出的数值与「可见圆边」的圆心/半径，"
            + "视图轮廓框已被标注放大、仅作粗略参考，严禁据其反算精确尺寸；事实中没有的数字一律不得编造。\n"
            + "2. 多视图归并：同一零件的多个视图是同一几何体的不同投影，必须去重合并："
            + "从反映轮廓与孔分布的视图取截面形状（如俯视圆视图），从正视/侧视图取厚度与高度；"
            + "圆边轴(0,1,0)表示该圆位于 XZ 平面（俯视方向），轴(0,0,1)表示位于 XY 平面（正视方向）。\n"
            + "3. 坐标：以几何包围盒中心为草图原点（回转体/对称件 cx=0,cy=0）；"
            + "圆边坐标已是模型空间坐标，直接按对应平面映射，不要再做比例换算。\n"
            + "4. 造型选择：圆盘/法兰等回转体优先 extrudeBoss（圆视图 circle + depthMm 厚度），"
            + "也可用 revolveBoss（半截面 + centerline，360°）；板/箱体用 rectCenter/polyline + extrudeBoss；"
            + "孔系：每个通孔用独立草图 circle + extrudeCut {throughAll:true}，"
            + "同径孔位成圆周/线性分布时用 circularPattern/linearPattern 表达，孔位以圆边圆心为准。\n"
            + "5. 尽力映射：尽量覆盖事实中出现的全部特征（台阶、孔、圆角、倒角等）；"
            + "执行器不支持或事实明显不足的特征允许省略，但必须写入 warnings。\n"
            + "6. 信息不足（外轮廓/总体尺寸/孔位无法从标注与圆边定量）时，返回 "
            + "{\"type\":\"clarify\",\"text\":\"中文说明缺什么、建议用户补充什么\"}，禁止猜测。\n"
            + "7. 成功输出 {\"type\":\"plan\",\"plan\":{...}} 时在根对象附带 recognition 字段："
            + "{\"confidence\":0到1,\"warnings\":[\"中文注意事项\"],\"summary\":\"一句话摘要\"}。"
            + "confidence 标准：标注与圆边齐全、直接定量 0.9 左右；少量尺寸靠对称推断 0.6~0.8；关键尺寸缺失时不得出 plan。\n"
            + "8. materialGuess 无法判断时必须为 null，禁止照抄字段说明里的枚举字符串。\n"
            + "【嵌套层级红线】\n"
            + "- 草图实体必须放在步骤的 \"sketch\":{\"plane\":...,\"entities\":[...]} 里，严禁直接挂在 step 上；\n"
            + "- extrudeBoss/extrudeCut/revolveBoss 必须是 steps 数组中各自独立的步骤对象，用 \"sketchId\" 引用草图，"
            + "严禁与别的步骤挤在同一对象里；输出必须是完整闭合的 JSON，禁止重复片段与半截对象。\n"
            + "仍然只输出一个 JSON 对象，禁止输出代码、markdown 围栏与解释文字。";
    }
}
