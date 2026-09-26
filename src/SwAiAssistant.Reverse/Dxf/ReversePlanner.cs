using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SwAiAssistant.Planner.Schema;

namespace SwAiAssistant.Reverse.Dxf
{
    /// <summary>DXF 反建结果：特征树候选 + 置信度 + 识别摘要（展示在计划卡片，等待人工确认）。</summary>
    public sealed class ReversePlanResult
    {
        public DxfSheet Sheet { get; set; }
        public FeatureTree Tree { get; set; }
        /// <summary>0~1。</summary>
        public double Confidence { get; set; }
        /// <summary>高（≥0.85）| 中（≥0.6）| 低。</summary>
        public string ConfidenceLevel { get; set; } = "低";
        /// <summary>结构化识别摘要（中文，逐行展示）。</summary>
        public List<string> Summary { get; } = new List<string>();
        /// <summary>警告（尺寸冲突/孔被忽略/单位存疑等）。</summary>
        public List<string> Warnings { get; } = new List<string>();
        /// <summary>Schema 校验错误；非空时不可直接执行。</summary>
        public List<SchemaError> ValidationErrors { get; } = new List<SchemaError>();
        public bool CanBuild { get; set; }
        /// <summary>识别结果直接计算的期望体积 mm³（外轮廓−孔）×厚。</summary>
        public double ExpectedVolumeMm3 { get; set; }
        public double? ThicknessMm { get; set; }
        public bool ThicknessExplicit { get; set; }
        public string ThicknessSource { get; set; } = "";
    }

