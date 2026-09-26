using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SwAiAssistant.Planner.Schema
{
    /// <summary>一条 Schema 校验错误：JSON 路径定位 + 中文说明。</summary>
    public class SchemaError
    {
        public string Path { get; set; } = "";
        public string Message { get; set; } = "";
        public override string ToString() => $"{Path}: {Message}";
    }

    /// <summary>
    /// 特征树 Schema 校验：必填字段、正数尺寸、草图引用顺序、枚举值域。
    /// 全部错误一次返回（带 steps[i].field 路径），供 Planner 反馈给模型纠正。
    /// </summary>
    public static class FeatureTreeValidator
    {
        private static readonly HashSet<string> StepKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sketch", "extrudeBoss", "extrudeCut", "revolveBoss", "revolveCut",
            "fillet", "chamfer", "holeWizard", "linearPattern", "circularPattern",
            "mirror", "rib", "draft", "shell", "setMaterial"
        };

        private static readonly HashSet<string> Planes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "front", "top", "right" };

        private static readonly HashSet<string> EntityTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "rectCenter", "rectCorner", "circle", "arc", "slot", "polygon", "polyline", "centerline" };

        private static readonly HashSet<string> ConstraintTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "coincident", "concentric", "tangent", "parallel", "perpendicular",
          "horizontal", "vertical", "equal", "midpoint", "fix" };

        private static readonly HashSet<string> DimensionKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "horizontal", "vertical", "linear", "diameter", "radius" };

        private static readonly HashSet<string> EdgeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "allEdges", "verticalEdges", "topEdges", "bottomEdges" };

        public static List<SchemaError> Validate(FeatureTree tree)
        {
            var errors = new List<SchemaError>();
            if (tree == null)
            {
                errors.Add(new SchemaError { Path = "$", Message = "特征树为空。" });
                return errors;
            }
            if (!string.Equals(tree.Units, "mm", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new SchemaError { Path = "units", Message = "单位必须为 \"mm\"。" });
            }
            if (tree.Steps == null || tree.Steps.Count == 0)
            {
                errors.Add(new SchemaError { Path = "steps", Message = "steps 不能为空。" });
                return errors;
            }

            var sketchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < tree.Steps.Count; i++)
            {
                var step = tree.Steps[i];
                string path = $"steps[{i}]";
                if (step == null)
                {
                    errors.Add(new SchemaError { Path = path, Message = "步骤为 null。" });
                    continue;
                }
                if (string.IsNullOrWhiteSpace(step.Id))
                {
                    step.Id = "s" + (i + 1).ToString(CultureInfo.InvariantCulture);
                }
                if (!stepIds.Add(step.Id))
                {
                    errors.Add(new SchemaError { Path = path + ".id", Message = $"步骤 Id「{step.Id}」重复。" });
                }
                if (string.IsNullOrWhiteSpace(step.Kind) || !StepKinds.Contains(step.Kind))
                {
                    errors.Add(new SchemaError { Path = path + ".kind", Message = $"未知步骤类型「{step.Kind ?? "<空>"}」。" });
                    continue;
                }

                switch (step.Kind.ToLowerInvariant())
                {
                    case "sketch":
                        ValidateSketch(step.Sketch, path + ".sketch", errors);
                        sketchIds.Add(step.Id);
                        break;
                    case "extrudeboss":
                        RequireSketchRef(step, sketchIds, path, errors);
                        ValidateExtrude(step.Extrude, false, path + ".extrude", errors);
                        break;
                    case "extrudecut":
                        RequireSketchRef(step, sketchIds, path, errors);
                        ValidateExtrude(step.Extrude, true, path + ".extrude", errors);
                        break;
                    case "revolveboss":
                    case "revolvecut":
                        RequireSketchRef(step, sketchIds, path, errors);
                        if (step.Revolve != null && (step.Revolve.AngleDeg <= 0 || step.Revolve.AngleDeg > 360))
                        {
                            errors.Add(new SchemaError { Path = path + ".revolve.angleDeg", Message = "旋转角须在 (0, 360]。" });
                        }
                        break;
                    case "fillet":
                        if (step.Fillet == null) Missing(path + ".fillet", errors);
                        else
                        {
                            Positive(step.Fillet.RadiusMm, path + ".fillet.radiusMm", "圆角半径", errors);
                            if (!EdgeTargets.Contains(step.Fillet.Target ?? ""))
                            {
                                errors.Add(new SchemaError { Path = path + ".fillet.target", Message = "target 须为 allEdges/verticalEdges/topEdges/bottomEdges。" });
                            }
                        }
                        break;
                    case "chamfer":
                        if (step.Chamfer == null) Missing(path + ".chamfer", errors);
                        else
                        {
                            Positive(step.Chamfer.DistanceMm, path + ".chamfer.distanceMm", "倒角距离", errors);
                            if (!EdgeTargets.Contains(step.Chamfer.Target ?? ""))
                            {
                                errors.Add(new SchemaError { Path = path + ".chamfer.target", Message = "target 须为 allEdges/verticalEdges/topEdges/bottomEdges。" });
                            }
                        }
                        break;
                    case "holewizard":
                        ValidateHole(step.Hole, path + ".hole", errors);
                        break;
                    case "linearpattern":
                    case "circularpattern":
                        if (step.Pattern == null) Missing(path + ".pattern", errors);
                        else
                        {
                            if (string.IsNullOrWhiteSpace(step.Pattern.SourceStepId))
                            {
                                errors.Add(new SchemaError { Path = path + ".pattern.sourceStepId", Message = "须指定被阵列特征步骤 Id。" });
                            }
                            if (step.Pattern.Count == null || step.Pattern.Count < 2)
                            {
                                errors.Add(new SchemaError { Path = path + ".pattern.count", Message = "阵列数量须 ≥ 2。" });
                            }
                            if (step.Kind.Equals("linearPattern", StringComparison.OrdinalIgnoreCase))
                            {
                                Positive(step.Pattern.SpacingMm, path + ".pattern.spacingMm", "阵列间距", errors);
                            }
                        }
                        break;
                    case "mirror":
                        if (step.Mirror == null) Missing(path + ".mirror", errors);
                        else
                        {
                            if (string.IsNullOrWhiteSpace(step.Mirror.SourceStepId))
                            {
                                errors.Add(new SchemaError { Path = path + ".mirror.sourceStepId", Message = "须指定被镜像特征步骤 Id。" });
                            }
                            if (!Planes.Contains(step.Mirror.Plane ?? ""))
                            {
                                errors.Add(new SchemaError { Path = path + ".mirror.plane", Message = "镜像面须为 front/top/right。" });
                            }
                        }
                        break;
                    case "rib":
                        if (step.Rib == null) Missing(path + ".rib", errors);
                        else Positive(step.Rib.ThicknessMm, path + ".rib.thicknessMm", "筋厚度", errors);
                        RequireSketchRef(step, sketchIds, path, errors);
                        break;
                    case "draft":
                        if (step.Draft == null) Missing(path + ".draft", errors);
                        else Positive(step.Draft.AngleDeg, path + ".draft.angleDeg", "拔模角", errors);
                        break;
                    case "shell":
                        if (step.Shell == null) Missing(path + ".shell", errors);
                        else Positive(step.Shell.ThicknessMm, path + ".shell.thicknessMm", "抽壳厚度", errors);
                        break;
                    case "setmaterial":
                        if (step.SetMaterial == null || string.IsNullOrWhiteSpace(step.SetMaterial.MaterialName))
                        {
                            errors.Add(new SchemaError { Path = path + ".setMaterial.materialName", Message = "材料名不能为空。" });
                        }
                        break;
                }
            }
            return errors;
        }

        private static void RequireSketchRef(PlanStep step, HashSet<string> sketchIds,
            string path, List<SchemaError> errors)
        {
            if (string.IsNullOrWhiteSpace(step.SketchId))
            {
                errors.Add(new SchemaError { Path = path + ".sketchId", Message = "特征步骤须引用草图步骤 Id。" });
            }
            else if (!sketchIds.Contains(step.SketchId))
            {
                errors.Add(new SchemaError
                {
                    Path = path + ".sketchId",
                    Message = $"引用的草图「{step.SketchId}」不存在或未在之前定义（特征顺序须先草图后特征）。"
                });
            }
        }

        private static void ValidateSketch(SketchSpec sketch, string path, List<SchemaError> errors)
        {
            if (sketch == null) { Missing(path, errors); return; }
            if (!Planes.Contains(sketch.Plane ?? ""))
            {
                errors.Add(new SchemaError { Path = path + ".plane", Message = "基准面须为 front/top/right。" });
            }
            if (sketch.Entities == null || sketch.Entities.Count == 0)
            {
                errors.Add(new SchemaError { Path = path + ".entities", Message = "草图实体不能为空。" });
                return;
            }
            for (int i = 0; i < sketch.Entities.Count; i++)
            {
                var e = sketch.Entities[i];
                string ep = $"{path}.entities[{i}]";
                if (e == null || string.IsNullOrWhiteSpace(e.Type) || !EntityTypes.Contains(e.Type))
                {
                    errors.Add(new SchemaError { Path = ep + ".type", Message = $"未知草图实体类型「{e?.Type ?? "<空>"}」。" });
                    continue;
                }
                switch (e.Type.ToLowerInvariant())
                {
                    case "rectcenter":
                        Positive(e.Width, ep + ".width", "矩形宽", errors);
                        Positive(e.Height, ep + ".height", "矩形高", errors);
                        Require(e.Cx, ep + ".cx", errors); Require(e.Cy, ep + ".cy", errors);
                        break;
                    case "rectcorner":
                        Require(e.X1, ep + ".x1", errors); Require(e.Y1, ep + ".y1", errors);
                        Require(e.X2, ep + ".x2", errors); Require(e.Y2, ep + ".y2", errors);
                        if (e.X1.HasValue && e.X2.HasValue && Math.Abs(e.X2.Value - e.X1.Value) < 1e-9)
                        {
                            errors.Add(new SchemaError { Path = ep, Message = "矩形两角点 X 相同，宽度为 0。" });
                        }
                        if (e.Y1.HasValue && e.Y2.HasValue && Math.Abs(e.Y2.Value - e.Y1.Value) < 1e-9)
                        {
                            errors.Add(new SchemaError { Path = ep, Message = "矩形两角点 Y 相同，高度为 0。" });
                        }
                        break;
                    case "circle":
                        Require(e.Cx, ep + ".cx", errors); Require(e.Cy, ep + ".cy", errors);
                        if (e.Diameter == null && e.Radius == null)
                        {
                            errors.Add(new SchemaError { Path = ep, Message = "圆须给 diameter 或 radius。" });
                        }
                        if (e.Diameter != null) Positive(e.Diameter, ep + ".diameter", "圆直径", errors);
                        if (e.Radius != null) Positive(e.Radius, ep + ".radius", "圆半径", errors);
                        break;
                    case "arc":
                        // 圆心 + 起点 + 终点（端点须落在圆周上，执行前不再做距离校验，SW 自身会约束）
                        Require(e.Cx, ep + ".cx", errors); Require(e.Cy, ep + ".cy", errors);
                        Require(e.X1, ep + ".x1", errors); Require(e.Y1, ep + ".y1", errors);
                        Require(e.X2, ep + ".x2", errors); Require(e.Y2, ep + ".y2", errors);
                        break;
                    case "slot":
                        Require(e.Cx, ep + ".cx", errors); Require(e.Cy, ep + ".cy", errors);
                        Positive(e.Length, ep + ".length", "槽口长", errors);
                        Positive(e.Width, ep + ".width", "槽口宽", errors);
                        if (e.Length != null && e.Width != null && e.Length < e.Width)
                        {
                            errors.Add(new SchemaError { Path = ep + ".length", Message = "槽口长度须 ≥ 宽度。" });
                        }
                        break;
                    case "polygon":
                        Require(e.Cx, ep + ".cx", errors); Require(e.Cy, ep + ".cy", errors);
                        if (e.Sides == null || e.Sides < 3 || e.Sides > 64)
                        {
                            errors.Add(new SchemaError { Path = ep + ".sides", Message = "多边形边数须在 3~64。" });
                        }
                        Positive(e.CircumDiameter, ep + ".circumDiameter", "外接圆直径", errors);
                        break;
                    case "polyline":
                        if (e.Points == null || e.Points.Count < 2)
                        {
                            errors.Add(new SchemaError { Path = ep + ".points", Message = "自由轮廓至少 2 个点。" });
                        }
                        else if (e.Closed == true && e.Points.Count < 3)
                        {
                            errors.Add(new SchemaError { Path = ep + ".points", Message = "闭合轮廓至少 3 个点。" });
                        }
                        break;
                    case "centerline":
                        Require(e.X1, ep + ".x1", errors); Require(e.Y1, ep + ".y1", errors);
                        Require(e.X2, ep + ".x2", errors); Require(e.Y2, ep + ".y2", errors);
                        break;
                }
            }

            int entityCount = sketch.Entities?.Count ?? 0;

            // 约束声明校验（0.2.0 Schema 接受、执行器显式降级不施加）
            if (sketch.Constraints != null)
            {
                for (int i = 0; i < sketch.Constraints.Count; i++)
                {
                    var c = sketch.Constraints[i];
                    string cp = $"{path}.constraints[{i}]";
                    if (c == null || string.IsNullOrWhiteSpace(c.Type) || !ConstraintTypes.Contains(c.Type))
                    {
                        errors.Add(new SchemaError { Path = cp + ".type",
                            Message = $"未知约束类型「{c?.Type ?? "<空>"}」。" });
                        continue;
                    }
                    if (c.EntityRefs == null || c.EntityRefs.Count == 0)
                    {
                        errors.Add(new SchemaError { Path = cp + ".entityRefs", Message = "约束至少引用 1 个草图实体。" });
                        continue;
                    }
                    foreach (int idx in c.EntityRefs)
                    {
                        if (idx < 0 || idx >= entityCount)
                        {
                            errors.Add(new SchemaError { Path = cp + ".entityRefs",
                                Message = $"实体下标 {idx} 越界（本草图 {entityCount} 个实体，下标 0~{entityCount - 1}）。" });
                        }
                    }
                }
            }

            // 标注声明校验（同上，执行器显式降级）
            if (sketch.Dimensions != null)
            {
                for (int i = 0; i < sketch.Dimensions.Count; i++)
                {
                    var d = sketch.Dimensions[i];
                    string dp = $"{path}.dimensions[{i}]";
                    if (d == null || string.IsNullOrWhiteSpace(d.Kind) || !DimensionKinds.Contains(d.Kind))
                    {
                        errors.Add(new SchemaError { Path = dp + ".kind",
                            Message = $"未知标注类型「{d?.Kind ?? "<空>"}」（horizontal/vertical/linear/diameter/radius）。" });
                        continue;
                    }
                    if (d.EntityRef < 0 || d.EntityRef >= entityCount)
                    {
                        errors.Add(new SchemaError { Path = dp + ".entityRef",
                            Message = $"实体下标 {d.EntityRef} 越界（本草图 {entityCount} 个实体）。" });
                    }
                    if (d.SecondEntityRef.HasValue
                        && (d.SecondEntityRef.Value < 0 || d.SecondEntityRef.Value >= entityCount))
                    {
                        errors.Add(new SchemaError { Path = dp + ".secondEntityRef",
                            Message = $"第二实体下标 {d.SecondEntityRef.Value} 越界。" });
                    }
                    if (!(d.ValueMm > 0))
                    {
                        errors.Add(new SchemaError { Path = dp + ".valueMm", Message = "标注值必须为正数。" });
                    }
                }
            }
        }

        private static void ValidateExtrude(ExtrudeSpec ex, bool isCut, string path, List<SchemaError> errors)
        {
            if (ex == null) { Missing(path, errors); return; }
            if (!ex.ThroughAll)
            {
                Positive(ex.DepthMm, path + ".depthMm", "拉伸深度", errors);
            }
            if (!isCut && ex.ThroughAll)
            {
                errors.Add(new SchemaError { Path = path + ".throughAll", Message = "凸台拉伸不支持 throughAll（请给 depthMm）。" });
            }
        }

        private static void ValidateHole(HoleSpec hole, string path, List<SchemaError> errors)
        {
            if (hole == null) { Missing(path, errors); return; }
            Positive(hole.DiameterMm, path + ".diameterMm", "孔径", errors);
            if (!hole.ThroughAll) Positive(hole.DepthMm, path + ".depthMm", "孔深", errors);
            bool isCbore = hole.CboreDiameterMm != null || hole.CboreDepthMm != null;
            if (isCbore)
            {
                Positive(hole.CboreDiameterMm, path + ".cboreDiameterMm", "沉孔直径", errors);
                Positive(hole.CboreDepthMm, path + ".cboreDepthMm", "沉孔深度", errors);
                if (hole.CboreDiameterMm != null && hole.DiameterMm != null
                    && hole.CboreDiameterMm <= hole.DiameterMm)
                {
                    errors.Add(new SchemaError { Path = path + ".cboreDiameterMm", Message = "沉孔直径须大于孔径。" });
                }
            }
            if (hole.Positions == null || hole.Positions.Count == 0)
            {
                errors.Add(new SchemaError { Path = path + ".positions", Message = "孔位点列不能为空。" });
            }
            if (hole.Threaded && string.IsNullOrWhiteSpace(hole.ThreadSpec))
            {
                errors.Add(new SchemaError { Path = path + ".threadSpec",
                    Message = "螺纹孔（threaded=true）须给 threadSpec（如 M6x1-6H）；diameterMm 应填攻丝底孔直径。" });
            }
        }

        private static void Missing(string path, List<SchemaError> errors)
        {
            errors.Add(new SchemaError { Path = path, Message = "缺少该步骤类型对应的载荷对象。" });
        }

        private static void Require(double? v, string path, List<SchemaError> errors)
        {
            if (v == null)
            {
                errors.Add(new SchemaError { Path = path, Message = "缺少必填数值。" });
            }
        }

        private static void Positive(double? v, string path, string label, List<SchemaError> errors)
        {
            if (v == null)
            {
                errors.Add(new SchemaError { Path = path, Message = $"{label}为必填。" });
            }
            else if (v <= 0)
            {
                errors.Add(new SchemaError { Path = path, Message = $"{label}必须为正数（实际 {v.Value}）。" });
            }
        }

        /// <summary>把错误列表格式化为反馈给模型的中文文本。</summary>
        public static string FormatErrors(IEnumerable<SchemaError> errors)
        {
            var sb = new StringBuilder();
            int i = 1;
            foreach (var e in errors)
            {
                sb.AppendLine($"{i}. {e}");
                i++;
            }
            return sb.ToString();
        }
    }
}
