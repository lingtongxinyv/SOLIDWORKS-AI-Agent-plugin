using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using netDxf;
using netDxf.Entities;
using netDxf.Units;

namespace SwAiAssistant.Reverse.Dxf
{
    /// <summary>
    /// DXF 结构化提取器（M8-T24）：
    /// 图层分类 → 闭合轮廓（圆/闭合多段线/线弧链/整椭圆）→ 最大环=外轮廓、其余=孔；
    /// 标注真值与文本原样保留（厚度仲裁在 ReversePlanner）；单位经 $INSUNITS 折算毫米。
    /// 纯托管实现，不触碰 SolidWorks。
    /// </summary>
    public sealed class DxfParser
    {
        /// <summary>DWG 等不支持格式时的统一中文引导。</summary>
        public const string DwgGuidance =
            "DWG 为专有二进制格式，本插件不内嵌任何盗版转换组件。请先转换为 DXF 后再导入：" +
            "① ODA File Converter（免费，支持批量转 2018 ASCII DXF）；" +
            "② 或在 AutoCAD/中望 CAD 中「另存为 → AutoCAD 2018 DXF(*.dxf)」；" +
            "③ 或在 SolidWorks 中打开工程图后另存为 DXF。";

        private const double ChordTolMm = 0.1;   // 圆弧离散弦高公差（mm）
        private const double EndpointTolMm = 0.5; // 线弧链端点吸附容差（mm）