    /// <summary>
    /// 结构化 DXF → JSON 特征树候选（M8-T24）：
    /// 外轮廓（矩形/圆/多边形）→ top 草图 + 凸台拉伸（厚度经文本标注仲裁）；
    /// 每个孔（圆/异形闭合环）→ 独立 top 草图 + 双向贯穿切除；坐标以外轮廓中心归零。
    /// 纯函数式输出候选，绝不触碰 SolidWorks——建模只发生在人工点击计划卡片「执行」之后。
    /// </summary>
    public sealed class ReversePlanner
    {
        private static readonly Regex ThicknessCn =
            new Regex(@"(?:厚\s*度?|板\s*厚|壁\s*厚)\s*[:：]?\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);
        private static readonly Regex ThicknessEn =
            new Regex(@"(?:THICKNESS|THK|\bT\b)\s*[=＝:：]?\s*([0-9]+(?:\.[0-9]+)?)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ThicknessDelta =
            new Regex(@"δ\s*[:：]?\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);

        private readonly DxfParser _parser = new DxfParser();

        /// <summary>解析 DXF 并生成特征树候选（DWG 等抛 DxfParseException，消息为中文引导）。</summary>
        public ReversePlanResult Plan(string dxfPath)
        {
            DxfSheet sheet = _parser.Parse(dxfPath);
            return Build(sheet);
        }

        /// <summary>由已解析图纸生成候选（便于测试/复用）。</summary>
        public ReversePlanResult Build(DxfSheet sheet)
        {
            if (sheet == null) throw new ArgumentNullException(nameof(sheet));

            var result = new ReversePlanResult { Sheet = sheet };
            double scale = sheet.ScaleToMm <= 0 ? 1.0 : sheet.ScaleToMm;

            DxfLoop outer = sheet.Outer;
            double outerW = (outer.MaxX - outer.MinX) * scale;
            double outerH = (outer.MaxY - outer.MinY) * scale;
            double outerArea = outer.AreaAbs * scale * scale;
            double cenXr = (outer.MinX + outer.MaxX) / 2.0;
            double cenYr = (outer.MinY + outer.MaxY) / 2.0;
            DxfPoint Norm(DxfPoint p) => new DxfPoint((p.X - cenXr) * scale, (p.Y - cenYr) * scale);

            // ---- 厚度仲裁：文本标注优先，最小线性标注兜底 ----
            var textPool = new List<string>(sheet.TextLines);
            textPool.AddRange(sheet.Dimensions.Select(d => d.UserText));
            double? thick = FindAnnotatedThickness(textPool, out string thickSource);
            bool explicitThick = thick.HasValue;
            if (!thick.HasValue)
            {
                var lineDims = sheet.Dimensions
                    .Where(d => d.Kind == "linear" && d.MeasurementMm > 0)
                    .OrderBy(d => d.MeasurementMm)
                    .ToList();
                double minDim = Math.Min(outerW, outerH);
                var infer = lineDims.FirstOrDefault(d => d.MeasurementMm < minDim * 0.6);
                if (infer != null)
                {
                    thick = infer.MeasurementMm;
                    thickSource = $"最小线性标注 {infer.MeasurementMm:0.##}mm 推断（未找到厚度标注，请务必核对）";
                }
            }
            result.ThicknessMm = thick;
            result.ThicknessExplicit = explicitThick;
            result.ThicknessSource = thickSource ?? "";

            // ---- 识别摘要 ----
            result.Summary.Add($"图纸：{sheet.FileName}（单位 {sheet.UnitName}，换算 ×{scale:0.######} → mm）");
            result.Summary.Add(OuterDescription(outer, scale, outerW, outerH));
            foreach (var l in sheet.Layers)
            {
                result.Summary.Add($"图层「{l.Name}」：{l.Category} ×{l.EntityCount}");
            }
            result.Summary.Add(thick.HasValue
                ? $"板厚：{thick.Value:0.##}mm（{thickSource}）"
                : "板厚：未识别（需在计划中补充拉伸深度）");
            if (sheet.Holes.Count == 0)
            {
                result.Summary.Add("孔：0 个（无闭合内环）");
            }
            else
            {
                result.Summary.Add($"孔：{sheet.Holes.Count} 个（轮廓内闭合环，全部按双向贯穿切除）");
                for (int i = 0; i < sheet.Holes.Count; i++)
                {
                    var h = sheet.Holes[i];
                    var hc = Norm(h.Shape == DxfLoopShape.Circle ? h.Center : Bary(h));
                    if (h.Shape == DxfLoopShape.Circle)
                    {
                        result.Summary.Add(
                            $"  孔{i + 1}：φ{2 * h.Radius * scale:0.##}mm @ ({hc.X:0.##},{hc.Y:0.##})mm");
                    }
                    else
                    {
                        result.Summary.Add(
                            $"  孔{i + 1}：异形闭合环（{h.Source}，{h.Points.Count} 边）@ ({hc.X:0.##},{hc.Y:0.##})mm");
                    }
                }
            }
            foreach (var n in sheet.Notes) result.Warnings.Add(n);

            // ---- 标注/几何一致性交叉检查（仅影响置信度与警告） ----
            CrossCheckDimensions(sheet, outerW, outerH, scale, result);

            // ---- 期望体积 ----
            double holeArea = sheet.Holes.Sum(h => h.AreaAbs) * scale * scale;
            double expected = (outerArea - holeArea) * (thick ?? 0);
            result.ExpectedVolumeMm3 = expected;
            if (thick.HasValue) result.Summary.Add($"理论体积：{expected:0.#} mm³（外轮廓 − 孔）× 板厚，执行后自动回读 ΔV 校验。");

            // ---- 组装特征树 ----
            if (!thick.HasValue)
            {
                result.Warnings.Add("未找到板厚信息：无法生成凸台拉伸深度，已停在识别阶段。请在图纸中补充「t=数字」厚度标注后重新导入。");
                result.Confidence = 0;
                result.ConfidenceLevel = "低";
                return result;
            }

            var tree = new FeatureTree
            {
                Version = "1.0",
                Units = "mm",
                Part = new PartSpec { Name = SanitizePartName(sheet.FileName) }
            };

            var bossSketch = new SketchSpec { Plane = "top", Entities = { OuterEntity(outer, Norm, scale) } };
            tree.Steps.Add(new PlanStep
            {
                Id = "s1",
                Kind = "sketch",
                Title = "外轮廓草图",
                Sketch = bossSketch
            });
            tree.Steps.Add(new PlanStep
            {
                Id = "s2",
                Kind = "extrudeBoss",
                Title = "基体拉伸",
                SketchId = "s1",
                Extrude = new ExtrudeSpec { DepthMm = thick.Value }
            });

            for (int i = 0; i < sheet.Holes.Count; i++)
            {
                string skId = "s" + (3 + i * 2);
                string cutId = "s" + (4 + i * 2);
                var entity = HoleEntity(sheet.Holes[i], Norm, scale);
                tree.Steps.Add(new PlanStep
                {
                    Id = skId,
                    Kind = "sketch",
                    Title = $"孔{i + 1}草图",
                    Sketch = new SketchSpec { Plane = "top", Entities = { entity } }
                });
                tree.Steps.Add(new PlanStep
                {
                    Id = cutId,
                    Kind = "extrudeCut",
                    Title = $"孔{i + 1}贯穿切除",
                    SketchId = skId,
                    Extrude = new ExtrudeSpec { ThroughAll = true }
                });
            }

            result.Tree = tree;
            var errors = FeatureTreeValidator.Validate(tree);
            result.ValidationErrors.AddRange(errors);
            result.CanBuild = errors.Count == 0;

            // ---- 置信度 ----
            double conf = 0.3;                                  // 闭合外轮廓
            conf += explicitThick ? 0.25 : 0.08;               // 厚度来源
            conf += sheet.Holes.Count == 0 ? 0.2 : 0.2;        // 孔均为闭合环（解析阶段已保证环闭合）
            if (!string.Equals(sheet.UnitName, "Unitless", StringComparison.Ordinal)) conf += 0.1;
            if (errors.Count == 0
                && result.Warnings.Count == 0
                && sheet.Holes.All(h => h.Shape == DxfLoopShape.Circle || h.Points.Count <= 64))
            {
                conf += 0.15;
            }
            else conf += 0.05;
            result.Confidence = Math.Min(0.97, Math.Round(conf, 2));
            result.ConfidenceLevel = result.Confidence >= 0.85 ? "高" : result.Confidence >= 0.6 ? "中" : "低";

            result.Summary.Insert(1, $"识别置信度：{result.ConfidenceLevel}（{result.Confidence:0%}）——AI 图纸识别结果，请逐项核对尺寸后再点「执行计划」。");
            return result;
        }

        // ==================== 实体映射 ====================

        private static SketchEntity OuterEntity(DxfLoop outer, Func<DxfPoint, DxfPoint> norm, double scale)
        {
            if (outer.Shape == DxfLoopShape.Circle)
            {
                var c = norm(outer.Center);
                return new SketchEntity { Type = "circle", Cx = c.X, Cy = c.Y, Radius = outer.Radius * scale };
            }
            if (IsAxisAlignedRectangle(outer.Points, scale, out _, out _, out _, out _))
            {
                var p1 = norm(new DxfPoint(outer.MinX, outer.MinY));
                var p2 = norm(new DxfPoint(outer.MaxX, outer.MaxY));
                return new SketchEntity
                {
                    Type = "rectCorner",
                    X1 = p1.X, Y1 = p1.Y,
                    X2 = p2.X, Y2 = p2.Y
                };
            }
            return PolylineEntity(outer.Points, norm, closed: true);
        }

        private static SketchEntity HoleEntity(DxfLoop hole, Func<DxfPoint, DxfPoint> norm, double scale)
        {
            if (hole.Shape == DxfLoopShape.Circle)
            {
                var c = norm(hole.Center);
                return new SketchEntity { Type = "circle", Cx = c.X, Cy = c.Y, Radius = hole.Radius * scale };
            }
            return PolylineEntity(hole.Points, norm, closed: true);
        }

        private static SketchEntity PolylineEntity(IReadOnlyList<DxfPoint> pts,
            Func<DxfPoint, DxfPoint> norm, bool closed)
        {
            var e = new SketchEntity { Type = "polyline", Closed = closed };
            foreach (var p in pts)
            {
                var q = norm(p);
                e.Points.Add(new Point2 { X = Math.Round(q.X, 4), Y = Math.Round(q.Y, 4) });
            }
            return e;
        }

        private static bool IsAxisAlignedRectangle(IReadOnlyList<DxfPoint> pts, double scale,
            out double x1, out double y1, out double x2, out double y2)
        {
            x1 = y1 = x2 = y2 = 0;
            if (pts.Count != 4) return false;
            double tol = Math.Max(0.5 / scale, 0.01); // 原始坐标容差（0.5mm）
            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            foreach (var p in pts)
            {
                bool onVertical = Math.Abs(p.X - minX) <= tol || Math.Abs(p.X - maxX) <= tol;
                bool onHorizontal = Math.Abs(p.Y - minY) <= tol || Math.Abs(p.Y - maxY) <= tol;
                if (!onVertical || !onHorizontal) return false;
            }
            x1 = minX; y1 = minY; x2 = maxX; y2 = maxY;
            return (maxX - minX) > tol && (maxY - minY) > tol;
        }

        // ==================== 厚度 / 尺寸仲裁 ====================

        private double? FindAnnotatedThickness(IEnumerable<string> lines, out string source)
        {
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string line = raw.Replace(" ", "").Replace("　", "");
                double? v = MatchOne(ThicknessCn, line)
                    ?? MatchOne(ThicknessEn, line)
                    ?? MatchOne(ThicknessDelta, line);
                if (v.HasValue)
                {
                    source = $"文本标注「{raw.Trim()}」";
                    return v.Value;
                }
            }
            source = "";
            return null;
        }

        private static double? MatchOne(Regex rx, string input)
        {
            var m = rx.Match(input);
            if (!m.Success) return null;
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double v) && v > 0 && v < 10000)
            {
                return v;
            }
            return null;
        }

