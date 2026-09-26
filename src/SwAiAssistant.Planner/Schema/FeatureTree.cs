using System.Collections.Generic;
using Newtonsoft.Json;

namespace SwAiAssistant.Planner.Schema
{
    /// <summary>
    /// JSON 特征树 Schema v1（强类型）。LLM 只允许输出此结构；
    /// 所有长度单位毫米（mm），角度单位度（deg）。
    /// 扁平结构：每步一个 kind + 对应载荷字段，便于校验、编辑与执行映射。
    /// </summary>
    public class FeatureTree
    {
        public string Version { get; set; } = "1.0";
        public string Units { get; set; } = "mm";
        public PartSpec Part { get; set; } = new PartSpec();
        public List<PlanStep> Steps { get; set; } = new List<PlanStep>();
    }

    public class PartSpec
    {
        public string Name { get; set; } = "AI零件";
        /// <summary>AI 材料猜测（如 Q235/45钢/6061/304），null 表示未指定。</summary>
        public string MaterialGuess { get; set; }
    }

    /// <summary>建模步骤。Kind 决定启用哪个载荷字段。</summary>
    public class PlanStep
    {
        /// <summary>步骤稳定 ID（s1/s2/…），特征步骤经 SketchId 引用草图步骤。</summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// sketch | extrudeBoss | extrudeCut | revolveBoss | revolveCut |
        /// fillet | chamfer | holeWizard | linearPattern | circularPattern |
        /// mirror | rib | draft | shell | setMaterial
        /// </summary>
        public string Kind { get; set; } = "";

        /// <summary>中文显示名（计划卡片展示）。</summary>
        public string Title { get; set; } = "";

        /// <summary>特征步骤引用的草图步骤 Id。</summary>
        public string SketchId { get; set; }

        public SketchSpec Sketch { get; set; }
        public ExtrudeSpec Extrude { get; set; }
        public RevolveSpec Revolve { get; set; }
        public FilletSpec Fillet { get; set; }
        public ChamferSpec Chamfer { get; set; }
        public HoleSpec Hole { get; set; }
        public PatternSpec Pattern { get; set; }
        public MirrorSpec Mirror { get; set; }
        public RibSpec Rib { get; set; }
        public DraftSpec Draft { get; set; }
        public ShellSpec Shell { get; set; }
        public SetMaterialSpec SetMaterial { get; set; }
    }

    public class SketchSpec
    {
        /// <summary>front（前视 X-Y）| top（上视 X-Z）| right（右视 Y-Z）。</summary>
        public string Plane { get; set; } = "top";
        public List<SketchEntity> Entities { get; set; } = new List<SketchEntity>();

        /// <summary>
        /// 草图几何约束（0.2.0 起 Schema 接受并校验；执行器暂不施加，会在执行报告中显式提示降级。
        /// 几何本身按给定坐标精确创建，通常无需约束。
        /// </summary>
        public List<SketchConstraintSpec> Constraints { get; set; } = new List<SketchConstraintSpec>();

        /// <summary>草图标注尺寸（同上：Schema 接受、执行器暂不施加，显式降级提示）。</summary>
        public List<SketchDimensionSpec> Dimensions { get; set; } = new List<SketchDimensionSpec>();
    }

    public class SketchEntity
    {
        /// <summary>
        /// rectCenter | rectCorner | circle | arc | slot | polygon | polyline |
        /// centerline（旋转轴，构造线）。
        /// </summary>
        public string Type { get; set; } = "";

        // 通用坐标/尺寸字段（按 Type 取用，单位 mm）
        public double? Cx { get; set; }
        public double? Cy { get; set; }
        public double? X1 { get; set; }
        public double? Y1 { get; set; }
        public double? X2 { get; set; }
        public double? Y2 { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public double? Diameter { get; set; }
        public double? Radius { get; set; }
        public double? Length { get; set; }

        /// <summary>槽口/多边形旋转角（度，0=水平）。</summary>
        public double? Angle { get; set; }
        public double? CircumDiameter { get; set; }
        public int? Sides { get; set; }
        public List<Point2> Points { get; set; } = new List<Point2>();
        public bool? Closed { get; set; }

        /// <summary>arc 专用：true=从起点逆时针画到终点（默认 true）。</summary>
        public bool? Ccw { get; set; }
    }

