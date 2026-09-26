using System;
using System.Collections.Generic;
using System.Linq;
using SwAiAssistant.Cad.Session;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAiAssistant.Cad.Queries
{
    /// <summary>质量属性（MKS 单位：m³ / kg / m²）。</summary>
    public sealed class MassProps
    {
        public double VolumeM3 { get; set; }
        public double MassKg { get; set; }
        public double SurfaceAreaM2 { get; set; }

        public double VolumeMm3 => Units.M3ToMm3(VolumeM3);
    }

    /// <summary>包围盒（毫米）。</summary>
    public sealed class BoxMm
    {
        public double SizeX { get; set; }
        public double SizeY { get; set; }
        public double SizeZ { get; set; }

        /// <summary>三边尺寸升序（方向无关比较用）。</summary>
        public double[] SortedSizes() => new[] { SizeX, SizeY, SizeZ }.OrderBy(v => v).ToArray();
    }

    /// <summary>单条草图段回读（TR-11.1 用）。</summary>
    public sealed class SketchSegmentInfo
    {
        /// <summary>swSketchLINE=0 / ARC=1 / ELLIPSE=2 / SPLINE=3。</summary>
        public int Type { get; set; }
        public string TypeName { get; set; }
        public double LengthMm { get; set; }
        /// <summary>构造几何（不参与特征轮廓：矩形对角线/多边形构造圆/中心线）。</summary>
        public bool IsConstruction { get; set; }
    }

    /// <summary>草图实体回读汇总（段按类型分组计数 + 槽口数；计数默认排除构造几何）。</summary>
    public sealed class SketchEntitySummary
    {
        public List<SketchSegmentInfo> Segments { get; } = new List<SketchSegmentInfo>();
        public int SlotCount { get; set; }
        public int PointCount { get; set; }

        public int CountOf(int swType) => Segments.Count(s => s.Type == swType && !s.IsConstruction);
        public int LineCount => CountOf(0);
        public int ArcCount => CountOf(1);

        public int ConstructionLineCount => Segments.Count(s => s.Type == 0 && s.IsConstruction);
        public int ConstructionArcCount => Segments.Count(s => s.Type == 1 && s.IsConstruction);

        public double MaxSegmentLengthMm => Segments.Count == 0 ? 0 : Segments.Max(s => s.LengthMm);
    }

    /// <summary>
    /// 真实回读：质量属性、包围盒、特征清单。校验闭环（AC-11）的数据源。
    /// COM 签名经 SW2026 redist 反射实测：
    ///   IModelDocExtension.CreateMassProperty2() → IMassProperty2
    ///   （UseSystemUnits=true 时按 MKS 返回 m³/kg）；
    ///   IPartDoc.GetBodies2(swSolidBody=0, visibleOnly) → IBody2[].GetBodyBox() → double[6]（米）。
    /// </summary>
    public sealed class QueryService
    {
        private readonly SwSession _session;

        public QueryService(SwSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>重建后回读质量属性（无实体时报 CadException）。</summary>
        public MassProps GetMassProps(IModelDoc2 doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    doc.ForceRebuild3(false);
                    object raw = doc.Extension.CreateMassProperty2();
                    if (!(raw is IMassProperty2 mp))
                    {
                        throw new CadException("回读质量属性失败：零件当前没有实体。");
                    }
                    mp.UseSystemUnits = true; // MKS：m³ / kg / m²
                    mp.Recalculate();
                    return new MassProps
                    {
                        VolumeM3 = mp.Volume,
                        MassKg = mp.Mass,
                        SurfaceAreaM2 = mp.SurfaceArea
                    };
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("回读质量属性", ex); }
            });
        }

        /// <summary>全部实体合并包围盒（无实体时报 CadException）。</summary>
        public BoxMm GetBoundingBoxMm(IModelDoc2 doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    object raw = ((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false);
                    if (!(raw is object[] bodies) || bodies.Length == 0)
                    {
                        throw new CadException("回读包围盒失败：零件当前没有实体。");
                    }
                    double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                    foreach (IBody2 body in bodies.OfType<IBody2>())
                    {
                        if (!(body.GetBodyBox() is double[] b) || b.Length < 6) continue;
                        minX = Math.Min(minX, b[0]); minY = Math.Min(minY, b[1]); minZ = Math.Min(minZ, b[2]);
                        maxX = Math.Max(maxX, b[3]); maxY = Math.Max(maxY, b[4]); maxZ = Math.Max(maxZ, b[5]);
                    }
                    if (minX > maxX)
                    {
                        throw new CadException("回读包围盒失败：实体包围盒为空。");
                    }
                    return new BoxMm
                    {
                        SizeX = Units.MToMm(maxX - minX),
                        SizeY = Units.MToMm(maxY - minY),
                        SizeZ = Units.MToMm(maxZ - minZ)
                    };
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("回读包围盒", ex); }
            });
        }

        /// <summary>特征树顶层特征的名称与类型名（建模顺序）。</summary>
        public sealed class FeatureInfo
        {
            public string Name { get; set; } = "";
            /// <summary>IFeature.GetTypeName2()（如 HoleWizard / ProfileFeature / RefPlane）。</summary>
            public string TypeName { get; set; } = "";
        }

        /// <summary>特征树顶层特征枚举（名 + 类型名，建模顺序）。</summary>
        public FeatureInfo[] GetFeatureInfos(IModelDoc2 doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    var infos = new List<FeatureInfo>();
                    IFeature feat = doc.FirstFeature() as IFeature;
                    while (feat != null)
                    {
                        infos.Add(new FeatureInfo
                        {
                            Name = feat.Name,
                            TypeName = SafeTypeName(feat)
                        });
                        feat = feat.GetNextFeature() as IFeature;
                    }
                    return infos.ToArray();
                }
                catch (Exception ex) { throw CadException.FromCom("枚举特征", ex); }
            });
        }

        /// <summary>特征树顶层特征名清单（建模顺序）。</summary>
        public string[] GetFeatureNames(IModelDoc2 doc)
            => GetFeatureInfos(doc).Select(f => f.Name).ToArray();

        private static string SafeTypeName(IFeature feat)
        {
            try { return feat.GetTypeName2() ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// 特征树中最后一个草图（ProfileFeature）的名称——拉伸/切除前
        /// 经 SelectByID2(name, "SKETCH") 重新选中草图用（AddToDB 退出后选择集已清空）。
        /// </summary>
        public string GetLatestSketchName(IModelDoc2 doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    string latest = null;
                    IFeature feat = doc.FirstFeature() as IFeature;
                    while (feat != null)
                    {
                        if (feat.GetTypeName2() == "ProfileFeature")
                        {
                            latest = feat.Name;
                        }
                        feat = feat.GetNextFeature() as IFeature;
                    }
                    if (latest == null)
                    {
                        throw new CadException("未找到草图特征：确认已在零件中创建草图。");
                    }
                    return latest;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("查找草图特征", ex); }
            });
        }

        /// <summary>
        /// 草图实体回读：经 IFeature.GetSpecificFeature2() → ISketch 枚举段类型/长度，
        /// 加槽口/点计数（槽口 SketchSlot 不在 GetSketchSegments 列表中，单独计数）。
        /// </summary>
        public SketchEntitySummary GetSketchEntities(IModelDoc2 doc, string sketchName)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(sketchName)) throw new ArgumentNullException(nameof(sketchName));
            return _session.OnSta(() =>
            {
                try
                {
                    var summary = new SketchEntitySummary();
                    string docTitle = doc.GetTitle();
                    IModelDoc2 activated = _session.App.ActivateDoc3(docTitle, false, 0, 0) as IModelDoc2;
                    if (activated == null)
                    {
                        throw new CadException($"激活文档「{docTitle}」失败，无法回读草图实体。");
                    }
                    doc = activated;
                    IFeature sketchFeat = FindSketchFeatureByName(doc, sketchName);
                    doc.ClearSelection2(true);
                    sketchFeat.Select2(false, 0);
                    doc.EditSketch();
                    try
                    {
                        ISketch sketch = FindSketchByName(doc, sketchName);
                        if (sketch.GetSketchSegments() is object[] segs)
                        {
                            foreach (object o in segs)
                            {
                                if (!(o is ISketchSegment seg)) continue;
                                int t = seg.GetType();
                                summary.Segments.Add(new SketchSegmentInfo
                                {
                                    Type = t,
                                    TypeName = SegmentTypeName(t),
                                    LengthMm = Units.MToMm(seg.GetLength()),
                                    IsConstruction = seg.ConstructionGeometry
                                });
                            }
                        }
                        summary.SlotCount = sketch.GetSketchSlotCount();
                        summary.PointCount = sketch.GetSketchPointsCount();
                    }
                    finally
                    {
                        try { doc.SketchManager.InsertSketch(true); } catch { /* 退出失败忽略 */ }
                        try { doc.ClearSelection2(true); } catch { /* 忽略 */ }
                    }
                    return summary;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("回读草图实体", ex); }
            });
        }

        private static string SegmentTypeName(int swType)
        {
            switch (swType)
            {
                case 0: return "LINE";
                case 1: return "ARC";
                case 2: return "ELLIPSE";
                case 3: return "SPLINE";
                default: return "TYPE" + swType;
            }
        }

        /// <summary>按名定位草图特征（IFeature，供 Select2 选中进入编辑）。</summary>
        private static IFeature FindSketchFeatureByName(IModelDoc2 doc, string sketchName)
        {
            IFeature found = null;
            IFeature feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.Name, sketchName, StringComparison.OrdinalIgnoreCase)
                    && feat.GetTypeName2() == "ProfileFeature")
                {
                    found = feat;
                }
                feat = feat.GetNextFeature() as IFeature;
            }
            if (found == null)
            {
                throw new CadException($"特征树中未找到草图「{sketchName}」。");
            }
            return found;
        }

        /// <summary>按名定位草图特征并取 ISketch（取同名最后者，找不到时报 CadException）。</summary>
        private static ISketch FindSketchByName(IModelDoc2 doc, string sketchName)
        {
            IFeature feat = FindSketchFeatureByName(doc, sketchName);
            if (feat.GetSpecificFeature2() is ISketch sketch)
            {
                return sketch;
            }
            throw new CadException($"草图「{sketchName}」无法取得 ISketch 对象。");
        }
    }
}
