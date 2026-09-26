using System;
using System.Collections.Generic;
using System.Linq;
using SwAiAssistant.Cad.Geometry;
using SwAiAssistant.Cad.Session;
using SwAiAssistant.Core.Logging;
using SolidWorks.Interop.sldworks;

namespace SwAiAssistant.Cad.Features
{
    /// <summary>
    /// 特征原语：拉伸凸台 / 拉伸切除。
    /// COM 签名经 SW2026 redist 反射实测：
    ///   FeatureExtrusion3（25 参，末三参 T0/StartOffset/FlipStartOffset 为起始条件扩展）；
    ///   FeatureCut4（28 参，含 T0/StartOffset/FlipStartOffset/OptimizeGeometry）。
    /// SW2025 同签名（2021 起即为此签名），无需版本降级；若未来遇到旧版
    /// 缺失，按 task T5 约定反射降级 FeatureExtrusion/FeatureCut（少起始条件参数）。
    /// 单位约定：深度一律毫米输入。
    /// </summary>
    public sealed class FeatureService
    {
        // swEndConditions_e：盲拉伸 / 贯穿所有（反射实测值）
        private const int EndCondBlind = 0;
        private const int EndCondThroughAll = 1;

        private readonly SwSession _session;

        public FeatureService(SwSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// 单向盲拉伸凸台。
        /// sketchName：目标草图特征名（经 SelectByID2 "SKETCH" 选中——拉伸 API 基于选择集工作，
        /// AddToDB 退出草图后选择集已清空，必须重新选中）。
        /// reverseDir=true 时沿默认方向反向拉伸。
        /// </summary>
        public IFeature ExtrudeBossMm(IModelDoc2 doc, string sketchName, double depthMm, bool reverseDir = false)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (depthMm <= 0) throw new ArgumentOutOfRangeException(nameof(depthMm), "拉伸深度必须为正。");
            return _session.OnSta(() =>
            {
                try
                {
                    SelectSketch(doc, sketchName);
                    IFeature feat = doc.FeatureManager.FeatureExtrusion3(
                        true,               // Sd：单向
                        false,              // Flip
                        reverseDir,         // Dir：方向1反向
                        EndCondBlind,       // T1
                        EndCondBlind,       // T2
                        Units.MmToM(depthMm), // D1（米）
                        0,                  // D2
                        false, false,       // 拔模开关1/2
                        false, false,       // 拔模方向1/2
                        0, 0,               // 拔模角1/2
                        false, false,       // OffsetReverse1/2
                        false, false,       // TranslateSurface1/2
                        true,               // Merge：合并结果
                        true,               // UseFeatScope
                        true,               // UseAutoSelect
                        0, 0, false);       // T0 / StartOffset / FlipStartOffset
                    if (feat == null)
                    {
                        throw new CadException($"拉伸凸台失败（深度 {depthMm} mm）：确认草图为闭合轮廓且无自交。");
                    }
                    Log.Info("Cad", $"拉伸凸台完成：深度 {depthMm} mm，特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("拉伸凸台", ex); }
            });
        }

        /// <summary>双向贯穿拉伸切除，保证切透任意厚度。</summary>
        public IFeature ExtrudeCutThroughAll(IModelDoc2 doc, string sketchName)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    SelectSketch(doc, sketchName);
                    IFeature feat = doc.FeatureManager.FeatureCut4(
                        false,              // Sd：false=双向
                        false,              // Flip
                        false,              // Dir
                        EndCondThroughAll,  // T1
                        EndCondThroughAll,  // T2
                        0, 0,               // D1/D2（贯穿无效）
                        false, false, false, false, 0, 0,
                        false, false,       // OffsetReverse1/2
                        false, false,       // TranslateSurface1/2
                        false,              // NormalCut（钣金法向切除，零件无效）
                        true,               // UseFeatScope
                        true,               // UseAutoSelect
                        false,              // AssemblyFeatureScope
                        true,               // AutoSelectComponents
                        false,              // PropagateFeatureToParts
                        0, 0, false,        // T0 / StartOffset / FlipStartOffset
                        false);             // OptimizeGeometry
                    if (feat == null)
                    {
                        throw new CadException("拉伸切除失败：确认切除轮廓与实体相交。");
                    }
                    Log.Info("Cad", $"拉伸切除（双向贯穿）完成：特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("拉伸切除", ex); }
            });
        }

