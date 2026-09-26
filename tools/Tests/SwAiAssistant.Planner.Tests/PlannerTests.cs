using System;
using System.Linq;
using SwAiAssistant.Planner;
using SwAiAssistant.Planner.Schema;
using Xunit;

namespace SwAiAssistant.Planner.Tests
{
    public class FeatureTreeValidatorTests
    {
        private static FeatureTree ValidPlate()
        {
            return new FeatureTree
            {
                Steps =
                {
                    new PlanStep
                    {
                        Id = "s1", Kind = "sketch", Title = "板轮廓",
                        Sketch = new SketchSpec
                        {
                            Plane = "top",
                            Entities = { new SketchEntity { Type = "rectCenter", Cx = 0, Cy = 0, Width = 120, Height = 80 } }
                        }
                    },
                    new PlanStep
                    {
                        Id = "s2", Kind = "extrudeBoss", Title = "拉伸板",
                        SketchId = "s1", Extrude = new ExtrudeSpec { DepthMm = 10 }
                    },
                    new PlanStep
                    {
                        Id = "s3", Kind = "sketch", Title = "孔位",
                        Sketch = new SketchSpec
                        {
                            Plane = "top",
                            Entities = { new SketchEntity { Type = "circle", Cx = 50, Cy = 30, Diameter = 6 } }
                        }
                    },
                    new PlanStep
                    {
                        Id = "s4", Kind = "extrudeCut", Title = "通孔",
                        SketchId = "s3", Extrude = new ExtrudeSpec { ThroughAll = true }
                    }
                }
            };
        }

        [Fact]
        public void ValidTree_Passes()
        {
            var errors = FeatureTreeValidator.Validate(ValidPlate());
            Assert.Empty(errors);
        }

        [Fact]
        public void MissingDepth_Rejected()
        {
            var tree = ValidPlate();
            tree.Steps[1].Extrude.DepthMm = null;
            var errors = FeatureTreeValidator.Validate(tree);
            Assert.Contains(errors, e => e.Path.Contains("depthMm"));
        }

        [Fact]
        public void NegativeSize_Rejected()
        {
            var tree = ValidPlate();
            tree.Steps[0].Sketch.Entities[0].Width = -5;
            var errors = FeatureTreeValidator.Validate(tree);
            Assert.Contains(errors, e => e.Path.Contains("width") && e.Message.Contains("正数"));
        }

        [Fact]
        public void ForwardSketchRef_Rejected()
        {
            var tree = ValidPlate();
            tree.Steps[1].SketchId = "s3"; // s3 在 s2 之后定义
            var errors = FeatureTreeValidator.Validate(tree);
            Assert.Contains(errors, e => e.Path.Contains("sketchId"));
        }

        [Fact]
        public void UnknownKind_Rejected()
        {
            var tree = ValidPlate();
            tree.Steps[0].Kind = "laserCut";
            var errors = FeatureTreeValidator.Validate(tree);
            Assert.Contains(errors, e => e.Message.Contains("未知步骤类型"));
        }

        [Fact]
        public void UnitsMustBeMm()
        {
            var tree = ValidPlate();
            tree.Units = "inch";
            var errors = FeatureTreeValidator.Validate(tree);
            Assert.Contains(errors, e => e.Path == "units");
        }

        [Fact]
        public void CboreMustExceedHole()
        {
            var tree = ValidPlate();
            tree.Steps.Add(new PlanStep
            {
                Id = "s5", Kind = "holeWizard", Title = "沉孔",
                Hole = new HoleSpec
                {
                    DiameterMm = 10, CboreDiameterMm = 8, CboreDepthMm = 3,
                    Positions = { new Point2 { X = 0, Y = 0 } }
                }
            });
            var errors = FeatureTreeValidator.Validate(tree);
            Assert.Contains(errors, e => e.Message.Contains("沉孔直径须大于孔径"));
        }
    }

    public class PlanJsonParserTests
    {
        private const string ValidPlanJson =
            "{\"type\":\"plan\",\"plan\":{\"version\":\"1.0\",\"units\":\"mm\"," +
            "\"part\":{\"name\":\"板\"},\"steps\":[" +
            "{\"id\":\"s1\",\"kind\":\"sketch\",\"title\":\"轮廓\",\"sketch\":{\"plane\":\"top\",\"entities\":[" +
            "{\"type\":\"rectCenter\",\"cx\":0,\"cy\":0,\"width\":120,\"height\":80}]}}," +
            "{\"id\":\"s2\",\"kind\":\"extrudeBoss\",\"title\":\"拉伸\",\"sketchId\":\"s1\",\"extrude\":{\"depthMm\":10}}]}}";

        [Fact]
        public void ValidPlan_Parsed()
        {
            bool ok = PlanJsonParser.TryParse(ValidPlanJson, out var resp, out string error);
            Assert.True(ok, error);
            Assert.Equal(PlannerResponseType.Plan, resp.Type);
            Assert.Equal(2, resp.Plan.Steps.Count);
        }

        [Fact]
        public void FencedJson_StillParsed()
        {
            string fenced = "```json\n" + ValidPlanJson + "\n```";
            bool ok = PlanJsonParser.TryParse(fenced, out var resp, out string error);
            Assert.True(ok, error);
            Assert.Equal(PlannerResponseType.Plan, resp.Type);
        }

        [Fact]
        public void CodeSmuggling_Rejected()
        {
            string bad = ValidPlanJson + "\n```python\nimport os\nos.system('x')\n```";
            bool ok = PlanJsonParser.TryParse(bad, out _, out string error);
            Assert.False(ok);
            Assert.Contains("代码", error);
        }

        [Fact]
        public void ProseOnly_Rejected()
        {
            bool ok = PlanJsonParser.TryParse("好的，我来帮你建这个板：先画矩形……", out _, out string error);
            Assert.False(ok);
            Assert.Contains("JSON", error);
        }

        [Fact]
        public void SchemaViolation_RejectedWithPath()
        {
            string bad = "{\"type\":\"plan\",\"plan\":{\"units\":\"mm\",\"steps\":[" +
                "{\"id\":\"s1\",\"kind\":\"extrudeBoss\",\"sketchId\":\"s9\",\"extrude\":{\"depthMm\":0}}]}}";
            bool ok = PlanJsonParser.TryParse(bad, out _, out string error);
            Assert.False(ok);
            Assert.Contains("steps[0]", error);
        }

        [Fact]
        public void ChatResponse_Parsed()
        {
            bool ok = PlanJsonParser.TryParse("{\"type\":\"chat\",\"text\":\"这个零件重 0.75 kg\"}",
                out var resp, out string error);
            Assert.True(ok, error);
            Assert.Equal(PlannerResponseType.Chat, resp.Type);
            Assert.Contains("0.75", resp.Text);
        }
    }
}