    /// <summary>
    /// 草图约束声明：Type 为约束类型，EntityRefs 为约束作用的草图实体下标
    /// （对应 SketchSpec.Entities 的 0 基下标；两元约束给两个下标）。
    /// </summary>
    public class SketchConstraintSpec
    {
        /// <summary>coincident | concentric | tangent | parallel | perpendicular |
        /// horizontal | vertical | equal | midpoint | fix。</summary>
        public string Type { get; set; } = "";
        public List<int> EntityRefs { get; set; } = new List<int>();
    }

    /// <summary>
    /// 草图标注声明：Kind 为标注种类，EntityRef 为被标注实体下标；
    /// linear 两实体标注时可选 SecondEntityRef。
    /// </summary>
    public class SketchDimensionSpec
    {
        /// <summary>horizontal | vertical | linear | diameter | radius。</summary>
        public string Kind { get; set; } = "";
        public int EntityRef { get; set; }
        public int? SecondEntityRef { get; set; }
        public double ValueMm { get; set; }
    }

    public class Point2
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    public class ExtrudeSpec
    {
        /// <summary>拉伸深度 mm（盲）。ThroughAll 为 true 时忽略。</summary>
        public double? DepthMm { get; set; }

        /// <summary>双向贯穿切除（仅 extrudeCut 用）。</summary>
        public bool ThroughAll { get; set; }

        /// <summary>反方向。</summary>
        public bool Flip { get; set; }
    }

    public class RevolveSpec
    {
        /// <summary>旋转角（度，默认 360）。</summary>
        public double AngleDeg { get; set; } = 360.0;
    }

    public class FilletSpec
    {
        public double? RadiusMm { get; set; }
        /// <summary>allEdges | verticalEdges | topEdges | bottomEdges。</summary>
        public string Target { get; set; } = "allEdges";
    }

    public class ChamferSpec
    {
        public double? DistanceMm { get; set; }
        public string Target { get; set; } = "allEdges";
    }

    public class HoleSpec
    {
        public double? DiameterMm { get; set; }
        public double? DepthMm { get; set; }
        public bool ThroughAll { get; set; } = true;
        /// <summary>沉孔直径/深度（沉孔时必填）。</summary>
        public double? CboreDiameterMm { get; set; }
        public double? CboreDepthMm { get; set; }
        /// <summary>孔中心点列（在最近草图基准面坐标系）。</summary>
        public List<Point2> Positions { get; set; } = new List<Point2>();

        /// <summary>
        /// 是否为螺纹孔。0.2.0 执行器暂不调用异型孔向导螺纹参数/不生成装饰线：
        /// true 时按 DiameterMm（模型应给攻丝底孔直径）创建光孔，并在执行报告中显式提示需手工补螺纹。
        /// </summary>
        public bool Threaded { get; set; }
        /// <summary>螺纹规格标注（如 "M6x1-6H"），仅螺纹孔用；执行器原样写入报告提示，不参与几何。</summary>
        public string ThreadSpec { get; set; }
    }

    public class PatternSpec
    {
        /// <summary>被阵列/镜像的特征步骤 Id。</summary>
        public string SourceStepId { get; set; }
        public double? SpacingMm { get; set; }
        public int? Count { get; set; }
        /// <summary>线性阵列方向：x | y。</summary>
        public string Direction { get; set; } = "x";
        /// <summary>圆周阵列总角（度，默认 360 均布）。</summary>
        public double? TotalAngleDeg { get; set; }
    }

    public class MirrorSpec
    {
        public string SourceStepId { get; set; }
        /// <summary>镜像基准面：front | top | right。</summary>
        public string Plane { get; set; } = "right";
    }

    public class RibSpec
    {
        public double? ThicknessMm { get; set; }
    }

    public class DraftSpec
    {
        public double? AngleDeg { get; set; }
    }

    public class ShellSpec
    {
        public double? ThicknessMm { get; set; }
        /// <summary>移除面：top | bottom | none。</summary>
        public string RemoveFace { get; set; } = "top";
    }

    public class SetMaterialSpec
    {
        public string MaterialName { get; set; } = "";
    }
}