        /// <summary>单向盲拉伸切除（指定深度，mm）。</summary>
        public IFeature ExtrudeCutBlindMm(IModelDoc2 doc, string sketchName, double depthMm, bool reverseDir = false)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (depthMm <= 0) throw new ArgumentOutOfRangeException(nameof(depthMm), "切除深度必须为正。");
            return _session.OnSta(() =>
            {
                try
                {
                    SelectSketch(doc, sketchName);
                    IFeature feat = doc.FeatureManager.FeatureCut4(
                        true,               // Sd：单向
                        false,              // Flip
                        reverseDir,         // Dir：方向1反向
                        EndCondBlind,       // T1
                        EndCondBlind,       // T2
                        Units.MmToM(depthMm), // D1（米）
                        0,                  // D2
                        false, false, false, false, 0, 0,
                        false, false,       // OffsetReverse1/2
                        false, false,       // TranslateSurface1/2
                        false,              // NormalCut
                        true,               // UseFeatScope
                        true,               // UseAutoSelect
                        false,              // AssemblyFeatureScope
                        true,               // AutoSelectComponents
                        false,              // PropagateFeatureToParts
                        0, 0, false,        // T0 / StartOffset / FlipStartOffset
                        false);             // OptimizeGeometry
                    if (feat == null)
                    {
                        throw new CadException($"盲切除失败（深度 {depthMm} mm）：确认切除轮廓与实体相交。");
                    }
                    Log.Info("Cad", $"拉伸切除（盲 {depthMm} mm）完成：特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("盲拉伸切除", ex); }
            });
        }

        /// <summary>按当前名查找特征并改名（草图命名走这里，避免脆弱的选择集 API）。</summary>
        public void RenameFeatureByName(IModelDoc2 doc, string oldName, string newName)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _session.OnSta<object>(() =>
            {
                try
                {
                    IFeature feat = doc.FirstFeature() as IFeature;
                    while (feat != null)
                    {
                        if (string.Equals(feat.Name, oldName, StringComparison.Ordinal))
                        {
                            feat.Name = newName;
                            return null;
                        }
                        feat = feat.GetNextFeature() as IFeature;
                    }
                    Log.Warn("Cad", $"按名查找特征失败（改名跳过）：「{oldName}」");
                    return null;
                }
                catch (Exception ex) { throw CadException.FromCom("特征改名", ex); }
            });
        }

        /// <summary>特征改名（T14 起 AI 特征统一 AI__ 前缀命名）。</summary>
        public void Rename(IFeature feature, string name)
        {
            if (feature == null) throw new ArgumentNullException(nameof(feature));
            _session.OnSta<object>(() =>
            {
                try { feature.Name = name; }
                catch (Exception ex) { throw CadException.FromCom("特征改名", ex); }
                return null;
            });
        }

        // ============================================================
        // T12 放置类特征：圆角 / 倒角 / 孔（异型孔向导尝试 + 降级）
        // ============================================================