        public DxfSheet Parse(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new DxfParseException("未选择图纸文件。");
            if (!File.Exists(path)) throw new DxfParseException("图纸文件不存在：" + path);

            string ext = Path.GetExtension(path);
            if (ext.Equals(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                throw new DxfParseException(DwgGuidance);
            }
            if (!ext.Equals(".dxf", StringComparison.OrdinalIgnoreCase))
            {
                throw new DxfParseException("仅支持 .dxf 文本图纸（收到 " + ext + "）。" + DwgGuidance);
            }

            DxfDocument dxf;
            try
            {
                dxf = DxfDocument.Load(path);
            }
            catch (Exception ex)
            {
                throw new DxfParseException(
                    "DXF 解析失败（文件可能损坏或版本过新）：" + ex.Message + "。建议另存为 AutoCAD 2018 ASCII DXF 后重试。", ex);
            }
            if (dxf == null) throw new DxfParseException("DXF 解析返回空，请检查文件内容后重试。");

            double scale = UnitScaleToMm(dxf.DrawingVariables.InsUnits, out string unitName);
            double tolRaw = EndpointTolMm / scale;
            double chordRaw = ChordTolMm / scale;

            var sheet = new DxfSheet
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                UnitName = unitName,
                ScaleToMm = scale
            };
            var layerCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            var loops = new List<DxfLoop>();
            var looseSegments = new List<Segment>();

            // ---- 圆（语义保留） ----
            foreach (var c in dxf.Entities.Circles)
            {
                if (!IsUsable(c)) continue;
                CountLayer(layerCounts, c.Layer?.Name);
                if (!(c.Radius > 0)) continue;
                var loop = CircleLoop(new DxfPoint(c.Center.X, c.Center.Y), c.Radius,
                    c.Layer?.Name ?? "", "圆");
                loops.Add(loop);
            }

            // ---- 闭合多段线（凸度弧段离散） ----
            foreach (var pl in dxf.Entities.Polylines2D)
            {
                if (!IsUsable(pl)) continue;
                CountLayer(layerCounts, pl.Layer?.Name);
                if (pl.Vertexes == null || pl.Vertexes.Count < 2) continue;
                if (pl.IsClosed)
                {
                    var pts = TessellatePolyline(pl.Vertexes, closed: true, chord: chordRaw);
                    if (pts.Count >= 3) loops.Add(PolygonLoop(pts, pl.Layer?.Name ?? "", "闭合多段线"));
                }
                else
                {
                    AddPolylineSegments(looseSegments, pl.Vertexes, chord: chordRaw, layer: pl.Layer?.Name ?? "");
                }
            }

            // ---- 整椭圆 → 离散多边形；非整椭圆暂不支持 ----
            int partialEllipse = 0;
            foreach (var el in dxf.Entities.Ellipses)
            {
                if (!IsUsable(el)) continue;
                CountLayer(layerCounts, el.Layer?.Name);
                if (el.IsFullEllipse && el.MajorAxis > 0 && el.MinorAxis > 0)
                {
                    var pts = TessellateEllipse(el.Center.X, el.Center.Y,
                        el.MajorAxis / 2.0, el.MinorAxis / 2.0, el.Rotation, chord: chordRaw);
                    loops.Add(PolygonLoop(pts, el.Layer?.Name ?? "", "整椭圆"));
                }
                else partialEllipse++;
            }

            // ---- 零散直线 / 圆弧（先离散成弦段，再做端点链接环） ----
            foreach (var ln in dxf.Entities.Lines)
            {
                if (!IsUsable(ln)) continue;
                CountLayer(layerCounts, ln.Layer?.Name);
                looseSegments.Add(Segment.Of(
                    new DxfPoint(ln.StartPoint.X, ln.StartPoint.Y),
                    new DxfPoint(ln.EndPoint.X, ln.EndPoint.Y)));
            }
            foreach (var arc in dxf.Entities.Arcs)
            {
                if (!IsUsable(arc)) continue;
                CountLayer(layerCounts, arc.Layer?.Name);
                if (!(arc.Radius > 0)) continue;
                var pts = TessellateArc(arc.Center.X, arc.Center.Y, arc.Radius,
                    arc.StartAngle, arc.EndAngle, chord: chordRaw);
                AddPointChainSegments(looseSegments, pts);
            }

            foreach (var chain in BuildChains(looseSegments, tolRaw))
            {
                if (chain.Count >= 3) loops.Add(PolygonLoop(chain, "线弧链", "线弧链闭合环"));
            }

            // ---- 标注真值 ----
            foreach (var d in dxf.Entities.Dimensions)
            {
                if (!IsUsable(d)) continue;
                CountLayer(layerCounts, d.Layer?.Name);
                string kind = DimKind(d.GetType().Name);
                sheet.Dimensions.Add(new DxfDimInfo
                {
                    Kind = kind,
                    MeasurementMm = d.Measurement * scale,
                    UserText = d.UserText ?? "",
                    LayerName = d.Layer?.Name ?? ""
                });
            }

            // ---- 文本（厚度/说明仲裁用） ----
            foreach (var t in dxf.Entities.Texts)
            {
                if (!IsUsable(t)) continue;
                CountLayer(layerCounts, t.Layer?.Name);
                if (!string.IsNullOrWhiteSpace(t.Value)) sheet.TextLines.Add(t.Value.Trim());
            }
            foreach (var mt in dxf.Entities.MTexts)
            {
                if (!IsUsable(mt)) continue;
                CountLayer(layerCounts, mt.Layer?.Name);
                if (!string.IsNullOrWhiteSpace(mt.Value)) sheet.TextLines.Add(StripMText(mt.Value));
            }

            // ---- 块参照/样条等暂不支持，记录说明 ----
            int inserts = dxf.Entities.Inserts.Count(IsUsable);
            int splines = dxf.Entities.Splines.Count(IsUsable);
            if (inserts > 0) sheet.Notes.Add($"检测到 {inserts} 个块参照（INSERT），未展开识别，请将其炸开（EXPLODE）后重新导入。");
            if (splines > 0) sheet.Notes.Add($"检测到 {splines} 条样条曲线，未参与轮廓识别（简单板件可忽略）。");
            if (partialEllipse > 0) sheet.Notes.Add($"检测到 {partialEllipse} 个非整椭圆（局部椭圆弧），未参与轮廓识别。");

            // ---- 外轮廓 / 孔 ----
            if (loops.Count == 0)
            {
                throw new DxfParseException(
                    "未识别到任何闭合轮廓（圆/闭合多段线/首尾相连的线弧）。请确认图纸为俯视图轮廓、线条未做成块参照。");
            }
            foreach (var l in loops) FillMetrics(l);
            DxfLoop outer = loops.Aggregate((a, b) => b.AreaAbs > a.AreaAbs ? b : a);
            sheet.Outer = outer;
            var others = loops.Where(l => !ReferenceEquals(l, outer)).ToList();

            // 孔去重（圆心距 0.1mm 且半径差 1% 内视为同一孔）
            var seen = new List<DxfLoop>();
            foreach (var h in others)
            {
                DxfPoint hc = LoopCenter(h);
                bool dup = seen.Any(s =>
                {
                    DxfPoint sc = LoopCenter(s);
                    double dCen = Dist(sc, hc);
                    double rCmp = Math.Max(s.Radius, h.Radius);
                    return dCen < 0.1 / scale && Math.Abs(s.Radius - h.Radius) <= Math.Max(0.01, rCmp * 0.01);
                });
                if (!dup) seen.Add(h);
            }
            foreach (var h in seen)
            {
                DxfPoint hc = LoopCenter(h);
                if (PointInLoop(hc, outer)) sheet.Holes.Add(h);
                else sheet.Notes.Add($"图层「{h.LayerName}」上一个闭合环位于外轮廓之外，已作为非孔轮廓忽略（{h.Source}）。");
            }

            // ---- 图层分类输出 ----
            sheet.Layers.AddRange(layerCounts
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new DxfLayerInfo
                {
                    Name = kv.Key,
                    Category = ClassifyLayer(kv.Key),
                    EntityCount = kv.Value
                }));

