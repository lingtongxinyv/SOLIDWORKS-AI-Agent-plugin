using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using SwAiAssistant.Core.Text;
using SwAiAssistant.Planner.Execution;
using SwAiAssistant.Planner.Schema;

namespace SwAiAssistant.Planner
{
    /// <summary>模型响应类型：建模计划 / 纯对话回答 / 澄清提问 / 结构化修改。</summary>
    public enum PlannerResponseType
    {
        Plan,
        Chat,
        Clarify,
        Edit
    }

    /// <summary>解析后的模型响应。</summary>
    public class PlannerResponse
    {
        public PlannerResponseType Type { get; set; }
        public FeatureTree Plan { get; set; }
        public string Text { get; set; } = "";
        public string RawJson { get; set; } = "";
        /// <summary>type=edit 时的结构化修改请求序列（T14 对话修改：改尺寸/改参数/追加/删除）。</summary>
        public List<EditRequest> Edits { get; } = new List<EditRequest>();
        /// <summary>type=edit 时模型给出的修改摘要（气泡展示）。</summary>
        public string EditSummary { get; set; } = "";
        /// <summary>
        /// 启发式安全告警（低-3）：JSON 字符串值内出现可执行代码特征关键字时记录。
        /// 不阻断解析（下游是强类型确定性执行器，无代码执行面），仅由调用方写日志/审计。
        /// </summary>
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// 模型输出解析器（安全红线：只接受 JSON，绝不执行任何模型产出的代码）。
    /// 兼容 ```json 围栏与前后多余文本；提取第一个平衡花括号块；
    /// 检测到代码内容（python/C#/VBA 等）时拒绝并给出纠正提示。
    /// </summary>
    public static class PlanJsonParser
    {
        private static readonly string[] CodeMarkers =
        {
            "import ", "def ", "Sub ", "Dim swApp", "CSharpCodeProvider", "Process.Start",
            "public class", "static void Main", "#include", "function(", "powershell"
        };

        /// <summary>从模型输出提取 JSON 文本；失败时 error 给出中文原因。</summary>
        public static bool TryExtractJson(string raw, out string json, out string error)
        {
            json = null;
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "模型未返回任何内容。";
                return false;
            }

            string text = raw.Trim();
            // 去掉 markdown 围栏
            if (text.StartsWith("```"))
            {
                int firstNl = text.IndexOf('\n');
                if (firstNl > 0) text = text.Substring(firstNl + 1);
                int end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (end >= 0) text = text.Substring(0, end);
                text = text.Trim();
            }

            int start = text.IndexOf('{');
            if (start < 0)
            {
                error = "输出中未找到 JSON 对象。请只输出 JSON 特征树，不要输出代码或解释文字。";
                return false;
            }

