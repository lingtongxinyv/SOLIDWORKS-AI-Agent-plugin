using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using SwAiAssistant.AddIn.UI;

namespace SwAiAssistant.AddIn.Hosting
{
    /// <summary>
    /// 任务面板的 WinForms 宿主：通过 ElementHost 承载 WPF 用户控件。
    /// SolidWorks ITaskpaneView.AddControl(ProgID, licKey) 经 COM 类名实例化本控件，
    /// 因此必须 public + 显式 Guid/ProgId + ComVisible + 无参构造函数（RegAsm 时注册）。
    /// </summary>
    [ComVisible(true)]
    [Guid("8F2A7C32-9B4E-4A6D-B1F2-0E3A5C7D8891")]
    [ProgId("SwAiAssistant.TaskPaneHost")]
    public class TaskPaneHost : UserControl
    {
        private readonly ElementHost _elementHost;
        private readonly TaskPaneView _view;

        public TaskPaneHost()
        {
            Dock = DockStyle.Fill;
            _view = new TaskPaneView();
            _elementHost = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = _view
            };
            Controls.Add(_elementHost);
        }

        public void ShowSettings()
        {
            _view.ShowSettings();
        }

        /// <summary>Ribbon 入口：为当前活动零件生成 GB 三视图工程图。</summary>
        public void GenerateDrawing()
        {
            _view.GenerateDrawing();
        }

        /// <summary>由 SwAddIn 在 Cad 会话建立后注入服务组。</summary>
        public void AttachCad(
            SwAiAssistant.Cad.Documents.DocService docs,
            SwAiAssistant.Cad.Sketching.SketchService sketch,
            SwAiAssistant.Cad.Features.FeatureService features,
            SwAiAssistant.Cad.Queries.QueryService query,
            SwAiAssistant.Cad.Capture.CaptureService capture = null,
            SwAiAssistant.Cad.Drawings.DrawingService drawings = null,
            SwAiAssistant.Cad.Assemblies.AssemblyService assemblies = null,
            SwAiAssistant.Cad.Materials.MaterialService materials = null)
        {
            _view.AttachCad(docs, sketch, features, query, capture, drawings, assemblies, materials);
        }

        /// <summary>由 SwAddIn 注入快照服务（M4-T16）。</summary>
        public void AttachSnapshots(SwAiAssistant.Planner.Execution.SnapshotService snapshots)
        {
            _view.AttachSnapshots(snapshots);
        }

        /// <summary>由 SwAddIn 注入 AI 服务组。</summary>
        public void AttachAi(
            SwAiAssistant.Core.Configuration.ConfigService config,
            SwAiAssistant.Ai.CapabilityProbe probe,
            SwAiAssistant.Ai.Scheduling.Scheduler scheduler,
            SwAiAssistant.Planner.PlannerService planner,
            SwAiAssistant.Planner.Execution.PlanExecutor executor,
            SwAiAssistant.Ai.OllamaServiceManager ollamaMgr,
            SwAiAssistant.Planner.Execution.EditPlanner editPlanner = null,
            SwAiAssistant.Planner.Execution.QAService qa = null,
            SwAiAssistant.Cad.Queries.DimensionService dimensions = null)
        {
            _view.AttachAi(config, probe, scheduler, planner, executor, ollamaMgr,
                editPlanner, qa, dimensions);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _elementHost?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