            if (string.Equals(unitName, "Unitless", StringComparison.Ordinal))
            {
                sheet.Notes.Add("图纸未声明单位（$INSUNITS=0），已按毫米解释；若尺寸整体差 10/25.4 倍，请在 CAD 中设置单位后另存。");
            }
            return sheet;
        }

        // ==================== 几何构造 ====================

        private static DxfLoop CircleLoop(DxfPoint c, double r, string layer, string source)
        {
            var l = new DxfLoop
            {
                Shape = DxfLoopShape.Circle,
                Center = c,
                Radius = r,
                LayerName = layer,
                Source = source
            };
            FillMetrics(l);
            return l;
        }

        private static DxfLoop PolygonLoop(List<DxfPoint> pts, string layer, string source)
        {
            // 统一逆时针
            if (SignedArea(pts) < 0) pts.Reverse();
            var l = new DxfLoop
            {
                Shape = DxfLoopShape.Polygon,
                LayerName = layer,
                Source = source
            };
            l.Points.AddRange(pts);
            FillMetrics(l);
            return l;
        }

        private static void FillMetrics(DxfLoop l)
        {
            IReadOnlyList<DxfPoint> pts;
            if (l.Shape == DxfLoopShape.Circle)
            {
                l.AreaAbs = Math.PI * l.Radius * l.Radius;
                l.MinX = l.Center.X - l.Radius; l.MaxX = l.Center.X + l.Radius;
                l.MinY = l.Center.Y - l.Radius; l.MaxY = l.Center.Y + l.Radius;
                return;
            }
            pts = l.Points;
            double a = Math.Abs(SignedArea(pts));
            l.AreaAbs = a;
            double minX = pts[0].X, maxX = pts[0].X, minY = pts[0].Y, maxY = pts[0].Y;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            l.MinX = minX; l.MaxX = maxX; l.MinY = minY; l.MaxY = maxY;
            if (l.Shape == DxfLoopShape.Polygon)
            {
                // 用面积/外接矩形等效半径供去重比较（圆孔 Shape=Circle 时用真实半径）
                l.Radius = Math.Sqrt(a / Math.PI);
                l.Center = BaryCenter(pts);
            }
        }

        private static DxfPoint LoopCenter(DxfLoop l)
        {
            if (l.Shape == DxfLoopShape.Circle) return l.Center;
            return BaryCenter(l.Points);
        }

