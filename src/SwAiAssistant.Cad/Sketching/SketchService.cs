using System;
using System.Collections.Generic;
using SwAiAssistant.Cad.Session;
using SwAiAssistant.Core.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAiAssistant.Cad.Sketching
{
    /// <summary>三个默认基准面（基准面坐标系映射：前视 X-Y / 上视 X-Z / 右视 Y-Z）。</summary>
    public enum PlaneKind
    {
        /// <summary>前视基准面（草图 X=全局 X，草图 Y=全局 Y）。</summary>
        Front,
        /// <summary>上视基准面（草图 X=全局 X，草图 Y=全局 Z）。</summary>
        Top,
        /// <summary>右视基准面（草图 X=全局 Z，草图 Y=全局 Y）。</summary>
        Right
    }

    /// <summary>
    /// 草图原语：基准面选择、进入/退出草图、矩形（角点）、圆（圆心半径）、
    /// 槽口（直槽两圆心点法）、多边形（外接圆）、直线/圆弧/中心线自由轮廓、
    /// 几何约束（重合/同心/水平/竖直等）、智能尺寸（直径/半径/水平/竖直/线性）。
    /// COM 签名经 SW2026 redist 反射实测：InsertSketch(bool)、
    /// CreateCornerRectangle(6 double)、CreateCircleByRadius(4 double)、
    /// CreateSketchSlot(line=0,CenterCenter=0,Width 米,两圆心点+宽度侧点,AddDimension=false)、
    /// CreatePolygon(cx,cy,0,cx+r,cy,0,sides,Inscribed=true)、CreateLine/CreateCenterLine/CreateArc、
    /// IModelDoc2.SketchAddConstraints("sgXXX") 作用于当前选择集、
    /// IAddDiameterDimension2/IAddRadialDimension2/IAddHorizontalDimension2/IAddVerticalDimension2/IAddDimension2。
    /// 单位约定：公开方法一律毫米输入，内部换算为米调用 COM。
    /// </summary>
    public sealed class SketchService
    {
        /// <summary>基准面候选名：中文模板（gb_part.prtdot）/英文模板/简写。</summary>
        private static readonly string[][] PlaneNames =
        {
            new[] { "前视基准面", "Front Plane", "Front" },
            new[] { "上视基准面", "Top Plane", "Top" },
            new[] { "右视基准面", "Right Plane", "Right" }
        };

        private readonly SwSession _session;

        public SketchService(SwSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>基准面候选名（供镜像面等按名选择 PLANE 的调用方使用）。</summary>
        public static string[] PlaneCandidates(PlaneKind plane) => PlaneNames[(int)plane];

        /// <summary>按名称选择默认基准面（任一候选名命中即成功）。</summary>
        public void SelectPlane(IModelDoc2 doc, PlaneKind plane)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _session.OnSta<object>(() =>
            {
                foreach (string name in PlaneNames[(int)plane])
                {
                    bool ok = doc.Extension.SelectByID2(name, "PLANE", 0, 0, 0, false, 0, null, 0);
                    if (ok)
                    {
                        Log.Info("Cad", $"已选择基准面：{name}");
                        return null;
                    }
                }
                throw new CadException($"选择基准面失败：{plane}（尝试过：{string.Join(" / ", PlaneNames[(int)plane])}）。");
            });
        }

        /// <summary>进入草图编辑；addToDb=true 时实体直写数据库（不生成自动约束，速度快，适合程序化批量建模）。</summary>
        public void BeginSketch(IModelDoc2 doc, bool addToDb)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _session.OnSta<object>(() =>
            {
                try
                {
                    doc.SketchManager.AddToDB = addToDb;
                    doc.SketchManager.InsertSketch(true);
                }
                catch (Exception ex) { throw CadException.FromCom("进入草图", ex); }
                return null;
            });
        }

        /// <summary>退出草图编辑并恢复 AddToDB=false。</summary>
        public void EndSketch(IModelDoc2 doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _session.OnSta<object>(() =>
            {
                try
                {
                    doc.SketchManager.AddToDB = false;
                    doc.SketchManager.InsertSketch(true);
                    doc.ClearSelection2(true);
                }
                catch (Exception ex) { throw CadException.FromCom("退出草图", ex); }
                return null;
            });
        }

        /// <summary>角点矩形（草图坐标，毫米）。返回 4 条边段。</summary>
        public ISketchSegment[] CreateCornerRectangleMm(IModelDoc2 doc, double x1, double y1, double x2, double y2)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    var p1 = (Units.MmToM(x1), Units.MmToM(y1), 0.0);
                    var p2 = (Units.MmToM(x2), Units.MmToM(y2), 0.0);
                    object result = doc.SketchManager.CreateCornerRectangle(
                        p1.Item1, p1.Item2, p1.Item3,
                        p2.Item1, p2.Item2, p2.Item3);
                    if (result is object[] segs)
                    {
                        var arr = new ISketchSegment[segs.Length];
                        for (int i = 0; i < segs.Length; i++) arr[i] = segs[i] as ISketchSegment;
                        return arr;
                    }
                    throw new CadException("创建矩形失败：SolidWorks 返回空（确认已在草图编辑状态）。");
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("创建矩形", ex); }
            });
        }

        /// <summary>圆心半径圆（草图坐标，毫米）。</summary>
        public ISketchSegment CreateCircleMm(IModelDoc2 doc, double centerX, double centerY, double radiusMm)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    var seg = doc.SketchManager.CreateCircleByRadius(
                        Units.MmToM(centerX), Units.MmToM(centerY), 0, Units.MmToM(radiusMm));
                    if (seg == null)
                    {
                        throw new CadException("创建圆失败：SolidWorks 返回空（确认已在草图编辑状态）。");
                    }
                    return seg;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("创建圆", ex); }
            });
        }

        /// <summary>
        /// 对草图段加直径智能尺寸（尺寸驱动路径，供 T14 对话式修改尺寸使用）。
        /// 返回 IDimension：Value 属性（米）可读写。
        /// </summary>
        public IDimension AddDiameterDimensionMm(IModelDoc2 doc, ISketchSegment segment, double textX, double textY)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (segment == null) throw new ArgumentNullException(nameof(segment));
            return _session.OnSta(() =>
            {
                try
                {
                    // 关键坑位：swInputDimValOnCreate 默认开——创建尺寸时弹出「修改」输入框
                    // 等待人工输入，程序化/后台调用会永久阻塞，必须关闭。
                    _session.App.SetUserPreferenceToggle(
                        (int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
                    doc.ClearSelection2(true);
                    if (!segment.Select4(false, null))
                    {
                        throw new CadException("添加尺寸失败：草图段选择未命中。");
                    }
                    var disp = doc.IAddDiameterDimension2(Units.MmToM(textX), Units.MmToM(textY), 0);
                    if (disp == null)
                    {
                        throw new CadException("添加尺寸失败：SolidWorks 未创建标注。");
                    }
                    IDimension dim = disp.GetDimension2(0);
                    if (dim == null)
                    {
                        throw new CadException("添加尺寸失败：无法取得尺寸对象。");
                    }
                    return dim;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("添加直径尺寸", ex); }
            });
        }

        // ==================== M3 草图实体补全 ====================

        /// <summary>
        /// 直槽口（总长 lengthMm、宽 widthMm、中心 (cxMm,cyMm)、旋转角 angleDeg，草图坐标毫米）。
        /// 实测约定：CreateSketchSlot(line=0, CenterCenter=0, Width 米, 两圆心点, 宽度侧点, 0, AddDimension=false)；
        /// 圆心距 = 总长 − 槽宽。返回 SketchSlot（亦属 ISketchSegment）。
        /// </summary>
        public object CreateSlotMm(IModelDoc2 doc, double cxMm, double cyMm,
            double lengthMm, double widthMm, double angleDeg)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (lengthMm <= 0 || widthMm <= 0) throw new CadException("槽口长/宽必须为正数。");
            if (lengthMm < widthMm) throw new CadException("槽口长度须 ≥ 宽度。");
            return _session.OnSta(() =>
            {
                try
                {
                    double theta = angleDeg * Math.PI / 180.0;
                    double halfCenter = (lengthMm - widthMm) / 2.0;
                    double dx = halfCenter * Math.Cos(theta), dy = halfCenter * Math.Sin(theta);
                    double x1 = cxMm - dx, y1 = cyMm - dy;   // 圆心 1
                    double x2 = cxMm + dx, y2 = cyMm + dy;   // 圆心 2
                    // 宽度侧点：垂直于槽轴方向偏移任意距离（槽宽由 Width 参数决定）
                    double px = x1 - Math.Sin(theta) * widthMm, py = y1 + Math.Cos(theta) * widthMm;
                    object slot = doc.SketchManager.CreateSketchSlot(
                        0, 0, Units.MmToM(widthMm),
                        Units.MmToM(x1), Units.MmToM(y1), 0,
                        Units.MmToM(x2), Units.MmToM(y2), 0,
                        Units.MmToM(px), Units.MmToM(py), 0,
                        0, false);
                    if (slot == null)
                    {
                        throw new CadException("创建槽口失败：SolidWorks 返回空（确认已在草图编辑状态）。");
                    }
                    return slot;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("创建槽口", ex); }
            });
        }

        /// <summary>外接圆正多边形（中心、外接圆半径、边数、起始顶点旋转角；毫米）。</summary>
        public ISketchSegment[] CreatePolygonMm(IModelDoc2 doc, double cxMm, double cyMm,
            double circumRadiusMm, int sides, double angleDeg)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (circumRadiusMm <= 0) throw new CadException("多边形外接圆半径必须为正数。");
            if (sides < 3 || sides > 64) throw new CadException("多边形边数须在 3~64。");
            return _session.OnSta(() =>
            {
                try
                {
                    double theta = angleDeg * Math.PI / 180.0;
                    double px = cxMm + circumRadiusMm * Math.Cos(theta);
                    double py = cyMm + circumRadiusMm * Math.Sin(theta);
                    object result = doc.SketchManager.CreatePolygon(
                        Units.MmToM(cxMm), Units.MmToM(cyMm), 0,
                        Units.MmToM(px), Units.MmToM(py), 0,
                        sides, true); // Inscribed=true：外接圆多边形
                    if (result is object[] segs)
                    {
                        var arr = new ISketchSegment[segs.Length];
                        for (int i = 0; i < segs.Length; i++) arr[i] = segs[i] as ISketchSegment;
                        return arr;
                    }
                    throw new CadException("创建多边形失败：SolidWorks 返回空（确认已在草图编辑状态）。");
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("创建多边形", ex); }
            });
        }

        /// <summary>直线段（草图坐标毫米）。</summary>
        public ISketchSegment CreateLineMm(IModelDoc2 doc, double x1, double y1, double x2, double y2)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    var seg = doc.SketchManager.CreateLine(
                        Units.MmToM(x1), Units.MmToM(y1), 0, Units.MmToM(x2), Units.MmToM(y2), 0);
                    if (seg == null)
                    {
                        throw new CadException("创建直线失败：SolidWorks 返回空。");
                    }
                    return seg;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("创建直线", ex); }
            });
        }

        /// <summary>中心线（构造线，旋转凸台轴线用；草图坐标毫米）。</summary>
        public ISketchSegment CreateCenterLineMm(IModelDoc2 doc, double x1, double y1, double x2, double y2)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    var seg = doc.SketchManager.CreateCenterLine(
                        Units.MmToM(x1), Units.MmToM(y1), 0, Units.MmToM(x2), Units.MmToM(y2), 0);
                    if (seg == null)
                    {
                        throw new CadException("创建中心线失败：SolidWorks 返回空。");
                    }
                    return seg;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("创建中心线", ex); }
            });
        }

        /// <summary>圆弧（圆心+起点+终点；ccw=true 逆时针；草图坐标毫米）。</summary>
        public ISketchSegment CreateArcMm(IModelDoc2 doc, double cx, double cy,
            double x1, double y1, double x2, double y2, bool ccw)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    var seg = doc.SketchManager.CreateArc(
                        Units.MmToM(cx), Units.MmToM(cy), 0,
                        Units.MmToM(x1), Units.MmToM(y1), 0,
                        Units.MmToM(x2), Units.MmToM(y2), 0,
                        (short)(ccw ? 1 : -1));
                    if (seg == null)
                    {
                        throw new CadException("创建圆弧失败：SolidWorks 返回空。");
                    }
                    return seg;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("创建圆弧", ex); }
            });
        }

        /// <summary>
        /// 自由轮廓（点序列，毫米）：相邻点直线连接，closed 时首尾闭合。
        /// 注意 AddToDB=true 时端点自动吸附重合，无需显式约束。
        /// </summary>
        public ISketchSegment[] CreatePolylineMm(IModelDoc2 doc,
            IList<Point2Mm> points, bool closed)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (points == null || points.Count < 2) throw new CadException("自由轮廓至少 2 个点。");
            if (closed && points.Count < 3) throw new CadException("闭合轮廓至少 3 个点。");
            int count = closed ? points.Count : points.Count - 1;
            var segs = new List<ISketchSegment>(count);
            for (int i = 0; i < count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                segs.Add(CreateLineMm(doc, a.X, a.Y, b.X, b.Y));
            }
            return segs.ToArray();
        }

        // ==================== 几何约束 ====================

        /// <summary>
        /// 对指定草图段施加约束。constraint 为 sgXXX 字符串：
        /// sgCOINCIDENT/sgCONCENTRIC/sgCOLINEAR/sgHORIZONTAL/sgVERTICAL/sgSAMELENGTH/sgTANGENT 等。
        /// 实测：SketchAddConstraints 作用于当前选择集，须先 Select4 逐段选中（append）。
        /// </summary>
        public void AddConstraint(IModelDoc2 doc, string constraint, params ISketchSegment[] segments)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(constraint)) throw new CadException("约束类型不能为空。");
            if (segments == null || segments.Length == 0) throw new CadException("约束须至少选中 1 个草图段。");
            _session.OnSta<object>(() =>
            {
                try
                {
                    doc.ClearSelection2(true);
                    int selected = 0;
                    foreach (var seg in segments)
                    {
                        if (seg != null && seg.Select4(selected > 0, null)) selected++;
                    }
                    if (selected == 0)
                    {
                        throw new CadException("添加约束失败：草图段选择未命中。");
                    }
                    doc.SketchAddConstraints(constraint);
                    Log.Info("Cad", $"已施加约束 {constraint}（{selected} 段）");
                    return null;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("添加约束 " + constraint, ex); }
            });
        }

        // ==================== 尺寸标注补全 ====================

        /// <summary>尺寸标注定位点（毫米，草图坐标）。</summary>
        public struct DimAnchor
        {
            public double X; public double Y;
            public DimAnchor(double x, double y) { X = x; Y = y; }
        }

        /// <summary>半径智能尺寸（选中圆/弧段）。返回 IDimension（SystemValue 为米）。</summary>
        public IDimension AddRadialDimensionMm(IModelDoc2 doc, ISketchSegment segment, double textX, double textY)
        {
            return AddDimCore(doc, segment, textX, textY, "半径",
                (d, x, y) => d.IAddRadialDimension2(x, y, 0));
        }

        /// <summary>
        /// 水平智能尺寸（选两个点，或单选一条斜线标水平投影）。
        /// points 为草图坐标毫米（尺寸文本位置）。
        /// </summary>
        public IDimension AddHorizontalDimensionMm(IModelDoc2 doc, ISketchPoint[] points,
            ISketchSegment segment, double textX, double textY)
        {
            return AddPointDimCore(doc, points, segment, textX, textY, "水平",
                (d, x, y) => d.IAddHorizontalDimension2(x, y, 0));
        }

        /// <summary>竖直智能尺寸（同水平，竖直方向）。</summary>
        public IDimension AddVerticalDimensionMm(IModelDoc2 doc, ISketchPoint[] points,
            ISketchSegment segment, double textX, double textY)
        {
            return AddPointDimCore(doc, points, segment, textX, textY, "竖直",
                (d, x, y) => d.IAddVerticalDimension2(x, y, 0));
        }

        /// <summary>
        /// 通用线性尺寸（两选实体最近距离；两直线选中时为角度尺寸）。
        /// 用于点-点距离/点-线距离/线-线距离或角度。
        /// </summary>
        public IDimension AddLinearDimensionMm(IModelDoc2 doc, ISketchSegment segA, ISketchSegment segB,
            double textX, double textY)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (segA == null || segB == null) throw new CadException("线性尺寸须选中 2 个草图段。");
            return _session.OnSta(() =>
            {
                try
                {
                    EnsureNoDimPopup();
                    doc.ClearSelection2(true);
                    if (!segA.Select4(false, null) || !segB.Select4(true, null))
                    {
                        throw new CadException("添加线性尺寸失败：草图段选择未命中。");
                    }
                    var disp = doc.IAddDimension2(Units.MmToM(textX), Units.MmToM(textY), 0);
                    return ExtractDimension(disp, "线性");
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("添加线性尺寸", ex); }
            });
        }

        /// <summary>创建直径/半径尺寸公共路径（单段选中）。</summary>
        private IDimension AddDimCore(IModelDoc2 doc, ISketchSegment segment, double textX, double textY,
            string label, Func<IModelDoc2, double, double, DisplayDimension> creator)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (segment == null) throw new ArgumentNullException(nameof(segment));
            return _session.OnSta(() =>
            {
                try
                {
                    EnsureNoDimPopup();
                    doc.ClearSelection2(true);
                    if (!segment.Select4(false, null))
                    {
                        throw new CadException($"添加{label}尺寸失败：草图段选择未命中。");
                    }
                    var disp = creator(doc, Units.MmToM(textX), Units.MmToM(textY));
                    return ExtractDimension(disp, label);
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom($"添加{label}尺寸", ex); }
            });
        }

        /// <summary>创建水平/竖直尺寸公共路径（两点或单段选中）。</summary>
        private IDimension AddPointDimCore(IModelDoc2 doc, ISketchPoint[] points, ISketchSegment segment,
            double textX, double textY, string label,
            Func<IModelDoc2, double, double, DisplayDimension> creator)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if ((points == null || points.Length == 0) && segment == null)
            {
                throw new CadException($"{label}尺寸须给点列或草图段。");
            }
            return _session.OnSta(() =>
            {
                try
                {
                    EnsureNoDimPopup();
                    doc.ClearSelection2(true);
                    int selected = 0;
                    if (points != null)
                    {
                        foreach (var p in points)
                        {
                            if (p != null && p.Select4(selected > 0, null)) selected++;
                        }
                    }
                    if (selected == 0 && segment != null && segment.Select4(false, null)) selected++;
                    if (selected == 0)
                    {
                        throw new CadException($"添加{label}尺寸失败：草图实体选择未命中。");
                    }
                    var disp = creator(doc, Units.MmToM(textX), Units.MmToM(textY));
                    return ExtractDimension(disp, label);
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom($"添加{label}尺寸", ex); }
            });
        }

        private void EnsureNoDimPopup()
        {
            _session.App.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
        }

        private static IDimension ExtractDimension(DisplayDimension disp, string label)
        {
            if (disp == null)
            {
                throw new CadException($"添加{label}尺寸失败：SolidWorks 未创建标注。");
            }
            IDimension dim = disp.GetDimension2(0);
            if (dim == null)
            {
                throw new CadException($"添加{label}尺寸失败：无法取得尺寸对象。");
            }
            return dim;
        }
    }

    /// <summary>草图点（毫米，自由轮廓点序列用）。</summary>
    public struct Point2Mm
    {
        public double X; public double Y;
        public Point2Mm(double x, double y) { X = x; Y = y; }
    }
}
