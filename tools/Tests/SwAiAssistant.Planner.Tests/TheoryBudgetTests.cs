using System;
using SwAiAssistant.Planner.Schema;
using SwAiAssistant.Verify.Theory;
using Xunit;

namespace SwAiAssistant.Planner.Tests
{
    /// <summary>
    /// M4-T17 理论值预算器单测：全部用手算值对比。
    /// 约定：平板类草图放 front 面（草图(x,y)=全局(x,y)，拉伸沿 +z），
    /// 故 100×80×10 板的包围盒为 (100,80,10)，通孔/通切厚度取 Z 向尺寸。
    /// </summary>
    public class TheoryBudgetTests
    {
        // 相对容差 1e-6 或绝对 1e-3
        private static void CloseTo(double expected, double actual)
            => Assert.True(Math.Abs(expected - actual) <= Math.Max(1e-3, Math.Abs(expected) * 1e-6),
                $"期望 {expected}，实际 {actual}");

        private static PlanStep SketchRectCenter(string id, double w, double h, string plane = "front")
            => new PlanStep
            {
                Id = id, Kind = "sketch", Title = "草图",
                Sketch = new SketchSpec
                {
                    Plane = plane,
                    Entities = { new SketchEntity { Type = "rectCenter", Cx = 0, Cy = 0, Width = w, Height = h } }
                }
            };

        private static PlanStep Boss(string id, string sketchId, double depth)
            => new PlanStep
            {
                Id = id, Kind = "extrudeBoss", Title = "凸台",
                SketchId = sketchId, Extrude = new ExtrudeSpec { DepthMm = depth }
            };

        [Fact]
        public void Plate_100x80x10()
        {
            var tree = new FeatureTree
            {
                Steps = { SketchRectCenter("s1", 100, 80), Boss("s2", "s1", 10) }
            };
            var rep = TheoryBudget.Evaluate(tree);
            CloseTo(80000.0, rep.VolumeMm3);
            CloseTo(100.0, rep.BoundingBox.X);
            CloseTo(80.0, rep.BoundingBox.Y);
            CloseTo(10.0, rep.BoundingBox.Z);
            Assert.Equal(0, rep.HoleCount);
            Assert.Equal(2, rep.Steps.Count);
        }

        [Fact]
        public void Plate_With4ThroughHoles()
        {
            var tree = new FeatureTree
            {
                Steps =
                {
                    SketchRectCenter("s1", 100, 80),
                    Boss("s2", "s1", 10),
                    new PlanStep
                    {
                        Id = "s3", Kind = "holeWizard", Title = "通孔",
                        Hole = new HoleSpec
                        {
                            DiameterMm = 10, ThroughAll = true,
                            Positions =
                            {
                                new Point2 { X = -30, Y = -20 }, new Point2 { X = 30, Y = -20 },
                                new Point2 { X = -30, Y = 20 }, new Point2 { X = 30, Y = 20 }
                            }
                        }
                    }
                }
            };
            var rep = TheoryBudget.Evaluate(tree);
            CloseTo(80000.0 - 4 * Math.PI * 25 * 10, rep.VolumeMm3);
            Assert.Equal(4, rep.HoleCount);
            Assert.Equal(new[] { 10.0 }, rep.HoleDiametersMm);
            // 孔不扩大包围盒
            CloseTo(100.0, rep.BoundingBox.X);
            CloseTo(10.0, rep.BoundingBox.Z);
        }

        [Fact]
        public void CylinderBoss_R20_D30()
        {
            var tree = new FeatureTree
            {
                Steps =
                {
                    new PlanStep
                    {
                        Id = "s1", Kind = "sketch",
                        Sketch = new SketchSpec
                        {
                            Plane = "front",
                            Entities = { new SketchEntity { Type = "circle", Cx = 0, Cy = 0, Radius = 20 } }
                        }
                    },
                    Boss("s2", "s1", 30)
                }
            };
            var rep = TheoryBudget.Evaluate(tree);
            CloseTo(Math.PI * 400 * 30, rep.VolumeMm3);
            CloseTo(40.0, rep.BoundingBox.X);
            CloseTo(40.0, rep.BoundingBox.Y);
            CloseTo(30.0, rep.BoundingBox.Z);
        }

