using System;
using System.Collections.Generic;
using System.Linq;
using SwAiAssistant.Cad.Session;
using SwAiAssistant.Core.Logging;
using SolidWorks.Interop.sldworks;

namespace SwAiAssistant.Cad.Geometry
{
    /// <summary>平面点（毫米，顶面草图坐标系）。</summary>
    public struct PointMm
    {
        public double X;
        public double Y;
        public PointMm(double x, double y) { X = x; Y = y; }
    }

    /// <summary>边分类目标（相对拉伸轴 Z，即零件厚度方向）。</summary>
    public enum EdgeTarget
    {
        /// <summary>全部直边（不含圆边）。</summary>
        AllEdges,
        /// <summary>竖直边：方向与 Z 平行（拉伸侧棱）。</summary>
        VerticalEdges,
        /// <summary>顶面周边（Z 最大处平面面的边界）。</summary>
        TopEdges,
        /// <summary>底面周边（Z 最小处平面面的边界）。</summary>
        BottomEdges
    }

    /// <summary>
    /// 几何语义选择器：把「竖直边/顶边/底边/全部边」翻译成具体
    /// IEdge 列表并选中（Mark 可配），供圆角/倒角等基于选择集的 API 使用。
    /// 原料（SW2026 redist 反射实测）：IPartDoc.GetBodies2 → IBody2.GetFaces/GetEdges →
    /// ISurface.PlaneParams(double[6]: RootX,Y,Z,NormalX,Y,Z) → IFace2.GetBox(double[6] 米) →
    /// ICurve.LineParams(double[7]: RootX,Y,Z,DirX,Y,Z) / CircleParams → IEntity.Select4。
    /// 坐标约定：板类/支座类第一版零件均沿 +Z 拉伸，故以 Z 为「厚度轴」。
    /// </summary>
    public sealed class GeometryService
    {
        /// <summary>方向余弦判定「平行于 Z」的阈值（|dz| ≥ 0.9 ≈ ±25° 内）。</summary>
        private const double ParallelToZThreshold = 0.9;

        private readonly SwSession _session;