        private static void CrossCheckDimensions(DxfSheet sheet, double outerW, double outerH,
            double scale, ReversePlanResult result)
        {
            foreach (var d in sheet.Dimensions)
            {
                if (d.Kind == "linear" && d.MeasurementMm > 0)
                {
                    double v = d.MeasurementMm;
                    bool matchesOuter = Close(v, outerW) || Close(v, outerH);
                    if (!matchesOuter && result.ThicknessExplicit
                        && !Close(v, result.ThicknessMm ?? -1))
                    {
                        // 既不对应外轮廓边长也不对应板厚：可能是内部结构尺寸（孔距等），仅提示
                        result.Warnings.Add($"线性标注 {v:0.##}mm 未与外轮廓边长/板厚对应，已按几何轮廓建模，请核对孔位。");
                    }
                }
                else if (d.Kind == "diameter" && d.MeasurementMm > 0)
                {
                    bool matched = sheet.Holes.Any(h =>
                        h.Shape == DxfLoopShape.Circle
                        && Close(2 * h.Radius * scale, d.MeasurementMm));
                    if (!matched)
                    {
                        result.Warnings.Add($"直径标注 φ{d.MeasurementMm:0.##}mm 未找到对应圆形孔，已按几何轮廓建模，请核对。");
                    }
                }
            }
        }

