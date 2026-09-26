using System;
using System.Collections.Generic;
using SwAiAssistant.Planner.Schema;

namespace SwAiAssistant.Verify.Theory
{
    /// <summary>理论包围盒（毫米，各轴尺寸 = 极值差）。</summary>
    public class BoxTheory
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public override string ToString() => $"({X:0.###},{Y:0.###},{Z:0.###})";
    }

    /// <summary>单步理论记录。</summary>
    public class StepTheory
    {
        public string StepId { get; set; } = "";
        public string Kind { get; set; } = "";
        /// <summary>本步体积增量 mm³（切除为负）。</summary>
        public double DeltaVolumeMm3 { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>理论预算报告（供计划卡片预览与 T18 真实回读对比）。</summary>
    public class TheoryReport
    {
        /// <summary>理论总体积 mm³（所有 DeltaVolume 之和）。</summary>
        public double VolumeMm3 { get; set; }
        public BoxTheory BoundingBox { get; set; } = new BoxTheory();
        public int HoleCount { get; set; }
        /// <summary>去重孔径表 mm。</summary>
        public List<double> HoleDiametersMm { get; set; } = new List<double>();
        /// <summary>无法精确预算的特征说明（如「圆角 R5 体积为估算」）。</summary>
        public List<string> Estimates { get; set; } = new List<string>();
        public List<StepTheory> Steps { get; set; } = new List<StepTheory>();
    }

    /// <summary>
    /// M4-T17 理论值预算器：由特征树做初等几何预算（理论体积/包围盒/孔表）。
    /// 纯几何算法，无 COM 依赖；任一步无法解析时记 Estimates 并跳过，不抛异常。
    ///
    /// 平面映射约定：
    ///   front → 草图(x,y) = 全局(x,y)，拉伸沿 +z；
    ///   top   → 草图(x,y) = 全局(x,z)，拉伸沿 +y；
    ///   right → 草图(x,y) = 全局(z,y)，拉伸沿 +x。
    /// </summary>
    public static class TheoryBudget
    {
        /// <summary>草图解析结果（内部）。</summary>
        private sealed class SketchInfo
        {
            public string Plane = "top";
            public bool HasProfile;                 // 是否解析出闭合轮廓
            public double Area;                     // 主轮廓面积 mm²
            public double MinX, MinY, MaxX, MaxY;   // 主轮廓 2D 包围盒
            public double CenX, CenY;               // 主轮廓质心
            public bool HasAxis;                    // 是否含 centerline 旋转轴
            public double Ax1, Ay1, Ax2, Ay2;       // 旋转轴端点
            public bool HasArc;                     // 含 arc 实体（预算器不解析弧轮廓，标注估算）
        }

        /// <summary>3D 包围盒累积器。</summary>
        private sealed class BoxAccum
        {
            public bool Has;
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

            public void Add(double x, double y, double z)
            {
                if (!Has)
                {
                    Has = true;
                    MinX = MaxX = x; MinY = MaxY = y; MinZ = MaxZ = z;
                    return;
                }
                MinX = Math.Min(MinX, x); MaxX = Math.Max(MaxX, x);
                MinY = Math.Min(MinY, y); MaxY = Math.Max(MaxY, y);
                MinZ = Math.Min(MinZ, z); MaxZ = Math.Max(MaxZ, z);
            }

            /// <summary>轴序号：0=X 1=Y 2=Z。</summary>
            public double Extent(int axis) => !Has ? 0 : axis == 0 ? MaxX - MinX : axis == 1 ? MaxY - MinY : MaxZ - MinZ;
        }

        /// <summary>主入口：对特征树逐步预算，返回理论报告。</summary>
        public static TheoryReport Evaluate(FeatureTree tree)
        {
            var rep = new TheoryReport();
            if (tree?.Steps == null) return rep;

            var sketches = new Dictionary<string, SketchInfo>(StringComparer.Ordinal);
            var deltas = new Dictionary<string, double>(StringComparer.Ordinal); // stepId → 本步体积增量（供阵列/镜像引用）
            var box = new BoxAccum();
            string lastPlane = "top"; // 最近草图基准面（孔轴/通切深度参照）

            foreach (var step in tree.Steps)
            {
                if (step == null) continue;
                var rec = new StepTheory { StepId = step.Id ?? "", Kind = step.Kind ?? "" };
                string kind = (step.Kind ?? "").ToLowerInvariant();
                try
                {
                    switch (kind)
                    {
                        case "sketch":
                        {
                            var info = ParseSketch(step.Sketch);
                            if (!string.IsNullOrEmpty(step.Id)) sketches[step.Id] = info;
                            lastPlane = info.Plane;
                            rec.Note = info.HasProfile
                                ? $"轮廓面积 {info.Area:0.##} mm²"
                                : "未解析出闭合轮廓";
                            if (!info.HasProfile)
                                rep.Estimates.Add($"{step.Id} 草图无闭合轮廓，后续引用它的特征将无法精确预算");
                            if (info.HasArc)
                                rep.Estimates.Add($"{step.Id} 草图含圆弧（arc）实体，弧边轮廓面积未纳入理论预算，体积/包围盒判定精度有限");
                            break;
                        }

                        case "extrudeboss":
                        case "extrudecut":
                        {
                            bool isCut = kind == "extrudecut";
                            var sk = Lookup(sketches, step.SketchId);
                            if (sk == null || !sk.HasProfile)
                            {
                                rec.Note = "草图缺失或无闭合轮廓，跳过";
                                rep.Estimates.Add($"{step.Id} {step.Kind} 无可计算轮廓");
                                break;
                            }
                            double depth;
                            if (isCut && step.Extrude != null && step.Extrude.ThroughAll)
                            {
                                // 通切深度 = 已累积包围盒在拉伸方向的尺寸
                                depth = box.Extent(AxisOf(sk.Plane));
                                if (depth <= 0)
                                {
                                    rec.Note = "无已累积包围盒，无法预算通切深度";
                                    rep.Estimates.Add($"{step.Id} 通切无参照厚度");
                                    break;
                                }
                            }
                            else
                            {
                                depth = step.Extrude?.DepthMm ?? 0;
                                if (!isCut && step.Extrude != null && step.Extrude.ThroughAll)
                                    rep.Estimates.Add($"{step.Id} 凸台 ThroughAll 不常见，按 DepthMm 处理");
                            }
                            if (depth <= 0)
                            {
                                rec.Note = "拉伸深度缺失，跳过";
                                rep.Estimates.Add($"{step.Id} {step.Kind} 深度缺失");
                                break;
                            }
                            double vol = sk.Area * depth;
                            rec.DeltaVolumeMm3 = isCut ? -vol : vol;
                            rec.Note = $"轮廓 {sk.Area:0.##} mm² × 深 {depth:0.##} mm";
                            if (!isCut)
                            {
                                // 凸台累积包围盒（切除不扩大包围盒）
                                bool flip = step.Extrude != null && step.Extrude.Flip;
                                double t0 = flip ? -depth : 0, t1 = flip ? 0 : depth;
                                UnionMapped(box, sk.Plane, sk.MinX, sk.MinY, t0);
                                UnionMapped(box, sk.Plane, sk.MaxX, sk.MaxY, t0);
                                UnionMapped(box, sk.Plane, sk.MinX, sk.MinY, t1);
                                UnionMapped(box, sk.Plane, sk.MaxX, sk.MaxY, t1);
                            }
                            break;
                        }

                        case "revolveboss":
                        case "revolvecut":
                        {
                            bool isCut = kind == "revolvecut";
                            var sk = Lookup(sketches, step.SketchId);
                            if (sk == null || !sk.HasProfile)
                            {
                                rec.Note = "草图缺失或无闭合轮廓，跳过";
                                rep.Estimates.Add($"{step.Id} {step.Kind} 无可计算轮廓");
                                break;
                            }
                            // Pappus 定理：V = 轮廓面积 × 2π × 质心到轴距离；AngleDeg<360 按比例
                            double d;
                            if (sk.HasAxis)
                            {
                                double dx = sk.Ax2 - sk.Ax1, dy = sk.Ay2 - sk.Ay1;
                                double len = Math.Sqrt(dx * dx + dy * dy);
                                if (len < 1e-9)
                                {
                                    d = Math.Abs(sk.CenY);
                                    rep.Estimates.Add($"{step.Id} 旋转轴退化，按 X 轴估算");
                                }
                                else
                                {
                                    d = Math.Abs(dx * (sk.Ay1 - sk.CenY) - (sk.Ax1 - sk.CenX) * dy) / len;
                                }
                            }
                            else
                            {
                                d = Math.Abs(sk.CenY); // 缺 centerline 时按草图 X 轴估算
                                rep.Estimates.Add($"{step.Id} 缺 centerline，旋转轴按草图 X 轴估算");
                            }
                            double frac = Clamp01((step.Revolve?.AngleDeg ?? 360.0) / 360.0);
                            double vol = sk.Area * 2.0 * Math.PI * d * frac;
                            rec.DeltaVolumeMm3 = isCut ? -vol : vol;
                            rec.Note = $"Pappus 面积 {sk.Area:0.##} × 2π×{d:0.##} × {frac:0.###}";
                            if (!isCut) UnionRevolveBox(box, sk);
                            break;
                        }

                        case "holewizard":
                        {
                            var h = step.Hole;
                            if (h == null || !h.DiameterMm.HasValue || h.DiameterMm.Value <= 0)
                            {
                                rec.Note = "孔参数缺失，跳过";
                                rep.Estimates.Add($"{step.Id} 孔参数缺失");
                                break;
                            }
                            int n = h.Positions?.Count ?? 0;
                            if (n == 0) rep.Estimates.Add($"{step.Id} 孔位缺失，孔数按 0 计");
                            double r = h.DiameterMm.Value / 2.0;
                            double depth;
                            if (h.ThroughAll)
                            {
                                // 通孔深度 = 已累积包围盒沿最近草图基准面法向的尺寸（板厚）
                                depth = box.Extent(AxisOf(lastPlane));
                                if (depth <= 0)
                                {
                                    rec.Note = "无板厚可参照，跳过";
                                    rep.Estimates.Add($"{step.Id} 通孔无参照板厚");
                                    break;
                                }
                            }
                            else
                            {
                                depth = h.DepthMm ?? 0;
                                if (depth <= 0)
                                {
                                    rec.Note = "孔深缺失，跳过";
                                    rep.Estimates.Add($"{step.Id} 盲孔深度缺失");
                                    break;
                                }
                            }
                            double perHole = Math.PI * r * r * depth;
                            string note = $"φ{h.DiameterMm.Value:0.##}×{depth:0.##}";
                            if (h.CboreDiameterMm.HasValue && h.CboreDepthMm.HasValue
                                && h.CboreDiameterMm.Value > 0 && h.CboreDepthMm.Value > 0)
                            {
                                // 沉孔段只累加「扩径环」体积 π(R²−r²)×depth：
                                // 通孔段已扣全深 r 柱体，沉孔顶部与通孔重叠的 r 柱部分不得重复扣
                                // （否则比 SW 真实去除量多扣 π·r²·沉孔深，T18 校验会系统性偏小）。
                                double rb = h.CboreDiameterMm.Value / 2.0;
                                perHole += Math.PI * (rb * rb - r * r) * h.CboreDepthMm.Value;
                                note += $" 沉孔φ{h.CboreDiameterMm.Value:0.##}×{h.CboreDepthMm.Value:0.##}";
                            }
                            rec.DeltaVolumeMm3 = -perHole * n;
                            rec.Note = note + $" ×{n} 孔";
                            rep.HoleCount += n;
                            if (!rep.HoleDiametersMm.Contains(h.DiameterMm.Value))
                                rep.HoleDiametersMm.Add(h.DiameterMm.Value);
                            break;
                        }

                        case "linearpattern":
                        case "circularpattern":
                        {
                            var srcId = step.Pattern?.SourceStepId;
                            int count = step.Pattern?.Count ?? 0;
                            if (srcId == null || !deltas.TryGetValue(srcId, out double seed) || count < 1)
                            {
                                rec.Note = "种子特征或实例数缺失，跳过";
                                rep.Estimates.Add($"{step.Id} 阵列无法预算（种子/实例数缺失）");
                                break;
                            }
                            // Count 含种子，本步增量 = 种子 × (Count−1)
                            rec.DeltaVolumeMm3 = seed * (count - 1);
                            rec.Note = $"种子 {srcId} ×{count}（含种子）";
                            break;
                        }

                        case "mirror":
                        {
                            var srcId = step.Mirror?.SourceStepId;
                            if (srcId == null || !deltas.TryGetValue(srcId, out double seed))
                            {
                                rec.Note = "种子特征缺失，跳过";
                                rep.Estimates.Add($"{step.Id} 镜像无法预算（种子缺失）");
                                break;
                            }
                            rec.DeltaVolumeMm3 = seed; // 总量 ×2，增量 = 种子 ×1
                            rec.Note = $"镜像 {srcId}（总量×2）";
                            break;
                        }

                        case "fillet":
                        {
                            double rf = step.Fillet?.RadiusMm ?? 0;
                            if (rf <= 0 || !box.Has)
                            {
                                rec.Note = "圆角半径缺失或无参照实体";
                                rep.Estimates.Add($"{step.Id} 圆角无法估算");
                                break;
                            }
                            // 粗略：ΔV ≈ −r²(1−π/4)×棱长；棱长按盒体 12 条棱近似 4(X+Y+Z)
                            double edgeLen = 4.0 * (box.Extent(0) + box.Extent(1) + box.Extent(2));
                            rec.DeltaVolumeMm3 = -rf * rf * (1.0 - Math.PI / 4.0) * edgeLen;
                            rec.Note = $"圆角 R{rf:0.##} 估算";
                            rep.Estimates.Add($"{step.Id} 圆角 R{rf:0.##} 体积为估算");
                            break;
                        }

                        case "chamfer":
                        {
                            double dc = step.Chamfer?.DistanceMm ?? 0;
                            if (dc <= 0 || !box.Has)
                            {
                                rec.Note = "倒角距离缺失或无参照实体";
                                rep.Estimates.Add($"{step.Id} 倒角无法估算");
                                break;
                            }
                            // 粗略：ΔV ≈ −d²/2×棱长
                            double edgeLen = 4.0 * (box.Extent(0) + box.Extent(1) + box.Extent(2));
                            rec.DeltaVolumeMm3 = -dc * dc / 2.0 * edgeLen;
                            rec.Note = $"倒角 {dc:0.##} 估算";
                            rep.Estimates.Add($"{step.Id} 倒角 {dc:0.##} 体积为估算");
                            break;
                        }

                        case "shell":
                        {
                            double t = step.Shell?.ThicknessMm ?? 0;
                            if (t <= 0 || !box.Has)
                            {
                                rec.Note = "壳厚缺失或无参照实体";
                                rep.Estimates.Add($"{step.Id} 抽壳无法估算");
                                break;
                            }
                            // 粗略：ΔV ≈ −表面积×厚度 = −2(XY+YZ+XZ)×t
                            double x = box.Extent(0), y = box.Extent(1), z = box.Extent(2);
                            rec.DeltaVolumeMm3 = -2.0 * (x * y + y * z + x * z) * t;
                            rec.Note = $"抽壳 t={t:0.##} 估算";
                            rep.Estimates.Add($"{step.Id} 抽壳 t={t:0.##} 体积为估算");
                            break;
                        }

                        case "rib":
                            rec.Note = "筋板缺轮廓，体积未预算";
                            rep.Estimates.Add($"{step.Id} 筋板体积为估算（0 计）");
                            break;

                        case "draft":
                            rec.Note = "拔模对体积影响小，未预算";
                            rep.Estimates.Add($"{step.Id} 拔模体积影响为估算（0 计）");
                            break;

                        case "setmaterial":
                            rec.Note = "材料设置不影响体积";
                            break;

                        default:
                            rec.Note = "未知类型，跳过";
                            rep.Estimates.Add($"{step.Id} 未知步骤类型 {step.Kind}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // 容错：任一步解析失败记 Estimates 并跳过，不抛异常
                    rec.DeltaVolumeMm3 = 0;
                    rec.Note = "解析失败：" + ex.Message;
                    rep.Estimates.Add($"{step.Id} {step.Kind} 解析失败，已跳过");
                }
                rep.Steps.Add(rec);
                rep.VolumeMm3 += rec.DeltaVolumeMm3;
                if (!string.IsNullOrEmpty(step.Id)) deltas[step.Id] = rec.DeltaVolumeMm3;
            }

            if (box.Has)
            {
                rep.BoundingBox = new BoxTheory
                {
                    X = box.Extent(0),
                    Y = box.Extent(1),
                    Z = box.Extent(2)
                };
            }
            return rep;
        }

        // ---------- 草图解析 ----------

        /// <summary>解析草图：主轮廓取面积最大的闭合实体。</summary>
        private static SketchInfo ParseSketch(SketchSpec sketch)
        {
            var info = new SketchInfo { Plane = (sketch?.Plane ?? "top").ToLowerInvariant() };
            if (sketch?.Entities == null) return info;
            double best = 0;
            foreach (var e in sketch.Entities)
            {
                if (e == null) continue;
                string t = (e.Type ?? "").ToLowerInvariant();
                if (t == "centerline")
                {
                    if (e.X1.HasValue && e.Y1.HasValue && e.X2.HasValue && e.Y2.HasValue)
                    {
                        info.HasAxis = true;
                        info.Ax1 = e.X1.Value; info.Ay1 = e.Y1.Value;
                        info.Ax2 = e.X2.Value; info.Ay2 = e.Y2.Value;
                    }
                    continue;
                }
                if (t == "arc") info.HasArc = true;
                if (TryProfile(e, t, out double area, out double mnx, out double mny,
                               out double mxx, out double mxy, out double cx, out double cy))
                {
                    // 取舍说明：多闭合轮廓取面积最大者为主轮廓（孤岛/嵌套轮廓暂不相加，避免重复计面积）
                    if (area > best)
                    {
                        best = area;
                        info.HasProfile = true;
                        info.Area = area;
                        info.MinX = mnx; info.MinY = mny; info.MaxX = mxx; info.MaxY = mxy;
                        info.CenX = cx; info.CenY = cy;
                    }
                }
            }
            return info;
        }

        /// <summary>计算单个闭合实体的面积/2D 包围盒/质心；非轮廓或参数缺失返回 false。</summary>
        private static bool TryProfile(SketchEntity e, string t,
            out double area, out double mnx, out double mny, out double mxx, out double mxy,
            out double cx, out double cy)
        {
            area = 0; mnx = mny = mxx = mxy = cx = cy = 0;
            switch (t)
            {
                case "rectcenter":
                {
                    if (!e.Cx.HasValue || !e.Cy.HasValue || !e.Width.HasValue || !e.Height.HasValue) return false;
                    double w = e.Width.Value, h = e.Height.Value;
                    if (w <= 0 || h <= 0) return false;
                    cx = e.Cx.Value; cy = e.Cy.Value;
                    mnx = cx - w / 2; mxx = cx + w / 2; mny = cy - h / 2; mxy = cy + h / 2;
                    area = w * h;
                    return true;
                }
                case "rectcorner":
                {
                    // 角点矩形：优先 X1/Y1/X2/Y2，兼容 X1/Y1 + Width/Height
                    double? x1 = e.X1, y1 = e.Y1, x2 = e.X2, y2 = e.Y2;
                    if (!x2.HasValue && x1.HasValue && e.Width.HasValue) x2 = x1 + e.Width;
                    if (!y2.HasValue && y1.HasValue && e.Height.HasValue) y2 = y1 + e.Height;
                    if (!x1.HasValue || !y1.HasValue || !x2.HasValue || !y2.HasValue) return false;
                    mnx = Math.Min(x1.Value, x2.Value); mxx = Math.Max(x1.Value, x2.Value);
                    mny = Math.Min(y1.Value, y2.Value); mxy = Math.Max(y1.Value, y2.Value);
                    double w = mxx - mnx, h = mxy - mny;
                    if (w <= 0 || h <= 0) return false;
                    cx = (mnx + mxx) / 2; cy = (mny + mxy) / 2;
                    area = w * h;
                    return true;
                }
                case "circle":
                {
                    double r = e.Radius ?? (e.Diameter ?? 0) / 2.0;
                    if (r <= 0 || !e.Cx.HasValue || !e.Cy.HasValue) return false;
                    cx = e.Cx.Value; cy = e.Cy.Value;
                    mnx = cx - r; mxx = cx + r; mny = cy - r; mxy = cy + r;
                    area = Math.PI * r * r;
                    return true;
                }
                case "slot":
                {
                    // 直槽口（总长 Length、宽 Width）= 矩形段 + 两端半圆；Angle 旋转不影响面积
                    if (!e.Length.HasValue || !e.Width.HasValue) return false;
                    double L = e.Length.Value, w = e.Width.Value;
                    if (L <= 0 || w <= 0) return false;
                    double straight = Math.Max(L - w, 0);
                    area = straight * w + Math.PI * (w / 2) * (w / 2);
                    cx = e.Cx ?? 0; cy = e.Cy ?? 0;
                    mnx = cx - L / 2; mxx = cx + L / 2; mny = cy - w / 2; mxy = cy + w / 2;
                    return true;
                }
                case "polygon":
                {
                    // 正多边形（外接圆直径 CircumDiameter）：A = (n/2)R²sin(2π/n)
                    int n = e.Sides ?? 0;
                    double R = (e.CircumDiameter ?? 0) / 2.0;
                    if (n < 3 || R <= 0) return false;
                    area = n / 2.0 * R * R * Math.Sin(2.0 * Math.PI / n);
                    cx = e.Cx ?? 0; cy = e.Cy ?? 0;
                    mnx = cx - R; mxx = cx + R; mny = cy - R; mxy = cy + R;
                    return true;
                }
                case "polyline":
                {
                    if (e.Closed != true || e.Points == null || e.Points.Count < 3) return false;
                    var pts = e.Points;
                    int n = pts.Count;
                    double a = 0, cx6 = 0, cy6 = 0;
                    mnx = mxx = pts[0].X; mny = mxy = pts[0].Y;
                    for (int i = 0; i < n; i++)
                    {
                        var p = pts[i]; var q = pts[(i + 1) % n];
                        double cross = p.X * q.Y - q.X * p.Y;
                        a += cross; cx6 += (p.X + q.X) * cross; cy6 += (p.Y + q.Y) * cross;
                        mnx = Math.Min(mnx, p.X); mxx = Math.Max(mxx, p.X);
                        mny = Math.Min(mny, p.Y); mxy = Math.Max(mxy, p.Y);
                    }
                    a /= 2.0; // shoelace 有向面积
                    area = Math.Abs(a);
                    if (area < 1e-9) return false;
                    cx = cx6 / (6.0 * a); // 多边形质心
                    cy = cy6 / (6.0 * a);
                    return true;
                }
            }
            return false;
        }

        // ---------- 平面映射与包围盒 ----------

        private static SketchInfo Lookup(Dictionary<string, SketchInfo> sketches, string sketchId)
            => sketchId != null && sketches.TryGetValue(sketchId, out var s) ? s : null;

        /// <summary>拉伸轴序号：front→2(Z)，top→1(Y)，right→0(X)。</summary>
        private static int AxisOf(string plane)
        {
            switch ((plane ?? "top").ToLowerInvariant())
            {
                case "front": return 2;
                case "right": return 0;
                default: return 1; // top
            }
        }

        /// <summary>草图 2D 点 + 拉伸向厚度 t → 全局 3D 坐标（映射约定见类注释）。</summary>
        private static void MapTo3D(string plane, double sx, double sy, double t,
            out double gx, out double gy, out double gz)
        {
            switch ((plane ?? "top").ToLowerInvariant())
            {
                case "front": gx = sx; gy = sy; gz = t; break;
                case "right": gx = t; gy = sy; gz = sx; break;
                default: gx = sx; gy = t; gz = sy; break; // top
            }
        }

        private static void UnionMapped(BoxAccum box, string plane, double sx, double sy, double t)
        {
            MapTo3D(plane, sx, sy, t, out double gx, out double gy, out double gz);
            box.Add(gx, gy, gz);
        }

        /// <summary>
        /// 旋转体包围盒（整周转）：轴水平(y=ya)时轮廓 x 区间不变，径向 r=max|y−ya| 扩展到面内法向与拉伸轴；
        /// 轴竖直(x=xa)同理。轴为斜线时退化为轮廓包围盒并视为近似。
        /// </summary>
        private static void UnionRevolveBox(BoxAccum box, SketchInfo sk)
        {
            const double eps = 1e-9;
            if (sk.HasAxis && Math.Abs(sk.Ay2 - sk.Ay1) < eps) // 水平轴 y=ya
            {
                double ya = sk.Ay1;
                double r = Math.Max(Math.Abs(sk.MinY - ya), Math.Abs(sk.MaxY - ya));
                UnionMapped(box, sk.Plane, sk.MinX, ya - r, -r);
                UnionMapped(box, sk.Plane, sk.MaxX, ya + r, +r);
            }
            else if (sk.HasAxis && Math.Abs(sk.Ax2 - sk.Ax1) < eps) // 竖直轴 x=xa
            {
                double xa = sk.Ax1;
                double r = Math.Max(Math.Abs(sk.MinX - xa), Math.Abs(sk.MaxX - xa));
                UnionMapped(box, sk.Plane, xa - r, sk.MinY, -r);
                UnionMapped(box, sk.Plane, xa + r, sk.MaxY, +r);
            }
            else // 无轴或斜轴：保守取轮廓 2D 包围盒（零厚度），仅供粗略参考
            {
                UnionMapped(box, sk.Plane, sk.MinX, sk.MinY, 0);
                UnionMapped(box, sk.Plane, sk.MaxX, sk.MaxY, 0);
            }
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
