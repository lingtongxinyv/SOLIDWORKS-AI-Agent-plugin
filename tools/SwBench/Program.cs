using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SwAiAssistant.Ai;
using SwAiAssistant.Ai.Scheduling;
using SwAiAssistant.Cad;
using SwAiAssistant.Cad.Documents;
using SwAiAssistant.Cad.Features;
using SwAiAssistant.Cad.Geometry;
using SwAiAssistant.Cad.Queries;
using SwAiAssistant.Cad.Session;
using SwAiAssistant.Cad.Sketching;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Threading;
using SwAiAssistant.Planner;
using SwAiAssistant.Planner.Execution;
using SwAiAssistant.Planner.Schema;
using SwAiAssistant.Verify;
using SwAiAssistant.Verify.Theory;

namespace SwAiAssistant.SwBench
{
    /// <summary>
    /// 基准件评测台（M5-T20）：固化 10 个板类/支座类基准件指令，
    /// 一轮跑完记录一次通过率 / AI 修复轮次 / ΔV / 耗时，输出 CSV + Markdown 报告。
    /// 用法：SwBench.exe [--visible] [--out 目录] [--only 1,3,5] [--round 标签]
    ///   云端轮：配置好云端模型后直接运行；
    ///   Ollama 轮（TR-20.2 离线条件）：停用全部云端模型、仅留 Ollama 后运行。
    /// 退出码：0 = 评测完成（不代表全部通过）；1 = 评测台自身失败。
    /// </summary>
    internal static class Program
    {
        /// <summary>固化基准件指令集（tasks.md T20：10 个板类/支座类）。</summary>
        private static readonly (string Id, string Title, string Instruction)[] Parts =
        {
            ("B01", "安装板", "做一块 100×80×10mm 的安装板，四角各有一个 φ8 通孔，孔心距板边 15mm。"),
            ("B02", "垫板", "做一块 60×60×5mm 的方形垫板，中心钻一个 φ20 通孔。"),
            ("B03", "法兰盘", "做一个法兰盘：圆盘外径 φ120、厚 12mm，中心 φ40 通孔，周围均布 6 个 φ10 通孔，孔心在 φ90 的分度圆上。"),
            ("B04", "L 支架", "做一个 L 形支架：底板 80 长×50 宽×8 厚，一端立起 60 高×50 宽×8 厚的立板，立板上打两个 φ8 通孔，底板四角各一个 φ10 通孔。"),
            ("B05", "三角筋板座", "做一个支座：底板 100×60×10mm，中央竖一块 60 高×8 厚的立板，立板两侧各加一块三角筋板，筋板厚 6mm。"),
            ("B06", "轴承座简化件", "做一个简化轴承座：底板 120×70×12mm，中部凸台宽 40、总高 50（含底板厚），凸台中央沿宽度方向一个 φ30 通孔，底板四角各一个 φ10 安装孔。"),
            ("B07", "电机安装板", "做一块 150×120×8mm 的电机安装板，中心 φ70 通孔，φ100 分度圆上均布 4 个 φ9 通孔，板四角各一个 φ11 通孔、孔心距边 12mm。"),
            ("B08", "槽口调节板", "做一块 120×50×8mm 的调节板，板中央沿长度方向开一条长 60、宽 10 的通槽，板两端各一个 φ8 通孔。"),
            ("B09", "带沉孔盖板", "做一块 90×90×8mm 的盖板，四角各一个 M8 沉头孔（沉孔 φ14 深 4、通孔 φ8.5），孔心距边 12mm，中心一个 φ25 通孔。"),
            ("B10", "多孔支座", "做一个多孔支座：底板 100×80×10mm，四角 φ9 通孔距边 12mm；中部立板 60 高×8 厚×80 宽，立板上水平均布 3 个 φ10 通孔。"),
        };

        private sealed class Row
        {
            public string Id, Title, ModelNames = "", Notes = "";
            public bool PlanOk, VerifyPassed;
            public int FixRounds;
            public double DeltaVPct = double.NaN;
            public double Seconds;
            /// <summary>一次通过 = 计划一次成功且零修复执行成功且校验通过。</summary>
            public bool FirstPass => PlanOk && FixRounds == 0 && VerifyPassed;
            /// <summary>最终通过 = 经修复后执行成功且校验通过。</summary>
            public bool FinalPass => VerifyPassed;
        }