        public GeometryService(SwSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// 选中指定分类的直边（追加=false 清选后选中）。
        /// mark：ISelectionMgr Mark（圆角/倒角预选边 Mark=0）。
        /// 返回选中边数（0 时由调用方决定是否报错）。
        /// </summary>
        public int SelectEdges(IModelDoc2 doc, EdgeTarget target, bool append = false, int mark = 0)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    List<IEdge> edges = CollectEdges(doc, target);
                    if (!append) doc.ClearSelection2(true);
                    SelectData sd = CreateSelectData(doc, mark);
                    int n = 0;
                    foreach (IEdge e in edges)
                    {
                        if (((IEntity)e).Select4(true, sd)) n++;
                    }
                    return n;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("选择边（" + target + "）", ex); }
            });
        }

        /// <summary>收集指定分类的直边（不含圆边；去重）。</summary>
        public List<IEdge> CollectEdges(IModelDoc2 doc, EdgeTarget target)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    IBody2[] bodies = GetSolidBodies(doc);
                    var all = new List<IEdge>();
                    foreach (IBody2 b in bodies)
                    {
                        if (!(b.GetEdges() is object[] es)) continue;
                        foreach (object o in es)
                        {
                            if (o is IEdge e && IsLineEdge(e)) all.Add(e);
                        }
                    }
                    switch (target)
                    {
                        case EdgeTarget.VerticalEdges:
                            return all.Where(IsVerticalEdge).ToList();
                        case EdgeTarget.TopEdges:
                            return BoundaryEdgesOfExtremeFace(bodies, all, wantMaxZ: true);
                        case EdgeTarget.BottomEdges:
                            return BoundaryEdgesOfExtremeFace(bodies, all, wantMaxZ: false);
                        default:
                            return all;
                    }
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("收集边（" + target + "）", ex); }
            });
        }

        /// <summary>顶面或底面的法向面（|normalZ| 最大且 Z 极值）。找不到返回 null。</summary>
        public IFace2 FindTopOrBottomFace(IModelDoc2 doc, bool wantMaxZ)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    IFace2 best = null;
                    double bestScore = -1;
                    foreach (IBody2 b in GetSolidBodies(doc))
                    {
                        if (!(b.GetFaces() is object[] fs)) continue;
                        foreach (object o in fs)
                        {
                            if (!(o is IFace2 f)) continue;
                            if (!(f.GetSurface() is ISurface surf) || !surf.IsPlane()) continue;
                            if (!(surf.PlaneParams is double[] p) || p.Length < 6) continue;
                            double nz = p[5];
                            double z = p[2];
                            double score = Math.Abs(nz) * 1e6 + (wantMaxZ ? z : -z);
                            if (score > bestScore)
                            {
                                bestScore = score;
                                best = f;
                            }
                        }
                    }
                    return best;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("查找顶/底面", ex); }
            });
        }

        /// <summary>选中一个面（供抽壳移除面/异型孔放置面等）。</summary>
        public bool SelectFace(IModelDoc2 doc, IFace2 face, bool append = false, int mark = 0)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (face == null) return false;
            return _session.OnSta(() =>
            {
                try
                {
                    if (!append) doc.ClearSelection2(true);
                    return ((IEntity)face).Select4(true, CreateSelectData(doc, mark));
                }
                catch (Exception ex) { throw CadException.FromCom("选择面", ex); }
            });
        }

        // ============================================================
        // T13 变换类特征选择器：方向边 / 旋转轴 / 侧立面 / 面集
        // ============================================================

        /// <summary>
        /// 选中一条平行于指定轴（x|y|z）的直边作为线性阵列方向（Mark 可配）。
        /// 取最长的匹配边（基体长边，避免误选种子凸台短边导致阵列方向解析异常）。
        /// 返回是否选中。
        /// </summary>
        public bool SelectDirectionEdge(IModelDoc2 doc, string axis, bool append = false, int mark = 0)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    string a = (axis ?? "x").ToLowerInvariant();
                    int comp = a == "y" ? 4 : a == "z" ? 5 : 3; // LineParams: Root(0..2), Dir(3..5)
                    IEdge best = null;
                    double bestLen = -1;
                    foreach (IEdge e in CollectEdges(doc, EdgeTarget.AllEdges))
                    {
                        if (!(((ICurve)e.GetCurve()).LineParams is double[] lp) || lp.Length < 6) continue;
                        if (Math.Abs(lp[comp]) < ParallelToZThreshold) continue;
                        double len = EdgeLengthM(e);
                        if (len > bestLen) { bestLen = len; best = e; }
                    }
                    if (best == null) return false;
                    if (!append) doc.ClearSelection2(true);
                    return ((IEntity)best).Select4(true, CreateSelectData(doc, mark));
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("选择方向边（" + axis + "）", ex); }
            });
        }

        /// <summary>直边长度（端点欧氏距离，米；取不到端点时返回 0）。</summary>
        private static double EdgeLengthM(IEdge e)
        {
            try
            {
                if (!(e.GetStartVertex() is IVertex v1) || !(e.GetEndVertex() is IVertex v2)) return 0;
                if (!(v1.GetPoint() is double[] p1) || !(v2.GetPoint() is double[] p2)) return 0;
                if (p1.Length < 3 || p2.Length < 3) return 0;
                double dx = p1[0] - p2[0], dy = p1[1] - p2[1], dz = p1[2] - p2[2];
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            catch { return 0; }
        }

        /// <summary>
        /// 选中圆周阵列旋转轴：轴线平行 Z 的圆柱面（半径最大者——主回转体，
        /// 避免误选孔的小圆柱面）；兜底选半径最大的圆边。返回是否选中。
        /// </summary>
        public bool SelectCircularAxis(IModelDoc2 doc, bool append = false, int mark = 0)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    IEntity best = null;
                    double bestRadius = -1;
                    foreach (IBody2 b in GetSolidBodies(doc))
                    {
                        if (!(b.GetFaces() is object[] fs)) continue;
                        foreach (object o in fs)
                        {
                            if (!(o is IFace2 f)) continue;
                            if (!(f.GetSurface() is ISurface surf) || !surf.IsCylinder()) continue;
                            // CylinderParams: CenterX,Y,Z,AxisX,Y,Z,Radius
                            if (!(surf.CylinderParams is double[] p) || p.Length < 7) continue;
                            if (Math.Abs(p[5]) < ParallelToZThreshold) continue;
                            if (p[6] > bestRadius) { bestRadius = p[6]; best = (IEntity)f; }
                        }
                    }
                    if (best == null)
                    {
                        foreach (IBody2 b in GetSolidBodies(doc))
                        {
                            if (!(b.GetEdges() is object[] es)) continue;
                            foreach (object o in es)
                            {
                                if (!(o is IEdge e) || !(e.GetCurve() is ICurve c)) continue;
                                if (!(c.CircleParams is double[] p) || p.Length < 7) continue;
                                if (Math.Abs(p[5]) < ParallelToZThreshold) continue;
                                if (p[6] > bestRadius) { bestRadius = p[6]; best = (IEntity)e; }
                            }
                        }
                    }
                    if (best == null) return false;
                    if (!append) doc.ClearSelection2(true);
                    return best.Select4(true, CreateSelectData(doc, mark));
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("选择旋转轴", ex); }
            });
        }

        /// <summary>侧立面清单：法向水平（|normalZ| &lt; 0.1）的平面面（拔模面候选）。</summary>
        public List<IFace2> FindSideFaces(IModelDoc2 doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    var result = new List<IFace2>();
                    foreach (IBody2 b in GetSolidBodies(doc))
                    {
                        if (!(b.GetFaces() is object[] fsRaw)) continue;
                        foreach (object o in fsRaw)
                        {
                            if (!(o is IFace2 f)) continue;
                            if (!(f.GetSurface() is ISurface surf) || !surf.IsPlane()) continue;
                            if (!(surf.PlaneParams is double[] p) || p.Length < 6) continue;
                            if (Math.Abs(p[5]) < 0.1) // 法向近似水平（竖直侧立面）
                            {
                                result.Add(f);
                            }
                        }
                    }
                    return result;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("查找侧立面", ex); }
            });
        }

        /// <summary>选中一组面（Mark 可配），返回选中数。</summary>
        public int SelectFaces(IModelDoc2 doc, IList<IFace2> faces, bool append = false, int mark = 0)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (faces == null) throw new ArgumentNullException(nameof(faces));
            return _session.OnSta(() =>
            {
                try
                {
                    if (!append) doc.ClearSelection2(true);
                    SelectData sd = CreateSelectData(doc, mark);
                    int n = 0;
                    foreach (IFace2 f in faces)
                    {
                        if (f != null && ((IEntity)f).Select4(true, sd)) n++;
                    }
                    return n;
                }
                catch (Exception ex) { throw CadException.FromCom("选择面集", ex); }
            });
        }

        /// <summary>Mark≠0 时经 ISelectionMgr.CreateSelectData 生成带标记的选择数据。</summary>
        private static SelectData CreateSelectData(IModelDoc2 doc, int mark)
        {
            if (mark == 0) return null;
            try
            {
                if (!(doc.SelectionManager is ISelectionMgr sm)) return null;
                SelectData sd = sm.CreateSelectData();
                if (sd != null) sd.Mark = mark;
                return sd;
            }
            catch { return null; }
        }

        private static IBody2[] GetSolidBodies(IModelDoc2 doc)
        {
            object raw = ((IPartDoc)doc).GetBodies2(0 /* swSolidBody */, false);
            if (!(raw is object[] bodies) || bodies.Length == 0)
            {
                throw new CadException("零件当前没有实体：请先建基体特征。");
            }
            // [诊断] GetBodies2 返回体类型统计
            Log.Warn("Cad", $"实体诊断[{doc.GetTitle()}]：GetBodies2 返回 {bodies.Length} 个，"
                + $"IBody2={bodies.OfType<IBody2>().Count()}，"
                + $"类型={string.Join("|", bodies.Select(b => b?.GetType().Name ?? "null"))}");
            return bodies.OfType<IBody2>().ToArray();
        }

        /// <summary>仅保留直边（ICurve.LineParams 可得）。</summary>
        private static bool IsLineEdge(IEdge e)
        {
            try
            {
                if (!(e.GetCurve() is ICurve c)) return false;
                return c.LineParams is double[] lp && lp.Length >= 6;
            }
            catch { return false; }
        }

        /// <summary>方向与 Z 平行（厚度轴）的直边。</summary>
        private static bool IsVerticalEdge(IEdge e)
        {
            try
            {
                var lp = (double[])((ICurve)e.GetCurve()).LineParams;
                double dz = lp[5];
                return Math.Abs(dz) >= ParallelToZThreshold;
            }
            catch { return false; }
        }

        /// <summary>顶面（wantMaxZ=true）或底面（false）的边界直边。</summary>
        private static List<IEdge> BoundaryEdgesOfExtremeFace(IBody2[] bodies, List<IEdge> allLineEdges, bool wantMaxZ)
        {
            IFace2 face = FindTopOrBottomFaceCore(bodies, wantMaxZ);
            if (face == null) return new List<IEdge>();
            var result = new List<IEdge>();
            if (!(face.GetEdges() is object[] es)) return result;
            var faceEdges = new HashSet<IEdge>(es.OfType<IEdge>());
            foreach (IEdge e in allLineEdges)
            {
                if (faceEdges.Contains(e)) result.Add(e);
            }
            return result;
        }

        private static IFace2 FindTopOrBottomFaceCore(IBody2[] bodies, bool wantMaxZ)
        {
            IFace2 best = null;
            double bestScore = -1;
            foreach (IBody2 b in bodies)
            {
                if (!(b.GetFaces() is object[] fs)) continue;
                foreach (object o in fs)
                {
                    if (!(o is IFace2 f)) continue;
                    if (!(f.GetSurface() is ISurface surf) || !surf.IsPlane()) continue;
                    if (!(surf.PlaneParams is double[] p) || p.Length < 6) continue;
                    double nz = p[5];
                    double z = p[2];
                    double score = Math.Abs(nz) * 1e6 + (wantMaxZ ? z : -z);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = f;
                    }
                }
            }
            return best;
        }
    }
}
