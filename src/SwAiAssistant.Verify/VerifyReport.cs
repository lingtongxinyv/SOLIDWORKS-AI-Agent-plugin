using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SwAiAssistant.Verify.Theory;

namespace SwAiAssistant.Verify
{
    /// <summary>
    /// 强校验报告（M4-T18）：理论预算 vs 真实回读对比结论。
    /// Passed=false 时 UI 弹「采纳/回滚/让 AI 修复」三选项。
    /// </summary>
    public sealed class VerifyReport
    {
        public string PartName { get; set; } = "";
        public TheoryReport Theory { get; set; }
        public double ActualVolumeMm3 { get; set; }
        public double ActualMassKg { get; set; }
        public BoxTheory ActualBoundingBox { get; set; }
        /// <summary>体积相对偏差（0..1）：|实际−理论|/|理论|。</summary>
        public double VolumeDeviation { get; set; }
        /// <summary>包围盒三边中的最大相对偏差（0..1）；理论或实际盒缺失时为 null（不参与判定）。</summary>
        public double? BoundingBoxDeviation { get; set; }
        /// <summary>孔数是否与理论一致；无法取得实际孔数（-1）时为 null（不参与判定）。</summary>
        public bool? HoleCountMatches { get; set; }
        /// <summary>判定阈值（0..1，来自配置 VerifyThresholdPct/100）。</summary>
        public double Threshold { get; set; }
        public bool Passed { get; set; }
        /// <summary>理论含估算特征（圆角/拔模等无法精确预算）时为 true，对比仅供参考。</summary>
        public bool HasEstimates { get; set; }
        /// <summary>视觉复核可用（有视觉模型且截图成功）。</summary>
        public bool VisionAvailable { get; set; }
        /// <summary>视觉复核结论文本；不可用时的降级说明。</summary>
        public string VisionConclusion { get; set; } = "";
        public List<string> ScreenshotPaths { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>中文报告卡片文本（对话气泡/报警弹窗共用）。</summary>
        public string SummaryText()
        {
            var sb = new StringBuilder();
            sb.Append(Passed ? "校验通过" : "校验偏差超限");
            sb.Append($"：零件「{PartName}」");
            if (Theory != null)
            {
                sb.Append($"\n理论体积 {Theory.VolumeMm3:F1} mm³，实际 {ActualVolumeMm3:F1} mm³"
                    + $"，偏差 {VolumeDeviation:P2}（阈值 {Threshold:P0}）");
                if (Theory.HoleCount > 0)
                {
                    sb.Append($"\n孔：{Theory.HoleCount} 个（φ{string.Join("、φ", Theory.HoleDiametersMm.Select(d => d.ToString("0.##")))}）");
                }
            }
            if (ActualBoundingBox != null)
            {
                sb.Append($"\n包围盒 {ActualBoundingBox.X:F1}×{ActualBoundingBox.Y:F1}×{ActualBoundingBox.Z:F1} mm");
                if (BoundingBoxDeviation.HasValue)
                {
                    sb.Append($"，边长最大偏差 {BoundingBoxDeviation.Value:P2}");
                }
            }
            if (HoleCountMatches.HasValue && !HoleCountMatches.Value)
            {
                sb.Append($"\n孔数不一致（理论 {Theory?.HoleCount ?? 0} 个）");
            }
            sb.Append($"\n质量 {ActualMassKg:F3} kg");
            if (HasEstimates)
            {
                sb.Append("\n（含估算特征，体积对比仅供参考）");
            }
            if (!string.IsNullOrEmpty(VisionConclusion))
            {
                sb.Append("\n视觉复核：" + VisionConclusion);
            }
            foreach (string w in Warnings)
            {
                sb.Append("\n警告：" + w);
            }
            return sb.ToString();
        }
    }
}