        [Fact]
        public void Slot_L50_W20_D10()
        {
            var tree = new FeatureTree
            {
                Steps =
                {
                    new PlanStep
                    {
                        Id = "s1", Kind = "sketch",
                        Sketch = new SketchSpec
                        {
                            Plane = "front",
                            Entities = { new SketchEntity { Type = "slot", Cx = 0, Cy = 0, Length = 50, Width = 20 } }
                        }
                    },
                    Boss("s2", "s1", 10)
                }
            };
            var rep = TheoryBudget.Evaluate(tree);
            // [(50−20)×20 + π×10²] × 10
            CloseTo(((50 - 20) * 20 + Math.PI * 100) * 10, rep.VolumeMm3);
            CloseTo(50.0, rep.BoundingBox.X);
            CloseTo(20.0, rep.BoundingBox.Y);
        }

        [Fact]
        public void HexagonFlange_D40_D8()
        {
            var tree = new FeatureTree
            {
                Steps =
                {
                    new PlanStep
                    {
                        Id = "s1", Kind = "sketch",
                        Sketch = new SketchSpec
                        {
                            Plane = "front",
                            Entities = { new SketchEntity { Type = "polygon", Cx = 0, Cy = 0, Sides = 6, CircumDiameter = 40 } }
                        }
                    },
                    Boss("s2", "s1", 8)
                }
            };
            var rep = TheoryBudget.Evaluate(tree);
            // (6/2)×20²×sin60°×8
            CloseTo(3.0 * 400 * Math.Sin(Math.PI / 3) * 8, rep.VolumeMm3);
            CloseTo(40.0, rep.BoundingBox.X);
            CloseTo(8.0, rep.BoundingBox.Z);
        }

        [Fact]
        public void BlindCut_30x30_D10_In_100x100x20()
        {
            var tree = new FeatureTree
            {
                Steps =
                {
                    SketchRectCenter("s1", 100, 100),
                    Boss("s2", "s1", 20),
                    SketchRectCenter("s3", 30, 30),
                    new PlanStep
                    {
                        Id = "s4", Kind = "extrudeCut", Title = "盲切",
                        SketchId = "s3", Extrude = new ExtrudeSpec { DepthMm = 10 }
                    }
                }
            };
            var rep = TheoryBudget.Evaluate(tree);
            CloseTo(200000.0 - 9000.0, rep.VolumeMm3);
            // 切除不缩小包围盒
            CloseTo(100.0, rep.BoundingBox.X);
            CloseTo(20.0, rep.BoundingBox.Z);
            Assert.True(rep.Steps[3].DeltaVolumeMm3 < 0);
        }

        [Fact]
        public void CboreHole_Plate_80x80x10()
        {
            var tree = new FeatureTree
            {
                Steps =
                {
                    SketchRectCenter("s1", 80, 80),
                    Boss("s2", "s1", 10),
                    new PlanStep
                    {
                        Id = "s3", Kind = "holeWizard", Title = "沉孔",
                        Hole = new HoleSpec
                        {
                            DiameterMm = 8, ThroughAll = true,
                            CboreDiameterMm = 14, CboreDepthMm = 3,
                            Positions = { new Point2 { X = 0, Y = 0 } }
                        }
                    }
                }
            };
            var rep = TheoryBudget.Evaluate(tree);
            // 板体积 = 80×80×10 = 64000；真实去除 = 通孔 φ8 全深 10（π·4²·10）
            // + 沉孔扩径环 (φ14²−φ8²)/4·π·3（π·(49−16)·3）
            // 即 64000 − 160π − 99π（沉孔与通孔重叠的 r 柱不重复扣）
            CloseTo(64000.0 - Math.PI * 16 * 10 - Math.PI * 33 * 3, rep.VolumeMm3);
            Assert.Equal(1, rep.HoleCount);
            Assert.Equal(new[] { 8.0 }, rep.HoleDiametersMm);
        }

        [Fact]
        public void MissingDepth_DoesNotThrow_AndRecorded()
        {
            // 容错：缺深度的凸台记 Estimates 并跳过，不抛异常
            var tree = new FeatureTree
            {
                Steps =
                {
                    SketchRectCenter("s1", 50, 50),
                    new PlanStep { Id = "s2", Kind = "extrudeBoss", SketchId = "s1", Extrude = new ExtrudeSpec() }
                }
            };
            var rep = TheoryBudget.Evaluate(tree);
            CloseTo(0.0, rep.VolumeMm3);
            Assert.NotEmpty(rep.Estimates);
        }
    }
}