        /// <summary>
        /// 等半径圆角。预选指定分类直边（Mark=0），FeatureFillet3 简单等半径。
        /// 半径毫米输入。返回圆角特征（按「前后特征名差集」取得）。
        /// </summary>
        public IFeature FilletMm(IModelDoc2 doc, GeometryService geo, EdgeTarget target, double radiusMm)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            if (radiusMm <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMm), "圆角半径必须为正。");
            return _session.OnSta(() =>
            {
                try
                {
                    string[] before = FeatureNames(doc);
                    int n = geo.SelectEdges(doc, target, append: false, mark: 0);
                    if (n == 0)
                    {
                        throw new CadException($"圆角失败：未选中任何「{target}」边（确认零件已有直边）。");
                    }
                    double r = Units.MmToM(radiusMm);
                    doc.FeatureManager.FeatureFillet3(
                        3,                  // Options = Propagate|UniformRadius
                        r,                  // R1
                        0, 0,               // R2 / Rho
                        0,                  // Ftyp = Simple
                        0,                  // OverflowType = Default
                        0,                  // ConicRhoType
                        new object[] { r }, // Radii
                        new object[0], new object[0], new object[0],
                        new object[0], new object[0], new object[0]);
                    IFeature feat = NewFeatureSince(doc, before, "Fillet");
                    Log.Info("Cad", $"圆角完成：R{radiusMm} mm，{n} 条「{target}」边，特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("圆角", ex); }
            });
        }

        /// <summary>
        /// 等距倒角（45°，距离×距离）。预选指定分类直边，InsertFeatureChamfer。
        /// 距离毫米输入。
        /// </summary>
        public IFeature ChamferMm(IModelDoc2 doc, GeometryService geo, EdgeTarget target, double distanceMm)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            if (distanceMm <= 0) throw new ArgumentOutOfRangeException(nameof(distanceMm), "倒角距离必须为正。");
            return _session.OnSta(() =>
            {
                try
                {
                    string[] before = FeatureNames(doc);
                    int n = geo.SelectEdges(doc, target, append: false, mark: 0);
                    if (n == 0)
                    {
                        throw new CadException($"倒角失败：未选中任何「{target}」边。");
                    }
                    double d = Units.MmToM(distanceMm);
                    IFeature feat = doc.FeatureManager.InsertFeatureChamfer(
                        0,                  // Options
                        2,                  // ChamferType = DistanceDistance
                        d,                  // Width（距离1）
                        Math.PI / 4.0,      // Angle = 45°
                        d,                  // OtherDist（距离2）
                        0, 0, 0);           // VertexChamDist1..3
                    if (feat == null) feat = NewFeatureSince(doc, before, "Chamfer");
                    Log.Info("Cad", $"倒角完成：C{distanceMm} mm，{n} 条「{target}」边，特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("倒角", ex); }
            });
        }

        /// <summary>
        /// 孔：优先异型孔向导（GB），失败/不可用降级为「草图圆 + 拉伸切除」。
        /// 沉孔降级 = 两级切除（沉孔段盲切 + 通孔段贯穿/盲切）。
        /// positions 为孔心点列（顶面草图坐标，毫米）。
        /// 返回 true 表示走了异型孔向导；false 表示降级（降级细节写入 note）。
        /// </summary>
        public bool HoleMm(IModelDoc2 doc, Sketching.SketchService sketch, Queries.QueryService query,
            IList<PointMm> positions, double diameterMm, bool throughAll, double depthMm,
            double? cboreDiameterMm, double? cboreDepthMm, out string note)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (sketch == null) throw new ArgumentNullException(nameof(sketch));
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (positions == null || positions.Count == 0)
                throw new ArgumentException("孔位点列不能为空。", nameof(positions));
            if (diameterMm <= 0) throw new ArgumentOutOfRangeException(nameof(diameterMm), "孔径必须为正。");
            bool isCbore = cboreDiameterMm.HasValue && cboreDepthMm.HasValue;

            // 尝试异型孔向导（依赖 Toolbox 数据库，目标机可能未装）
            bool wizardOk = false;
            string wizardErr = null;
            try
            {
                wizardOk = _session.OnSta(() => TryHoleWizard(doc, positions, diameterMm, throughAll,
                    depthMm, isCbore, cboreDiameterMm ?? 0, cboreDepthMm ?? 0));
            }
            catch (Exception ex)
            {
                wizardErr = ex.Message;
                Log.Warn("Cad", "异型孔向导异常，降级草图切除：" + ex.Message);
            }
            if (wizardOk)
            {
                note = null;
                Log.Info("Cad", $"异型孔向导建成：φ{diameterMm} ×{positions.Count}" + (isCbore ? "（沉孔）" : ""));
                return true;
            }

            // 降级路径：草图圆 + 拉伸切除
            note = "异型孔向导不可用（" + (wizardErr ?? "返回空") + "），已降级为草图圆+拉伸切除。";
            Log.Info("Cad", note);
            _session.OnSta<object>(() =>
            {
                // 通孔段。逐孔加直径智能尺寸——使孔径可按全名寻址（T14 对话修改「改孔径」），
                // AddToDB=false 才能进入含标注的常规草图（直写模式不支持智能尺寸回读寻址）。
                sketch.SelectPlane(doc, Sketching.PlaneKind.Top);
                sketch.BeginSketch(doc, false);
                foreach (var p in positions)
                {
                    ISketchSegment circle = sketch.CreateCircleMm(doc, p.X, p.Y, diameterMm / 2.0);
                    try { sketch.AddDiameterDimensionMm(doc, circle, p.X + diameterMm, p.Y + diameterMm); }
                    catch (Exception dimEx) { Log.Warn("Cad", "降级孔直径标注失败（跳过，不影响切除）：" + dimEx.Message); }
                }
                sketch.EndSketch(doc);
                string holeSketch = query.GetLatestSketchName(doc);
                if (throughAll) ExtrudeCutThroughAll(doc, holeSketch);
                else ExtrudeCutBlindMm(doc, holeSketch, depthMm);

                // 沉孔段（两级切除）
                if (isCbore)
                {
                    sketch.SelectPlane(doc, Sketching.PlaneKind.Top);
                    sketch.BeginSketch(doc, false);
                    foreach (var p in positions)
                    {
                        ISketchSegment cboreCircle = sketch.CreateCircleMm(doc, p.X, p.Y, cboreDiameterMm.Value / 2.0);
                        try { sketch.AddDiameterDimensionMm(doc, cboreCircle, p.X + cboreDiameterMm.Value, p.Y + cboreDiameterMm.Value); }
                        catch (Exception dimEx) { Log.Warn("Cad", "沉孔直径标注失败（跳过）：" + dimEx.Message); }
                    }
                    sketch.EndSketch(doc);
                    string cboreSketch = query.GetLatestSketchName(doc);
                    ExtrudeCutBlindMm(doc, cboreSketch, cboreDepthMm.Value);
                }
                return null;
            });
            Log.Info("Cad", $"降级孔建成：φ{diameterMm} ×{positions.Count}" + (isCbore ? "（沉孔两级切除）" : ""));
            return false;
        }

        /// <summary>异型孔向导（GB 标准）。返回 true 表示成功建成 ≥1 个孔特征。</summary>
        private bool TryHoleWizard(IModelDoc2 doc, IList<PointMm> positions,
            double diameterMm, bool throughAll, double depthMm,
            bool isCbore, double cboreDiameterMm, double cboreDepthMm)
        {
            string[] before = FeatureNames(doc);
            int created = 0;
            foreach (var p in positions)
            {
                // 选中顶面作为放置面
                doc.ClearSelection2(true);
                if (!doc.Extension.SelectByID2("", "FACE", Units.MmToM(p.X), Units.MmToM(p.Y), Units.MmToM(0), false, 0, null, 0))
                {
                    // 面选不中直接放弃向导
                    return false;
                }
                int generalType = isCbore ? 0 : 2;   // CounterBore=0 / Hole=2
                int standard = 13;                   // GB
                int fastener = isCbore ? 361 : 355;  // GBHexagonSocketHeadCapScrews / GBDrillSizes
                string size = "M" + diameterMm.ToString("0.##");
                short endType = (short)(throughAll ? 1 : 0); // 1=ThroughAll / 0=Blind
                object feat = doc.FeatureManager.HoleWizard5(
                    generalType, standard, fastener, size,
                    endType, Units.MmToM(diameterMm), Units.MmToM(throughAll ? 0 : depthMm),
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    "", false, true, true, false, true, false);
                if (feat != null) created++;
            }
            if (created > 0)
            {
                return true;
            }
            // 无新特征即视为失败
            string[] after = FeatureNames(doc);
            return after.Length > before.Length;
        }

        // ============================================================
        // T13 变换类特征：阵列 / 镜像 / 旋转 / 筋 / 拔模 / 抽壳
        // ============================================================

        /// <summary>
        /// 线性阵列。Mark 约定：种子特征=4，方向1 边=1（DName 传 "" 走预选方向边）。
        /// 方向边经几何语义选择器取「平行于指定轴的直边」。
        /// </summary>
        public IFeature LinearPatternMm(IModelDoc2 doc, GeometryService geo, string seedFeatureName,
            string directionAxis, int count, double spacingMm)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            if (count < 2) throw new ArgumentOutOfRangeException(nameof(count), "阵列数量须 ≥ 2。");
            if (spacingMm <= 0) throw new ArgumentOutOfRangeException(nameof(spacingMm), "阵列间距必须为正。");
            return _session.OnSta(() =>
            {
                try
                {
                    doc.ClearSelection2(true);
                    SelectFeatureByName(doc, seedFeatureName, append: false, mark: 4);
                    if (!geo.SelectDirectionEdge(doc, directionAxis, append: true, mark: 1))
                    {
                        throw new CadException($"线性阵列失败：未找到平行 {(directionAxis ?? "x").ToUpperInvariant()} 轴的方向边。");
                    }
                    IFeature feat = doc.FeatureManager.FeatureLinearPattern2(
                        count, Units.MmToM(spacingMm), 1, 0,
                        false, false, "", "", false);
                    if (feat == null)
                    {
                        throw new CadException("线性阵列失败：SolidWorks 返回空（确认种子特征与方向边有效）。");
                    }
                    Log.Info("Cad", $"线性阵列完成：{count}×{spacingMm} mm 沿 {(directionAxis ?? "x").ToUpperInvariant()}，特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("线性阵列", ex); }
            });
        }

        /// <summary>
        /// 圆周阵列。Mark 约定：种子特征=4，轴=1（预选圆柱面/圆边，半径最大者）。
        /// EqualSpacing=true：Spacing=总张角弧度（360° 均布=2π）。
        /// </summary>
        public IFeature CircularPatternMm(IModelDoc2 doc, GeometryService geo, string seedFeatureName,
            int count, double totalAngleDeg)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            if (count < 2) throw new ArgumentOutOfRangeException(nameof(count), "阵列数量须 ≥ 2。");
            if (totalAngleDeg <= 0 || totalAngleDeg > 360)
                throw new ArgumentOutOfRangeException(nameof(totalAngleDeg), "阵列总角须在 (0, 360]。");
            return _session.OnSta(() =>
            {
                try
                {
                    doc.ClearSelection2(true);
                    SelectFeatureByName(doc, seedFeatureName, append: false, mark: 4);
                    if (!geo.SelectCircularAxis(doc, append: true, mark: 1))
                    {
                        throw new CadException("圆周阵列失败：未找到旋转轴（轴线平行拉伸方向的圆柱面/圆边）。");
                    }
                    IFeature feat = doc.FeatureManager.FeatureCircularPattern4(
                        count, Units.DegToRad(totalAngleDeg), false, "", false, true, false);
                    if (feat == null)
                    {
                        throw new CadException("圆周阵列失败：SolidWorks 返回空。");
                    }
                    Log.Info("Cad", $"圆周阵列完成：{count}×{totalAngleDeg}°，特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("圆周阵列", ex); }
            });
        }

        /// <summary>
        /// 特征镜像。Mark 约定（SW2026 官方文档）：被镜像特征=1，镜像基准面=2。
        /// BMirrorBody=false / BGeometryPattern=false / BMerge=true / BKnit=false / ScopeOptions=0。
        /// 选择顺序：先选特征（Mark=1）再追加选面（Mark=2）。
        /// </summary>
        public IFeature MirrorFeature(IModelDoc2 doc, Sketching.PlaneKind plane, string featureName)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    string[] before = FeatureNames(doc);
                    doc.ClearSelection2(true);
                    SelectFeatureByName(doc, featureName, append: false, mark: 1);
                    SelectPlaneWithMark(doc, plane, mark: 2, append: true);
                    IFeature feat = doc.FeatureManager.InsertMirrorFeature2(false, false, true, false, 0);
                    // 部分 SW 版本镜像成功也返回 null：按前后特征名差集兜底取特征
                    if (feat == null) feat = NewFeatureSince(doc, before, "Mirror");
                    Log.Info("Cad", $"镜像完成：「{featureName}」关于 {plane}，特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("镜像", ex); }
            });
        }

        /// <summary>
        /// 旋转凸台。草图须含中心线（自动作旋转轴）；Dir1Type=0（盲，按角度）。
        /// </summary>
        public IFeature RevolveMm(IModelDoc2 doc, string sketchName, double angleDeg)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (angleDeg <= 0 || angleDeg > 360)
                throw new ArgumentOutOfRangeException(nameof(angleDeg), "旋转角须在 (0, 360]。");
            return _session.OnSta(() =>
            {
                try
                {
                    SelectSketch(doc, sketchName);
                    IFeature feat = doc.FeatureManager.FeatureRevolve2(
                        true,               // SingleDir
                        true,               // IsSolid
                        false,              // IsThin
                        false,              // IsCut
                        false,              // ReverseDir
                        false,              // BothDirectionUpToSameEntity
                        0,                  // Dir1Type = blind（按角度）
                        0,                  // Dir2Type
                        Units.DegToRad(angleDeg), // Dir1Angle（弧度）
                        0,                  // Dir2Angle
                        false, false,       // OffsetReverse1/2
                        0, 0,               // OffsetDistance1/2
                        0, 0, 0,            // ThinType / ThinThickness1/2
                        true,               // Merge
                        true,               // UseFeatScope
                        true);              // UseAutoSelect
                    if (feat == null)
                    {
                        throw new CadException("旋转凸台失败：确认草图含中心线且轮廓闭合、位于轴线一侧。");
                    }
                    Log.Info("Cad", $"旋转凸台完成：{angleDeg}°，特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("旋转凸台", ex); }
            });
        }

        /// <summary>旋转切除。草图须含中心线（自动作旋转轴）。</summary>
        public IFeature RevolveCutMm(IModelDoc2 doc, string sketchName, double angleDeg)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (angleDeg <= 0 || angleDeg > 360)
                throw new ArgumentOutOfRangeException(nameof(angleDeg), "旋转角须在 (0, 360]。");
            return _session.OnSta(() =>
            {
                try
                {
                    SelectSketch(doc, sketchName);
                    IFeature feat = doc.FeatureManager.FeatureRevolveCut2(
                        Units.DegToRad(angleDeg), // Angle（弧度）
                        false,              // ReverseDir
                        0,                  // Angle2
                        0,                  // RevType = blind
                        0,                  // Options
                        true,               // UseFeatScope
                        true,               // UseAutoSelect
                        false,              // AssemblyFeatureScope
                        true,               // AutoSelectComponents
                        false);             // PropagateFeatureToParts
                    if (feat == null)
                    {
                        throw new CadException("旋转切除失败：确认切除轮廓与实体相交。");
                    }
                    Log.Info("Cad", $"旋转切除完成：{angleDeg}°，特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("旋转切除", ex); }
            });
        }

        /// <summary>
        /// 筋（开轮廓草图）。InsertRib 返回 void：首次失败（异常或无新特征）时
        /// 按材料方向问题 ReverseMaterialDir=true 重试一次；特征经前后名差集取得。
        /// 官方 C# 示例约定：退出草图编辑态后 SelectByID2 选中 SKETCH（特征树选中，
        /// 非编辑态），再 InsertRib；EditSketch 激活编辑态会导致 SW2026 静默拒绝。
        /// silent=true 时返回 null 而非抛异常，供调用方降级处理。
        /// </summary>
        public IFeature RibMm(IModelDoc2 doc, string sketchName, double thicknessMm, bool silent = false)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (thicknessMm <= 0) throw new ArgumentOutOfRangeException(nameof(thicknessMm), "筋厚度必须为正。");
            return _session.OnSta(() =>
            {
                try
                {
                    string[] before = FeatureNames(doc);
                    double t = Units.MmToM(thicknessMm);
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        // 关键坑位：必须在非编辑态选中草图（官方示例路径）。
                        // EditSketch 激活编辑态会让 InsertRib 静默忽略（SW2026 实测）。
                        doc.ClearSelection2(true);
                        doc.Extension.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null, 0);
                        try
                        {
                            // Is2Sided=true：双侧加厚（官方 C# 示例用法，对开/闭轮廓最宽松）；
                            // IsNormToSketch=false：挤出方向平行于草图，筋沿轮廓延伸方向加厚。
                            doc.FeatureManager.InsertRib(true, false, t, 0, attempt == 1, false, false, 0, false, false);
                        }
                        catch (Exception ribEx)
                        {
                            Log.Warn("Cad", $"筋第 {attempt + 1} 次创建异常：" + ribEx.Message);
                        }
                        IFeature feat = NewFeatureSince(doc, before, "Rib", silent: true);
                        if (feat != null)
                        {
                            Log.Info("Cad", $"筋完成：{thicknessMm} mm，特征「{SafeName(feat)}」");
                            return feat;
                        }
                        Log.Warn("Cad", $"筋第 {attempt + 1} 次无新特征，"
                            + (attempt == 0 ? "ReverseMaterialDir=true 重试。" : "放弃。"));
                    }
                    if (silent) return null;
                    throw new CadException("筋创建失败：InsertRib 未产生新特征（轮廓延伸可能碰不到实体边界）。");
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("筋", ex); }
            });
        }