            int depth = 0;
            bool inString = false;
            bool escape = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        json = text.Substring(start, i - start + 1);
                        // JSON 之外的残留内容含代码标记 → 拒绝
                        string outside = text.Remove(start, i - start + 1);
                        if (ContainsCode(outside))
                        {
                            error = "输出在 JSON 之外夹带了代码内容。本插件只执行 JSON 特征树，请重新只输出 JSON。";
                            json = null;
                            return false;
                        }
                        return true;
                    }
                }
            }
            error = "JSON 花括号不平衡（输出可能被截断），请重新输出完整 JSON。";
            return false;
        }

        private static bool ContainsCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var marker in CodeMarkers)
            {
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 值内告警专用关键字（比外围 CodeMarkers 更聚焦「可执行内容」，降低中文零件名误伤；
        /// 如 "powershell"、"cmd /c" 等）。命中只告警审计，绝不作为执行输入。
        /// </summary>
        private static readonly string[] ValueCodeMarkers =
        {
            "powershell", "cmd.exe", "cmd /c", "wscript.shell", "createobject(",
            "invoke-expression", "invoke-command", "process.start", "frombase64string",
            "<script", "<?xml", "csharpprovider", "assembly.load"
        };

        /// <summary>遍历 JSON 全部字符串值，命中代码特征关键字时返回告警（最多 3 条，值截断 40 字）。</summary>
        private static List<string> ScanValueWarnings(JContainer root)
        {
            var warnings = new List<string>();
            foreach (var token in root.Descendants())
            {
                if (token.Type != JTokenType.String) continue;
                string value = token.ToString();
                foreach (var marker in ValueCodeMarkers)
                {
                    int idx = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    string snippet = value.Length > 40 ? value.Substring(0, 40) + "…" : value;
                    warnings.Add($"JSON 字符串值内含疑似可执行内容标记「{marker}」（值片段：{snippet}）；已忽略该内容不执行。");
                    break; // 一个值只报一次
                }
                if (warnings.Count >= 3) return warnings;
            }
            return warnings;
        }

        /// <summary>
        /// 解析完整响应（含 Schema 校验）。错误信息直接可反馈给模型纠正。
        /// </summary>
        public static bool TryParse(string raw, out PlannerResponse response, out string error)
        {
            response = null;
            error = null;

            if (!TryExtractJson(raw, out string json, out error))
            {
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                error = "JSON 语法错误：" + ex.Message;
                return false;
            }

            string type = root["type"]?.ToString() ?? "";
            var result = new PlannerResponse { RawJson = json };
            response = result;

            // 低-3：字符串值内代码关键字启发式（只告警不阻断，下游无代码执行面）
            foreach (string w in ScanValueWarnings(root))
            {
                result.Warnings.Add(w);
            }
            switch ((type ?? "").ToLowerInvariant())
            {
                case "plan":
                    result.Type = PlannerResponseType.Plan;
                    var planToken = root["plan"];
                    if (planToken == null)
                    {
                        error = "type=plan 但缺少 plan 字段。";
                        return false;
                    }
                    FeatureTree plan;
                    try
                    {
                        plan = planToken.ToObject<FeatureTree>(Newtonsoft.Json.JsonSerializer.Create(Json.Settings));
                    }
                    catch (Exception ex)
                    {
                        error = "特征树字段类型错误：" + ex.Message;
                        return false;
                    }
                    var errors = FeatureTreeValidator.Validate(plan);
                    if (errors.Count > 0)
                    {
                        error = "特征树 Schema 校验失败：\n" + FeatureTreeValidator.FormatErrors(errors);
                        return false;
                    }
                    result.Plan = plan;
                    return true;
                case "chat":
                case "clarify":
                    result.Type = type.Equals("clarify", StringComparison.OrdinalIgnoreCase)
                        ? PlannerResponseType.Clarify : PlannerResponseType.Chat;
                    result.Text = root["text"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(result.Text))
                    {
                        error = "type=" + type + " 但 text 为空。";
                        return false;
                    }
                    return true;
                case "edit":
                    result.Type = PlannerResponseType.Edit;
                    result.EditSummary = root["summary"]?.ToString() ?? "";
                    var editsToken = root["edits"] as JArray;
                    if (editsToken == null || editsToken.Count == 0)
                    {
                        error = "type=edit 但 edits 为空：对已有零件的修改至少包含 1 条修改请求。";
                        return false;
                    }
                    for (int i = 0; i < editsToken.Count; i++)
                    {
                        if (!(editsToken[i] is JObject ej))
                        {
                            error = "edits[" + i + "] 必须是 JSON 对象。";
                            return false;
                        }
                        string intentRaw = ej["intent"]?.ToString() ?? "";
                        if (!Enum.TryParse(intentRaw, true, out EditIntent intent)
                            || !Enum.IsDefined(typeof(EditIntent), intent))
                        {
                            error = "edits[" + i + "] 的 intent「" + intentRaw
                                + "」非法，只能是 changeDimension / changeFeatureParam / addFeature / deleteFeature。";
                            return false;
                        }
                        var req = new EditRequest { Intent = intent };
                        req.TargetFeatureName = ej["targetFeatureName"]?.ToString();
                        req.DimensionFullName = ej["dimensionFullName"]?.ToString();
                        double? nv = ej["newValueMm"]?.Value<double?>();
                        if (nv.HasValue) req.NewValueMm = nv.Value;

                        if (intent == EditIntent.ChangeDimension || intent == EditIntent.ChangeFeatureParam)
                        {
                            if (string.IsNullOrWhiteSpace(req.DimensionFullName))
                            {
                                error = "edits[" + i + "] 改尺寸缺少 dimensionFullName（须取自上下文给出的尺寸全名 D1@特征名）。";
                                return false;
                            }
                            if (!(req.NewValueMm > 0))
                            {
                                error = "edits[" + i + "] 改尺寸的 newValueMm 必须为正数。";
                                return false;
                            }
                        }
                        if (intent == EditIntent.DeleteFeature
                            && string.IsNullOrWhiteSpace(req.TargetFeatureName))
                        {
                            error = "edits[" + i + "] 删除特征缺少 targetFeatureName。";
                            return false;
                        }
                        if (intent == EditIntent.AddFeature)
                        {
                            var addPlanToken = ej["plan"];
                            if (addPlanToken == null)
                            {
                                error = "edits[" + i + "] addFeature 缺少 plan 增量特征树。";
                                return false;
                            }
                            FeatureTree addPlan;
                            try
                            {
                                addPlan = addPlanToken.ToObject<FeatureTree>(
                                    Newtonsoft.Json.JsonSerializer.Create(Json.Settings));
                            }
                            catch (Exception ex)
                            {
                                error = "edits[" + i + "] 增量特征树字段类型错误：" + ex.Message;
                                return false;
                            }
                            var addErrors = FeatureTreeValidator.Validate(addPlan);
                            if (addErrors.Count > 0)
                            {
                                error = "edits[" + i + "] 增量特征树 Schema 校验失败：\n"
                                    + FeatureTreeValidator.FormatErrors(addErrors);
                                return false;
                            }
                            req.NewFeatureTree = addPlan;
                        }
                        result.Edits.Add(req);
                    }
                    return true;
                default:
                    error = "未知响应类型「" + type + "」，type 只能是 plan / edit / chat / clarify。";
                    return false;
            }
        }
    }
}