        private static double SignedArea(IReadOnlyList<DxfPoint> pts)
        {
            double a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var q = pts[(i + 1) % pts.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2.0;
        }

        private static DxfPoint BaryCenter(IReadOnlyList<DxfPoint> pts)
        {
            // 顶点均值（仅用于位置判定，不参与面积）
            double sx = 0, sy = 0;
            foreach (var p in pts) { sx += p.X; sy += p.Y; }
            return new DxfPoint(sx / pts.Count, sy / pts.Count);
        }

        /// <summary>点是否在闭合环内（射线法；环内/边界都算）。</summary>
        private static bool PointInLoop(DxfPoint p, DxfLoop loop)
        {
            IReadOnlyList<DxfPoint> pts;
            if (loop.Shape == DxfLoopShape.Circle)
            {
                double rr = loop.Radius * 1.02;
                return Dist(p, loop.Center) <= rr;
            }
            pts = loop.Points;
            bool inside = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                var a = pts[j];
                var b = pts[i];
                if (((a.Y > p.Y) != (b.Y > p.Y))
                    && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        // ==================== 圆弧离散与凸度 ====================

        private static List<DxfPoint> TessellatePolyline(IList<Polyline2DVertex> verts, bool closed, double chord)
        {
            var result = new List<DxfPoint>();
            int n = verts.Count;
            int segCount = closed ? n : n - 1;
            for (int i = 0; i < segCount; i++)
            {
                var v1 = verts[i];
                var v2 = verts[(i + 1) % n];
                var p1 = new DxfPoint(v1.Position.X, v1.Position.Y);
                var p2 = new DxfPoint(v2.Position.X, v2.Position.Y);
                result.Add(p1);
                double b = v1.Bulge;
                if (Math.Abs(b) > 1e-12)
                {
                    result.AddRange(BulgePoints(p1, p2, b, chord, includeStart: false, includeEnd: false));
                }
            }
            return result;
        }

        private static void AddPolylineSegments(List<Segment> segs, IList<Polyline2DVertex> verts, double chord, string layer)
        {
            for (int i = 0; i < verts.Count - 1; i++)
            {
                var v1 = verts[i];
                var v2 = verts[i + 1];
                var p1 = new DxfPoint(v1.Position.X, v1.Position.Y);
                var p2 = new DxfPoint(v2.Position.X, v2.Position.Y);
                if (Math.Abs(v1.Bulge) > 1e-12)
                {
                    AddPointChainSegments(segs,
                        BulgePoints(p1, p2, v1.Bulge, chord, includeStart: true, includeEnd: true));
                }
                else if (Dist(p1, p2) > 1e-9)
                {
                    segs.Add(Segment.Of(p1, p2));
                }
            }
        }

        /// <summary>
        /// 凸度段离散：bulge b=tan(θ/4)，b&gt;0 为逆时针弧。
        /// </summary>
        private static List<DxfPoint> BulgePoints(DxfPoint p1, DxfPoint p2, double b, double chord,
            bool includeStart, bool includeEnd)
        {
            double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
            double c = Math.Sqrt(dx * dx + dy * dy);
            var pts = new List<DxfPoint>();
            if (c < 1e-12) return pts;

            double theta = 4.0 * Math.Atan(Math.Abs(b)); // 圆心角 0..2π
            double r = c * (1.0 + b * b) / (4.0 * Math.Abs(b));
            double apothem = c * (1.0 - b * b) / (4.0 * b); // 有符号：沿弦右法向
            // 右法向（顺时针旋转 90°）
            var center = new DxfPoint(
                (p1.X + p2.X) / 2.0 + dy / c * apothem,
                (p1.Y + p2.Y) / 2.0 - dx / c * apothem);

            double a1 = Math.Atan2(p1.Y - center.Y, p1.X - center.X);
            double a2 = Math.Atan2(p2.Y - center.Y, p2.X - center.X);
            if (b > 0) { while (a2 < a1) a2 += 2.0 * Math.PI; }
            else { while (a2 > a1) a2 -= 2.0 * Math.PI; }

            var arc = new List<DxfPoint>();
            int steps = ArcSteps(theta, r, chord);
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                double a = a1 + (a2 - a1) * t;
                arc.Add(new DxfPoint(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a)));
            }
            for (int i = includeStart ? 0 : 1; i < arc.Count - (includeEnd ? 0 : 1); i++)
            {
                pts.Add(arc[i]);
            }
            return pts;
        }

        private static List<DxfPoint> TessellateArc(double cx, double cy, double r,
            double a1, double a2, double chord)
        {
            while (a2 < a1) a2 += 2.0 * Math.PI;
            double theta = a2 - a1;
            int steps = ArcSteps(theta, r, chord);
            var pts = new List<DxfPoint>();
            for (int i = 0; i <= steps; i++)
            {
                double a = a1 + theta * i / steps;
                pts.Add(new DxfPoint(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
            }
            return pts;
        }

        private static List<DxfPoint> TessellateEllipse(double cx, double cy,
            double a, double b2, double rot, double chord)
        {
            double tol = Math.Min(chord, Math.Max(a, b2) * 0.005);
            int steps = Math.Max(24, (int)Math.Ceiling(2.0 * Math.PI / (2.0 * Math.Acos(1.0 - tol / Math.Max(a, 1e-9)))));
            steps = Math.Min(steps, 256);
            double cos = Math.Cos(rot), sin = Math.Sin(rot);
            var pts = new List<DxfPoint>();
            for (int i = 0; i < steps; i++)
            {
                double t = 2.0 * Math.PI * i / steps;
                double ex = a * Math.Cos(t), ey = b2 * Math.Sin(t);
                pts.Add(new DxfPoint(cx + ex * cos - ey * sin, cy + ex * sin + ey * cos));
            }
            return pts;
        }

        private static int ArcSteps(double theta, double r, double chordTol)
        {
            if (r <= 1e-12) return 1;
            double cosStep = 1.0 - chordTol / r;
            if (cosStep < -1.0) cosStep = -1.0;
            double da = 2.0 * Math.Acos(cosStep);
            int steps = da > 1e-9 ? (int)Math.Ceiling(theta / da) : 1;
            return Math.Max(2, Math.Min(steps, 256));
        }

        // ==================== 线弧链接环 ====================

        private struct Segment
        {
            public DxfPoint A;
            public DxfPoint B;
            public static Segment Of(DxfPoint a, DxfPoint b) => new Segment { A = a, B = b };
        }

        private static void AddPointChainSegments(List<Segment> segs, IList<DxfPoint> pts)
        {
            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (Dist(pts[i], pts[i + 1]) > 1e-9) segs.Add(Segment.Of(pts[i], pts[i + 1]));
            }
        }

        /// <summary>端点贪心链接：产出闭合多边形点列（非闭合链丢弃，简单板件轮廓均闭合）。</summary>
        private static IEnumerable<List<DxfPoint>> BuildChains(List<Segment> segs, double tol)
        {
            var unused = new List<Segment>(segs);
            var results = new List<List<DxfPoint>>();
            while (unused.Count > 0)
            {
                var chain = new List<DxfPoint>();
                var cur = unused[0];
                unused.RemoveAt(0);
                chain.Add(cur.A);
                chain.Add(cur.B);

                bool grew = true;
                while (grew)
                {
                    grew = false;
                    DxfPoint tail = chain[chain.Count - 1];
                    for (int i = 0; i < unused.Count; i++)
                    {
                        var s = unused[i];
                        if (Dist(s.A, tail) <= tol)
                        {
                            chain.Add(s.B);
                            unused.RemoveAt(i);
                            grew = true;
                            break;
                        }
                        if (Dist(s.B, tail) <= tol)
                        {
                            chain.Add(s.A);
                            unused.RemoveAt(i);
                            grew = true;
                            break;
                        }
                    }
                    if (Dist(chain[0], chain[chain.Count - 1]) <= tol) break;
                }

                if (Dist(chain[0], chain[chain.Count - 1]) <= tol && chain.Count >= 4)
                {
                    chain.RemoveAt(chain.Count - 1); // 去掉重合末点
                    if (chain.Count >= 3) results.Add(chain);
                }
            }
            return results;
        }

        private static double Dist(DxfPoint a, DxfPoint b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ==================== 单位 / 图层 / 杂项 ====================

        private static double UnitScaleToMm(DrawingUnits u, out string name)
        {
            name = u.ToString();
            switch (u)
            {
                case DrawingUnits.Millimeters: return 1.0;
                case DrawingUnits.Centimeters: return 10.0;
                case DrawingUnits.Meters: return 1000.0;
                case DrawingUnits.Decimeters: return 100.0;
                case DrawingUnits.Decameters: return 10000.0;
                case DrawingUnits.Hectometers: return 100000.0;
                case DrawingUnits.Kilometers: return 1_000_000.0;
                case DrawingUnits.Inches:
                case DrawingUnits.USSurveyInches:
                case DrawingUnits.Mils: // 密耳按千分之一英寸
                    return u == DrawingUnits.Mils ? 0.0254 : 25.4;
                case DrawingUnits.Feet:
                case DrawingUnits.USSurveyFeet:
                    return 304.8;
                case DrawingUnits.Yards:
                case DrawingUnits.USSurveyYards:
                    return 914.4;
                case DrawingUnits.Miles:
                case DrawingUnits.USSurveyMiles:
                    return 1_609_344.0;
                case DrawingUnits.Microinches: return 25.4e-6;
                case DrawingUnits.Angstroms: return 1e-7;
                case DrawingUnits.Nanometers: return 1e-6;
                case DrawingUnits.Microns: return 1e-3;
                default: return 1.0; // Unitless 等：按毫米
            }
        }

        private static bool IsUsable(EntityObject e)
        {
            try { return e != null && e.IsVisible; }
            catch { return e != null; }
        }

        private static void CountLayer(IDictionary<string, int> counts, string layer)
        {
            string key = string.IsNullOrWhiteSpace(layer) ? "0" : layer;
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        private static string ClassifyLayer(string name)
        {
            string s = (name ?? "").ToLowerInvariant();
            if (ContainsAny(s, "center", "中心", "轴线", "点划线")) return "center";
            if (ContainsAny(s, "hatch", "剖面", "填充")) return "hatch";
            if (ContainsAny(s, "dim", "标注", "尺寸")) return "dimension";
            if (ContainsAny(s, "text", "文字", "注释", "notes", "note")) return "text";
            if (ContainsAny(s, "outline", "轮廓", "粗实", "0")) return "outline";
            return "other";
        }

        private static bool ContainsAny(string s, params string[] keys)
            => keys.Any(k => s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        private static string DimKind(string typeName)
        {
            if (typeName.IndexOf("Diametric", StringComparison.OrdinalIgnoreCase) >= 0) return "diameter";
            if (typeName.IndexOf("Radial", StringComparison.OrdinalIgnoreCase) >= 0) return "radial";
            if (typeName.IndexOf("Angular", StringComparison.OrdinalIgnoreCase) >= 0) return "angular";
            if (typeName.IndexOf("Ordinate", StringComparison.OrdinalIgnoreCase) >= 0) return "ordinate";
            if (typeName.IndexOf("Linear", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Aligned", StringComparison.OrdinalIgnoreCase) >= 0) return "linear";
            return "other";
        }

        /// <summary>粗清 MText 控制码（\\P 换行、\\L..\\l、{ }、\\A 等），保留可读文本。</summary>
        private static string StripMText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new System.Text.StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (ch == '{') continue;
                if (ch == '}') continue;
                if (ch == '\\')
                {
                    if (i + 1 < raw.Length)
                    {
                        char n = raw[i + 1];
                        if (n == 'P' || n == 'p') { sb.Append(' '); i++; continue; }
                        if (n == '\\') { sb.Append('\\'); i++; continue; }
                        if (n == '{' || n == '}') { sb.Append(n); i++; continue; }
                        // 反斜杠开头的格式码：跳到字母序列结束
                        i++;
                        while (i + 1 < raw.Length && char.IsLetter(raw[i + 1])) i++;
                        continue;
                    }
                    continue;
                }
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }
    }
}
