using System.Collections.Generic;

namespace SwAiAssistant.Reverse.Dxf
{
    /// <summary>DXF 解析/规划失败（消息始终为中文，可直接展示给用户）。</summary>
    public sealed class DxfParseException : System.Exception
    {
        public DxfParseException(string message) : base(message) { }
        public DxfParseException(string message, System.Exception inner) : base(message, inner) { }
    }

    /// <summary>2D 点（DXF 原始坐标系，未缩放）。</summary>
    public struct DxfPoint
    {
        public double X;
        public double Y;
        public DxfPoint(double x, double y) { X = x; Y = y; }
    }

    /// <summary>闭合轮廓的解析形态。</summary>
    public enum DxfLoopShape
    {
        /// <summary>圆（Center/Radius 有效）。</summary>
        Circle,
        /// <summary>由直线/圆弧/椭圆弧离散点组成的多边形闭合环（Points 有效，轴无关）。</summary>
        Polygon
    }

    /// <summary>
    /// 闭合轮廓（轴无关）：圆保留语义（直接映射草图圆/孔），
    /// 其余形态（多段线、线弧链、椭圆）统一离散为多边形点列（弦高公差控制）。
    /// </summary>
    public sealed class DxfLoop
    {
        public DxfLoopShape Shape { get; set; }
        /// <summary>多边形点列（已离散，闭合环不重复末点）。</summary>
        public List<DxfPoint> Points { get; } = new List<DxfPoint>();
        /// <summary>圆心（Shape=Circle；原始坐标）。</summary>
        public DxfPoint Center { get; set; }
        /// <summary>圆半径（原始单位）。</summary>
        public double Radius { get; set; }
        /// <summary>有向面积绝对值（原始单位²）。</summary>
        public double AreaAbs { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public string LayerName { get; set; } = "";
        /// <summary>来源描述（多段线/圆/线弧链/椭圆…），用于识别报告。</summary>
        public string Source { get; set; } = "";
    }

    /// <summary>标注真值记录（已折算毫米）。</summary>
    public sealed class DxfDimInfo
    {
        /// <summary>linear | diameter | radial | angular | ordinate | other</summary>
        public string Kind { get; set; } = "other";
        public double MeasurementMm { get; set; }
        /// <summary>用户替换文本；"&lt;&gt;"或空表示采用测量值。</summary>
        public string UserText { get; set; } = "";
        public string LayerName { get; set; } = "";
    }

    /// <summary>图层分类条目。</summary>
    public sealed class DxfLayerInfo
    {
        public string Name { get; set; } = "";
        /// <summary>outline | dimension | text | center | hatch | other</summary>
        public string Category { get; set; } = "other";
        public int EntityCount { get; set; }
    }

    /// <summary>
    /// DXF 结构化提取结果（单视图板类图纸优先）：
    /// 最大闭合环=外轮廓，其余闭合环=孔；标注/文本单独保留供尺寸仲裁与置信度评估。
    /// </summary>
    public sealed class DxfSheet
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        /// <summary>$INSUNITS 名称。</summary>
        public string UnitName { get; set; } = "Unitless";
        /// <summary>原始单位 → 毫米的缩放系数。</summary>
        public double ScaleToMm { get; set; } = 1.0;
        public DxfLoop Outer { get; set; }
        public List<DxfLoop> Holes { get; } = new List<DxfLoop>();
        public List<DxfDimInfo> Dimensions { get; } = new List<DxfDimInfo>();
        public List<string> TextLines { get; } = new List<string>();
        public List<DxfLayerInfo> Layers { get; } = new List<DxfLayerInfo>();
        public List<string> Notes { get; } = new List<string>();
    }
}
