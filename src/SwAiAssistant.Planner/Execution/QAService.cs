using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SwAiAssistant.Cad.Queries;
using SwAiAssistant.Cad.Session;

namespace SwAiAssistant.Planner.Execution
{
    /// <summary>面向 LLM 的问答载荷：一次 Capture 的结构化回读快照。</summary>
    public sealed class QaSnapshot
    {
        /// <summary>质量（kg，MKS）。</summary>
        public double MassKg { get; set; }

        /// <summary>体积（mm³）。</summary>
        public double VolumeMm3 { get; set; }

        /// <summary>表面积（m²，MKS）。</summary>
        public double SurfaceAreaM2 { get; set; }

        /// <summary>包围盒三边（毫米）。</summary>
        public double BoxX { get; set; }
        public double BoxY { get; set; }
        public double BoxZ { get; set; }

        /// <summary>特征清单（AI 特征名后附「（AI）」标记）。</summary>
        public List<string> Features { get; } = new List<string>();

        /// <summary>孔特征数。</summary>
        public int HoleCount { get; set; }

        /// <summary>孔径表（毫米，去重）。</summary>
        public List<double> HoleDiametersMm { get; } = new List<double>();

        /// <summary>采集说明（降级/失败信息，供 LLM 如实回答）。</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// 真实问答回读（T14）：把 QueryService/DimensionService 的结构化数据
    /// 组装成面向 LLM 的问答载荷，并提供确定性关键词应答。
    /// </summary>
    public sealed class QAService
    {
        private readonly SwSession _session;
        private readonly QueryService _query;
        private readonly DimensionService _dims;
        private readonly FeatureRegistry _registry;

        public QAService(SwSession session, QueryService query, DimensionService dims, FeatureRegistry registry)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _dims = dims ?? throw new ArgumentNullException(nameof(dims));
            _registry = registry; // 允许 null：无注册表时仅不做 AI 标记/登记计数
        }

        /// <summary>采集一次问答快照（质量/包围盒/特征清单/孔统计）。单项失败记 Note 不中断。</summary>
        public QaSnapshot Capture(IModelDoc2 doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var snap = new QaSnapshot();

            try
            {
                var mp = _query.GetMassProps(doc);
                snap.MassKg = mp.MassKg;
                snap.VolumeMm3 = mp.VolumeMm3;
                snap.SurfaceAreaM2 = mp.SurfaceAreaM2;
            }
            catch (Exception ex)
            {
                snap.Notes.Add("质量属性读取失败：" + ex.Message);
            }

            try
            {
                var box = _query.GetBoundingBoxMm(doc);
                snap.BoxX = box.SizeX;
                snap.BoxY = box.SizeY;
                snap.BoxZ = box.SizeZ;
            }
            catch (Exception ex)
            {
                snap.Notes.Add("包围盒读取失败：" + ex.Message);
            }

            // 特征清单 + 孔统计（枚举经 Cad 层 QueryService，内部 STA 封送）
            foreach (var info in _query.GetFeatureInfos(doc))
            {
                string name = info.Name;
                bool isAi = _registry != null && _registry.IsAiFeature(name);
                snap.Features.Add(isAi ? name + "（AI）" : name);
                if (IsHoleFeature(info.TypeName, name))
                {
                    snap.HoleCount++;
                }
            }

            if (snap.HoleCount == 0 && _registry != null)
            {
                // 降级切除路径：切除特征名不含「孔」，按注册表登记的 holeWizard 记录计数
                snap.HoleCount = _registry.All()
                    .Count(r => string.Equals(r.Kind, "holeWizard", StringComparison.OrdinalIgnoreCase));
            }

            // 孔径表：尺寸枚举中属于孔类特征、且在合理孔径范围 (0, 200) mm。
            // 降级切除路径的孔径尺寸挂在「草图N」特征上（特征名不含「孔」）——
            // 当注册表登记过 holeWizard 记录时，把草图特征里 (0,200) 的 D 尺寸一并纳入。
            bool hasHoleRecord = _registry != null && _registry.All()
                .Any(r => string.Equals(r.Kind, "holeWizard", StringComparison.OrdinalIgnoreCase));
            try
            {
                var dims = _dims.ListDimensions(doc);
                foreach (var d in dims)
                {
                    bool holeOwned = d.FeatureName != null
                        && (d.FeatureName.IndexOf("孔", StringComparison.Ordinal) >= 0
                            || (hasHoleRecord && d.FeatureName.IndexOf("草图", StringComparison.Ordinal) >= 0));
                    if (holeOwned && d.ValueMm > 0 && d.ValueMm < 200
                        && !snap.HoleDiametersMm.Contains(d.ValueMm))
                    {
                        snap.HoleDiametersMm.Add(d.ValueMm);
                    }
                }
                if (snap.HoleCount > 0 && snap.HoleDiametersMm.Count == 0)
                {
                    snap.Notes.Add("孔径统计待尺寸枚举（降级切除路径草图无尺寸标注）。");
                }
            }
            catch (Exception ex)
            {
                snap.Notes.Add("尺寸枚举失败：" + ex.Message);
            }

            return snap;
        }

        /// <summary>
        /// 确定性关键词应答：多重/质量→质量，包围盒/尺寸/多大→三边，
        /// 几个孔/孔数→孔数，体积→体积；默认返回零件摘要。
        /// </summary>
        public string AnswerQuestion(QaSnapshot s, string question)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            string q = question ?? "";

            if (q.Contains("孔"))
            {
                string dia = s.HoleDiametersMm.Count > 0
                    ? "，孔径 " + string.Join("、", s.HoleDiametersMm.Select(v => v.ToString("0.##"))) + " mm"
                    : (s.Notes.Count > 0 ? "（" + string.Join("；", s.Notes) + "）" : "");
                return $"该零件共有 {s.HoleCount} 个孔{dia}。";
            }
            if (q.Contains("多重") || q.Contains("质量"))
            {
                return $"该零件质量约 {s.MassKg:F4} kg（体积 {s.VolumeMm3:F1} mm³）。";
            }
            if (q.Contains("体积"))
            {
                return $"该零件体积约 {s.VolumeMm3:F1} mm³。";
            }
            if (q.Contains("包围盒") || q.Contains("尺寸") || q.Contains("多大") || q.Contains("外形"))
            {
                return $"包围盒三边约 {s.BoxX:F1} × {s.BoxY:F1} × {s.BoxZ:F1} mm。";
            }
            return $"零件摘要：质量 {s.MassKg:F4} kg，体积 {s.VolumeMm3:F1} mm³，"
                + $"包围盒 {s.BoxX:F1}×{s.BoxY:F1}×{s.BoxZ:F1} mm，"
                + $"特征 {s.Features.Count} 个，孔 {s.HoleCount} 个。";
        }

        /// <summary>孔特征判定：异型孔向导类型，或特征名含「孔」。</summary>
        private static bool IsHoleFeature(string typeName, string name)
        {
            return typeName == "HoleWizard"
                || (name != null && name.IndexOf("孔", StringComparison.Ordinal) >= 0);
        }
    }
}