        /// <summary>
        /// 拔模（中性面=底面，PropType=4 外环传播到全部外侧面）。
        /// 官方约定：必须用 IModelDocExtension.SelectByID2 预选，Mark：中性面/方向=1，
        /// 拔模面=2，拔模边=4；IEntity.Select4 的 Mark 不被 InsertMultiFaceDraft 识别（返回 null）。
        /// PropType=4=propagate to all faces neighbor of neutral plane on outer loop（盒子侧面）。
        /// </summary>
        public IFeature DraftMm(IModelDoc2 doc, GeometryService geo, double angleDeg)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            if (angleDeg <= 0 || angleDeg >= 45)
                throw new ArgumentOutOfRangeException(nameof(angleDeg), "拔模角须在 (0, 45)。");
            return _session.OnSta(() =>
            {
                try
                {
                    string[] before = FeatureNames(doc);
                    IFace2 neutral = geo.FindTopOrBottomFace(doc, wantMaxZ: false);
                    if (neutral == null)
                    {
                        throw new CadException("拔模失败：未找到中性面（底面）。");
                    }
                    // 取中性面包围盒中心点作为 SelectByID2 的命中坐标（面上一点）。
                    double[] box = neutral.GetBox() as double[];
                    if (box == null || box.Length < 6)
                    {
                        throw new CadException("拔模失败：无法取得中性面包围盒。");
                    }
                    double cx = (box[0] + box[3]) / 2.0, cy = (box[1] + box[4]) / 2.0, cz = (box[2] + box[5]) / 2.0;
                    doc.ClearSelection2(true);
                    // Mark=1：中性面（方向参考）。SelectByID2 按坐标命中底面。
                    if (!doc.Extension.SelectByID2("", "FACE", cx, cy, cz, false, 1, null, 0))
                    {
                        throw new CadException("拔模失败：中性面选择未命中。");
                    }
                    // PropType=4：传播到中性面外环所有邻接面（盒子四侧面），无需逐个预选拔模面。
                    // FlipDir=true：以底面（Z=0）为中性面时使顶面向内收缩（减材料）；
                    // false 会让顶面外扩（体积反而增大，SW2026 实测方向与直觉相反）。
                    IFeature feat = doc.FeatureManager.InsertMultiFaceDraft(
                        Units.DegToRad(angleDeg), true, false, 4, false, false);
                    // 关键坑位：InsertMultiFaceDraft 可能返回非 null 但特征名非「Draft」
                    // （SW 中文环境显示为「拔模1」），直接信任 API 返回值。
                    if (feat == null) feat = NewFeatureSince(doc, before, "Draft");
                    Log.Info("Cad", $"拔模完成：{angleDeg}°（外环传播），特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("拔模", ex); }
            });
        }

        /// <summary>
        /// 抽壳。removeFace：top（移除顶面）| bottom（移除底面）| none（闭壳）。
        /// InsertFeatureShell 返回 void：预选移除面 Mark=0，特征经前后名差集取得。
        /// </summary>
        public IFeature ShellMm(IModelDoc2 doc, GeometryService geo, double thicknessMm, string removeFace)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            if (thicknessMm <= 0) throw new ArgumentOutOfRangeException(nameof(thicknessMm), "抽壳厚度必须为正。");
            return _session.OnSta(() =>
            {
                try
                {
                    string[] before = FeatureNames(doc);
                    bool open = !string.Equals(removeFace, "none", StringComparison.OrdinalIgnoreCase);
                    if (open)
                    {
                        bool wantMaxZ = !string.Equals(removeFace, "bottom", StringComparison.OrdinalIgnoreCase);
                        IFace2 face = geo.FindTopOrBottomFace(doc, wantMaxZ);
                        if (face == null)
                        {
                            throw new CadException("抽壳失败：未找到要移除的" + (wantMaxZ ? "顶" : "底") + "面。");
                        }
                        geo.SelectFace(doc, face, append: false, mark: 0);
                    }
                    else
                    {
                        doc.ClearSelection2(true);
                    }
                    doc.InsertFeatureShell(Units.MmToM(thicknessMm), false);
                    IFeature feat = NewFeatureSince(doc, before, "Shell");
                    Log.Info("Cad", $"抽壳完成：{thicknessMm} mm（移除面 {removeFace ?? "top"}），特征「{SafeName(feat)}」");
                    return feat;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("抽壳", ex); }
            });
        }