        private static bool Close(double a, double b)
        {
            if (b <= 0) return false;
            return Math.Abs(a - b) / Math.Max(Math.Abs(b), 1e-9) <= 0.02;
        }

        // ==================== 杂项 ====================

        private static DxfPoint Bary(DxfLoop l)
        {
            double sx = 0, sy = 0;
            foreach (var p in l.Points) { sx += p.X; sy += p.Y; }
            return new DxfPoint(sx / l.Points.Count, sy / l.Points.Count);
        }

        private static string OuterDescription(DxfLoop outer, double scale, double w, double h)
        {
            if (outer.Shape == DxfLoopShape.Circle)
            {
                return $"外轮廓：圆盘 φ{2 * outer.Radius * scale:0.##}mm";
            }
            if (IsAxisAlignedRectangle(outer.Points, scale, out _, out _, out _, out _))
            {
                return $"外轮廓：矩形 {w:0.##}×{h:0.##}mm（{outer.Points.Count} 顶点）";
            }
            return $"外轮廓：{outer.Source}多边形（包围盒 {w:0.##}×{h:0.##}mm，{outer.Points.Count} 顶点）";
        }

        private static string SanitizePartName(string fileName)
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName ?? "");
            foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
            if (baseName.Length > 24) baseName = baseName.Substring(0, 24);
            return string.IsNullOrWhiteSpace(baseName) ? "DXF反建件" : baseName;
        }
    }
}
