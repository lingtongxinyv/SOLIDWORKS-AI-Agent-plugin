using System;
using System.Collections.Generic;
using System.Linq;
using SwAiAssistant.Planner.Schema;
using SwAiAssistant.Verify.Theory;

namespace SwAiAssistant.Verify
{
    /// <summary>
    /// 强校验闭环核心（M4-T18）：执行前理论预算（TheoryBudget）vs 执行后真实回读（质量属性/包围盒/孔数）
    /// 的偏差计算与判定。COM 无关——实际值由调用方（Cad 层 QueryService/QAService）测得后传入，便于单测。
    /// 偏差阈值来自配置 VerifyThresholdPct（%），默认 5%。
    /// 判定口径：体积偏差与包围盒三边偏差均 ≤ 阈值才 Passed；
    /// 孔数为精确相等的辅助检查（阵列/降级计数可能不精确），不一致只告警不判负。
    /// </summary>
    public sealed class VerifyService
    {
        /// <summary>
        /// 对比特征树理论值与实际回读值，生成校验报告。
        /// theory 为 null 时内部自动 TheoryBudget.Evaluate(plan)；理论体积≈0（全估算件）时跳过体积判定。
        /// </summary>
        /// <param name="actualHoleCount">实测孔数；传 -1 表示未知（不参与检查）。</param>
        public VerifyReport Verify(FeatureTree plan,
            double actualVolumeMm3, double actualMassKg, BoxTheory actualBoundingBox,
            double thresholdPct, TheoryReport theory = null, int actualHoleCount = -1)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            theory = theory ?? TheoryBudget.Evaluate(plan);
            double threshold = thresholdPct > 0 ? thresholdPct / 100.0 : 0.05;

            var report = new VerifyReport
            {
                PartName = plan.Part?.Name ?? "AI零件",
                Theory = theory,
                ActualVolumeMm3 = actualVolumeMm3,
                ActualMassKg = actualMassKg,
                ActualBoundingBox = actualBoundingBox,
                Threshold = threshold,
                HasEstimates = theory.Estimates.Count > 0
            };

            bool volumePassed = CheckVolume(report, theory, actualVolumeMm3, threshold);
            bool boxPassed = CheckBoundingBox(report, theory, actualBoundingBox, threshold);
            CheckHoleCount(report, theory, actualHoleCount);

            if (Math.Abs(theory.VolumeMm3) < 1e-9)
            {
                // 理论体积为 0：全部步骤无法预算，体积不参与判定，仅按包围盒判定（通常也无）
                report.Passed = boxPassed;
                report.Warnings.Add("理论预算不可用（特征全部不可解析），未做体积偏差判定。");
            }
            else
            {
                report.Passed = volumePassed && boxPassed;
            }

            if (report.Passed && report.HasEstimates)
            {
                report.Warnings.Add("含估算特征（" + string.Join("；", theory.Estimates) + "），判定精度有限。");
            }
            return report;
        }

        private static bool CheckVolume(VerifyReport report, TheoryReport theory, double actual, double threshold)
        {
            if (Math.Abs(theory.VolumeMm3) < 1e-9) return true; // 全估算件：无体积基线
            report.VolumeDeviation = Math.Abs(actual - theory.VolumeMm3) / Math.Abs(theory.VolumeMm3);
            if (report.VolumeDeviation > threshold)
            {
                report.Warnings.Add($"体积偏差 {report.VolumeDeviation:P2} 超过阈值 {threshold:P0}，"
                    + "请选择：采纳（保留模型）/ 回滚（撤销本次建模）/ 让 AI 修复。");
                return false;
            }
            return true;
        }

        private static bool CheckBoundingBox(VerifyReport report, TheoryReport theory,
            BoxTheory actual, double threshold)
        {
            if (theory == null || actual == null) return true;
            double[] tEdges = { theory.BoundingBox.X, theory.BoundingBox.Y, theory.BoundingBox.Z };
            double[] aEdges = { actual.X, actual.Y, actual.Z };
            if (tEdges.Max() < 1.0 || aEdges.Max() < 1.0) return true; // 无有效盒数据（如未建成实体）

            // 三边按长度排序后逐边比，不依赖草图平面→全局轴的映射方向
            Array.Sort(tEdges);
            Array.Sort(aEdges);
            double maxDev = 0;
            var bad = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                if (tEdges[i] < 1.0) continue;
                double dev = Math.Abs(aEdges[i] - tEdges[i]) / tEdges[i];
                maxDev = Math.Max(maxDev, dev);
                if (dev > threshold) bad.Add($"{tEdges[i]:0.#}→{aEdges[i]:0.#} mm（{dev:P1}）");
            }
            report.BoundingBoxDeviation = maxDev;
            if (bad.Count > 0)
            {
                report.Warnings.Add("包围盒边长偏差超阈值：" + string.Join("，", bad)
                    + $"（最大 {maxDev:P1}，阈值 {threshold:P0}）。请核对拉伸深度/草图尺寸。");
                return false;
            }
            return true;
        }

        private static void CheckHoleCount(VerifyReport report, TheoryReport theory, int actual)
        {
            if (actual < 0) return; // 未知，不检查
            report.HoleCountMatches = actual == theory.HoleCount;
            if (!report.HoleCountMatches.Value)
            {
                // 阵列实例与降级切除计数可能不精确，只告警不判负
                report.Warnings.Add($"孔数与理论不一致：理论 {theory.HoleCount} 个，实测 {actual} 个"
                    + "（阵列/异型孔降级路径计数可能有偏差，供人工核对）。");
            }
        }
    }
}