        /// <summary>按名选中特征（BODYFEATURE），Mark 可配。</summary>
        private static void SelectFeatureByName(IModelDoc2 doc, string featureName, bool append, int mark)
        {
            if (string.IsNullOrWhiteSpace(featureName))
            {
                throw new CadException("未指定目标特征名。");
            }
            if (!doc.Extension.SelectByID2(featureName, "BODYFEATURE", 0, 0, 0, append, mark, null, 0))
            {
                throw new CadException($"选中特征失败：「{featureName}」（确认特征属于当前活动零件）。");
            }
        }

        /// <summary>按候选名选中基准面并打 Mark（镜像面用）。</summary>
        private static void SelectPlaneWithMark(IModelDoc2 doc, Sketching.PlaneKind plane, int mark, bool append = false)
        {
            foreach (string name in Sketching.SketchService.PlaneCandidates(plane))
            {
                if (doc.Extension.SelectByID2(name, "PLANE", 0, 0, 0, append, mark, null, 0))
                {
                    return;
                }
            }
            throw new CadException($"选择基准面失败：{plane}。");
        }

        /// <summary>当前特征树顶层特征名（建模顺序）。</summary>
        private static string[] FeatureNames(IModelDoc2 doc)
        {
            var names = new List<string>();
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                names.Add(f.Name);
                f = f.GetNextFeature() as IFeature;
            }
            return names.ToArray();
        }

        /// <summary>取「before 之后新增」的特征；找不到时按类型名兜底取最新。silent=true 找不到时返回 null 而不抛。</summary>
        private static IFeature NewFeatureSince(IModelDoc2 doc, string[] before, string typeNameFallback, bool silent = false)
        {
            var beforeSet = new HashSet<string>(before ?? new string[0], StringComparer.Ordinal);
            IFeature lastNew = null;
            IFeature lastOfType = null;
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                if (!beforeSet.Contains(f.Name)) lastNew = f;
                try { if (f.GetTypeName2() == typeNameFallback) lastOfType = f; } catch { }
                f = f.GetNextFeature() as IFeature;
            }
            IFeature result = lastNew ?? lastOfType;
            if (result == null && !silent)
            {
                throw new CadException($"特征「{typeNameFallback}」建成后未能定位（特征树差集为空）。");
            }
            return result;
        }

        /// <summary>
        /// 删除指定名称的 BODYFEATURE（SelectByID2 选中 + EditDelete，等效 Delete 键）。
        /// 分层纪律：Planner（快照回滚/对话删特征）不得直接调 SelectByID2/EditDelete。
        /// 调用方须自行保证只删 AI 特征（防误删红线在 Planner 的 FeatureRegistry 侧）。
        /// </summary>
        public void DeleteFeature(IModelDoc2 doc, string featureName)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(featureName))
                throw new ArgumentNullException(nameof(featureName));
            _session.OnSta<object>(() =>
            {
                try
                {
                    doc.ClearSelection2(true);
                    if (!doc.Extension.SelectByID2(featureName, "BODYFEATURE", 0, 0, 0, false, 0, null, 0))
                    {
                        throw new CadException($"选中特征失败：「{featureName}」，无法删除。");
                    }
                    doc.EditDelete();
                    Log.Info("Cad", $"特征已删除：「{featureName}」");
                    return null;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom($"删除特征「{featureName}」", ex); }
            });
        }

        /// <summary>选中草图（拉伸/切除 API 基于选择集工作）。</summary>
        private static void SelectSketch(IModelDoc2 doc, string sketchName)
        {
            if (string.IsNullOrWhiteSpace(sketchName))
            {
                throw new CadException("未指定目标草图：请先创建草图并传入其特征名。");
            }
            doc.ClearSelection2(true);
            if (!doc.Extension.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null, 0))
            {
                throw new CadException($"选中草图失败：「{sketchName}」（确认草图属于当前活动零件）。");
            }
        }

        private static string SafeName(IFeature feat)
        {
            try { return feat.Name; } catch { return "<未知>"; }
        }
    }
}