        private static int Main(string[] args)
        {
            AppPaths.Ensure();
            SwAiAssistant.Core.Logging.Log.DirectoryOverride =
                Path.Combine(Path.GetTempPath(), "SwAiAssistant-bench", "logs");
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("== SwBench：基准件评测台（M5-T20）==");

            bool visible = args.Contains("--visible", StringComparer.OrdinalIgnoreCase);
            string round = ArgValue(args, "--round") ?? "round";
            string outDir = ArgValue(args, "--out")
                ?? Path.Combine(Path.GetTempPath(), "SwAiAssistant-bench",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + round);
            var only = (ArgValue(args, "--only") ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var parts = Parts.Where(p => only.Count == 0 || only.Contains(p.Id)).ToArray();
            if (parts.Length == 0)
            {
                Console.WriteLine("[失败] --only 过滤后无基准件。");
                return 1;
            }

            var config = ConfigService.Default;
            var enabled = config.EnabledModels().ToList();
            if (enabled.Count == 0)
            {
                Console.WriteLine("[失败] 无已启用模型：请先在插件设置页配置模型（云端或 Ollama）。");
                return 1;
            }
            string modelNames = string.Join("、", enabled.Select(m => m.Name));
            Console.WriteLine("参评模型：" + modelNames);
            Console.WriteLine("基准件数：" + parts.Length + "　输出目录：" + outDir);
            Directory.CreateDirectory(outDir);

            var rows = new List<Row>();
            using (var sta = new StaExecutor("SwBenchSTA"))
            {
                SwSession session = null;
                try
                {
                    session = SwSession.ConnectOrStart(sta, visible, out bool startedNew);
                    Console.WriteLine(startedNew ? "已启动新 SolidWorks 实例。" : "已接管运行中的 SolidWorks。");

                    var docs = new DocService(session);
                    var executor = new PlanExecutor(docs,
                        new SketchService(session), new FeatureService(session),
                        new QueryService(session), new GeometryService(session));
                    var probe = new CapabilityProbe(config);
                    var scheduler = new Scheduler(config, probe);
                    var planner = new PlannerService(scheduler, config);
                    var profiles = ProbeProfiles(config, probe);
                    double threshold = config.Current.VerifyThresholdPct;

                    foreach (var part in parts)
                    {
                        var row = RunOne(part, planner, executor, profiles, threshold, modelNames);
                        row.ModelNames = modelNames;
                        rows.Add(row);
                        Console.WriteLine();
                    }

                    if (startedNew)
                    {
                        try { sta.Run(() => session.App.CloseAllDocuments(true)); } catch { /* 忽略 */ }
                    }
                    else
                    {
                        Console.WriteLine("[提示] 接管模式：评测文档保留在 SolidWorks 中，请手动关闭。");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[评测台失败] " + ex);
                    return 1;
                }
                finally
                {
                    session?.Dispose();
                }
            }

            WriteReports(outDir, round, rows);
            PrintSummary(rows);
            return 0;
        }

        /// <summary>单件评测：计划 → Schema 校验（失败可 AI 重生 1 次）→ 带 AI 修复重试执行 → ΔV 校验。</summary>
        private static Row RunOne((string Id, string Title, string Instruction) part,
            PlannerService planner, PlanExecutor executor,
            IReadOnlyDictionary<string, ModelProfile> profiles, double thresholdPct, string modelNames)
        {
            var row = new Row { Id = part.Id, Title = part.Title, ModelNames = modelNames };
            var sw = Stopwatch.StartNew();
            Console.WriteLine($"-- {part.Id} {part.Title} --");
            Console.WriteLine("   指令：" + part.Instruction);

            planner.ClearHistory();
            FeatureTree plan = null;
            try
            {
                var resp = planner.RequestAsync(part.Instruction, null, profiles, null,
                    CancellationToken.None).GetAwaiter().GetResult();
                if (resp.Type == PlannerResponseType.Plan && resp.Plan != null)
                {
                    plan = resp.Plan;
                }
                else
                {
                    row.Notes = "模型未返回计划（" + resp.Type + "）";
                }
            }
            catch (Exception ex)
            {
                row.Notes = "计划请求异常：" + ex.Message;
            }

            // Schema 校验；未通过给 AI 一次重生机会（计 1 轮修复）
            if (plan != null)
            {
                var errors = FeatureTreeValidator.Validate(plan);
                if (errors.Count > 0)
                {
                    row.FixRounds++;
                    Console.WriteLine("   [修复] Schema 未通过（" + errors[0] + "），请求 AI 重生…");
                    plan = Regenerate(planner, profiles, "特征树校验未通过：" + errors[0] + "。请修正后重新输出完整 JSON。", ref row);
                }
            }

            if (plan == null)
            {
                row.PlanOk = false;
                row.Seconds = sw.Elapsed.TotalSeconds;
                Console.WriteLine("   [结果] 计划阶段失败：" + row.Notes);
                return row;
            }
            row.PlanOk = true;
            Console.WriteLine($"   计划 OK：{plan.Steps.Count} 步" + (row.FixRounds > 0 ? $"（已修复 {row.FixRounds} 轮）" : ""));

            // 执行（AI 修复重试 ≤2 次，每次新建文档不残留半成品参与校验）
            ExecutionReport report = null;
            try
            {
                report = executor.ExecuteWithRetry(plan, _ => DocChoice.CreateNew, CancellationToken.None,
                    (current, errorText) =>
                    {
                        row.FixRounds++;
                        Console.WriteLine($"   [修复] 执行失败，AI 第 {row.FixRounds} 轮修正（{Shorten(errorText)}）…");
                        return Regenerate(planner, profiles, "SolidWorks 执行报错：" + errorText + "。请分析原因并输出修正后的完整特征树 JSON。", ref row);
                    }, maxRetries: 2);
            }
            catch (Exception ex)
            {
                row.Notes = TrimNotes(row.Notes, "执行失败：" + ex.Message);
                row.Seconds = sw.Elapsed.TotalSeconds;
                Console.WriteLine("   [结果] 执行失败：" + Shorten(ex.Message));
                return row;
            }

            // 强校验：理论预算 vs 实际回读
            var box = report.BoundingBox != null
                ? new BoxTheory { X = report.BoundingBox.SizeX, Y = report.BoundingBox.SizeY, Z = report.BoundingBox.SizeZ }
                : null;
            var verify = new VerifyService().Verify(plan, report.VolumeMm3, report.MassKg, box, thresholdPct);
            row.VerifyPassed = verify.Passed;
            row.DeltaVPct = verify.VolumeDeviation * 100;
            row.Notes = TrimNotes(row.Notes, string.Join("；", verify.Warnings));
            row.Seconds = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"   [结果] ΔV={row.DeltaVPct:F2}%（阈值 {thresholdPct}%）→ {(verify.Passed ? "通过" : "超限")}，"
                + $"体积 {report.VolumeMm3:F0} mm³，修复 {row.FixRounds} 轮，{row.Seconds:F0}s");
            return row;
        }

        /// <summary>请求 AI 修正/重生计划；返回 null 表示无法获得合法计划。</summary>
        private static FeatureTree Regenerate(PlannerService planner,
            IReadOnlyDictionary<string, ModelProfile> profiles, string feedback, ref Row row)
        {
            try
            {
                var resp = planner.RequestAsync(feedback, null, profiles, null, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (resp.Type == PlannerResponseType.Plan && resp.Plan != null)
                {
                    var errors = FeatureTreeValidator.Validate(resp.Plan);
                    if (errors.Count == 0) return resp.Plan;
                    row.Notes = TrimNotes(row.Notes, "修正后仍未过校验：" + errors[0]);
                }
                else
                {
                    row.Notes = TrimNotes(row.Notes, "AI 修正未返回计划");
                }
            }
            catch (Exception ex)
            {
                row.Notes = TrimNotes(row.Notes, "AI 修正异常：" + ex.Message);
            }
            return null;
        }

        private static IReadOnlyDictionary<string, ModelProfile> ProbeProfiles(
            ConfigService config, CapabilityProbe probe)
        {
            var dict = new Dictionary<string, ModelProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in config.EnabledModels())
            {
                var p = probe.GetCached(entry.Id, entry.Model);
                if (p == null)
                {
                    try { p = probe.ProbeAsync(entry, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (Exception ex) { Console.WriteLine($"   [提示] 探测「{entry.Name}」失败：{ex.Message}"); }
                }
                if (p != null) dict[entry.Id] = p;
            }
            return dict;
        }

        private static void PrintSummary(List<Row> rows)
        {
            int first = rows.Count(r => r.FirstPass), final = rows.Count(r => r.FinalPass);
            Console.WriteLine();
            Console.WriteLine("== 汇总 ==");
            foreach (var r in rows)
            {
                Console.WriteLine($"  {r.Id} {r.Title}：{(r.FirstPass ? "一次通过" : r.FinalPass ? $"修复 {r.FixRounds} 轮后通过" : "未通过")}"
                    + (double.IsNaN(r.DeltaVPct) ? "" : $"，ΔV={r.DeltaVPct:F2}%") + $"，{r.Seconds:F0}s");
            }
            Console.WriteLine($"一次通过率 {first}/{rows.Count}（{100.0 * first / rows.Count:F0}%），"
                + $"最终通过率 {final}/{rows.Count}（{100.0 * final / rows.Count:F0}%）");
        }

        private static void WriteReports(string outDir, string round, List<Row> rows)
        {
            string csv = Path.Combine(outDir, "bench-" + round + ".csv");
            var sb = new StringBuilder();
            sb.AppendLine("id,title,models,plan_ok,first_pass,final_pass,fix_rounds,delta_v_pct,seconds,notes");
            foreach (var r in rows)
            {
                sb.AppendLine(string.Join(",", r.Id, Csv(r.Title), Csv(r.ModelNames),
                    r.PlanOk ? 1 : 0, r.FirstPass ? 1 : 0, r.FinalPass ? 1 : 0, r.FixRounds,
                    double.IsNaN(r.DeltaVPct) ? "" : r.DeltaVPct.ToString("F2"),
                    r.Seconds.ToString("F0"), Csv(r.Notes)));
            }
            File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));

            string md = Path.Combine(outDir, "bench-" + round + ".md");
            int first = rows.Count(r => r.FirstPass), final = rows.Count(r => r.FinalPass);
            var mb = new StringBuilder();
            mb.AppendLine("# SwBench 基准件评测报告（" + round + "）");
            mb.AppendLine();
            mb.AppendLine("- 时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            mb.AppendLine("- 参评模型：" + rows.FirstOrDefault()?.ModelNames);
            mb.AppendLine($"- 一次通过率：{first}/{rows.Count}；最终通过率：{final}/{rows.Count}");
            mb.AppendLine();
            mb.AppendLine("| 编号 | 基准件 | 一次通过 | 最终通过 | 修复轮次 | ΔV% | 耗时s | 备注 |");
            mb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var r in rows)
            {
                mb.AppendLine($"| {r.Id} | {r.Title} | {(r.FirstPass ? "✓" : "✗")} | {(r.FinalPass ? "✓" : "✗")} | {r.FixRounds}"
                    + $" | {(double.IsNaN(r.DeltaVPct) ? "-" : r.DeltaVPct.ToString("F2"))} | {r.Seconds:F0} | {r.Notes.Replace("|", "/")} |");
            }
            File.WriteAllText(md, mb.ToString(), new UTF8Encoding(true));
            Console.WriteLine("报告已写入：" + md);
        }

        private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\n", " ") + "\"";
        private static string Shorten(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 80 ? s : s.Substring(0, 80) + "…");
        private static string TrimNotes(string old, string add)
            => string.IsNullOrWhiteSpace(add) ? old : (string.IsNullOrEmpty(old) ? add : old + "；" + add);

        private static string ArgValue(string[] args, string key)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }
    }
}
