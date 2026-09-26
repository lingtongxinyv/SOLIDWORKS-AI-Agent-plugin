using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SwAiAssistant.AddIn.Hosting;
using SwAiAssistant.AddIn.Ribbon;
using SwAiAssistant.Cad.Documents;
using SwAiAssistant.Cad.Session;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Logging;
using SwAiAssistant.Core.Threading;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace SwAiAssistant.AddIn
{
    /// <summary>
    /// SolidWorks 原生 COM Add-in 主类。
    /// 固定 GUID/ProgId，RegAsm /codebase 注册后由 SolidWorks 启动加载。
    /// </summary>
    [ComVisible(true)]
    [Guid("8F2A7C31-9B4E-4A6D-B1F2-0E3A5C7D8890")]
    [ProgId("SwAiAssistant.AddIn")]
    public class SwAddIn : ISwAddin
    {
        private ISldWorks _swApp;
        private int _cookie;
        private RibbonController _ribbon;
        private ITaskpaneView _taskPaneView;
        private TaskPaneHost _taskPaneHost;
        private bool _paneVisible;

        // Cad 层：唯一触碰 COM 的执行路径（所有调用经 StaExecutor 封送）
        private StaExecutor _sta;
        private SwSession _session;
        private DocService _docs;
        private Cad.Sketching.SketchService _sketch;
        private Cad.Features.FeatureService _features;
        private Cad.Queries.QueryService _query;
        private Cad.Geometry.GeometryService _geo;
        private Cad.Materials.MaterialService _materials;
        private Cad.Drawings.DrawingService _drawings;
        private Cad.Assemblies.AssemblyService _assemblies;
        private Planner.Execution.FeatureRegistry _registry;
        private Planner.Execution.SnapshotService _snapshots;
        private bool _docEventHooked;

        // AI 服务组（T6-T10）
        private Ai.CapabilityProbe _probe;
        private Ai.Scheduling.Scheduler _scheduler;
        private Planner.PlannerService _planner;
        private Planner.Execution.PlanExecutor _executor;
        private Ai.OllamaServiceManager _ollamaMgr;

        // ---- RegAsm 注册钩子：必须标注在「被注册类自身」的静态方法上，RegAsm 才会调用 ----

        [ComRegisterFunction]
        public static void ComRegister(Type type)
        {
            AddInRegistration.RegisterAddInKey(type);
        }

        [ComUnregisterFunction]
        public static void ComUnregister(Type type)
        {
            AddInRegistration.UnregisterAddInKey(type);
        }

        /// <summary>SolidWorks 加载插件时调用（SW UI 的 STA 线程）。</summary>
        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                AppPaths.Ensure();
                _cookie = Cookie;
                _swApp = ThisSW as ISldWorks;
                if (_swApp == null)
                {
                    throw new InvalidOperationException("ISldWorks 对象转换失败。");
                }

                _swApp.SetAddinCallbackInfo2(0, this, _cookie);
                Log.Info("AddIn", $"ConnectToSW：SolidWorks Revision={SafeRevision()}，cookie={_cookie}");

                _ribbon = new RibbonController(_swApp, _cookie);
                _ribbon.Create();

                // 已知问题兜底：启动初期（无文档）创建的「AI 助手」功能区标签页
                // 可能不出现在标签条——挂文档激活事件，每次激活时幂等重试可见性修复。
                // COM 事件定义在 SldWorks coclass 上（ISldWorks 接口无事件成员）。
                try
                {
                    ((SldWorks)_swApp).ActiveDocChangeNotify += OnActiveDocChange;
                    // M4-T19：文档销毁事件——清理特征注册表等会话状态，防止跨文档残留
                    ((SldWorks)_swApp).DestroyNotify += OnDocDestroy;
                    _docEventHooked = true;
                }
                catch (Exception ex)
                {
                    Log.Warn("AddIn", "挂接文档激活事件失败（功能区标签页可能需手动启用）：" + ex.Message);
                }

                CreateTaskPane();

                // Cad 会话：包装宿主传入的既有实例（生命周期归 SolidWorks，不退出进程）
                _sta = new StaExecutor("SwCadSTA");
                _session = SwSession.FromHostedInstance(_sta, _swApp);
                _docs = new DocService(_session);
                _sketch = new Cad.Sketching.SketchService(_session);
                _features = new Cad.Features.FeatureService(_session);
                _query = new Cad.Queries.QueryService(_session);
                _geo = new Cad.Geometry.GeometryService(_session);
                _materials = new Cad.Materials.MaterialService(_session);
                _drawings = new Cad.Drawings.DrawingService(_session, _query, _materials);
                _assemblies = new Cad.Assemblies.AssemblyService(_session);
                _taskPaneHost?.AttachCad(_docs, _sketch, _features, _query,
                    new Cad.Capture.CaptureService(_session), _drawings, _assemblies, _materials);

                // 快照与一键回滚（M4-T16）：特征注册表随会话存活，快照服务注入任务面板
                _registry = new Planner.Execution.FeatureRegistry();
                _snapshots = new Planner.Execution.SnapshotService(_query,
                    new Cad.Queries.DimensionService(_session),
                    _materials, _registry, _docs, _features);
                _taskPaneHost?.AttachSnapshots(_snapshots);

                // AI 服务组：配置 → 能力探测 → 调度 → 规划/执行 → Ollama 管理
                var config = ConfigService.Default;
                _probe = new Ai.CapabilityProbe(config);
                _scheduler = new Ai.Scheduling.Scheduler(config, _probe);
                _planner = new Planner.PlannerService(_scheduler, config);
                _executor = new Planner.Execution.PlanExecutor(_docs, _sketch, _features, _query, _geo,
                    _materials, _registry);
                _ollamaMgr = new Ai.OllamaServiceManager(config);
                // T14/T26 接线：对话修改 + 真实问答（复用同一会话注册表/尺寸服务）
                var dimsForEdit = new Cad.Queries.DimensionService(_session);
                var editPlanner = new Planner.Execution.EditPlanner(_executor, dimsForEdit, _registry,
                    _docs, _features);
                var qa = new Planner.Execution.QAService(_session, _query, dimsForEdit, _registry);
                _taskPaneHost?.AttachAi(config, _probe, _scheduler, _planner, _executor, _ollamaMgr,
                    editPlanner, qa, dimsForEdit);

                Log.Info("AddIn", "ConnectToSW 完成");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("AddIn", "ConnectToSW 失败", ex);
                return false;
            }
        }

        /// <summary>SolidWorks 卸载插件/关闭时调用。</summary>
        public bool DisconnectFromSW()
        {
            try
            {
                Log.Info("AddIn", "DisconnectFromSW 开始");
                if (_docEventHooked && _swApp != null)
                {
                    try
                    {
                        ((SldWorks)_swApp).ActiveDocChangeNotify -= OnActiveDocChange;
                        ((SldWorks)_swApp).DestroyNotify -= OnDocDestroy;
                    }
                    catch (Exception ex) { Log.Warn("AddIn", "退订文档激活事件失败：" + ex.Message); }
                    _docEventHooked = false;
                }
                try
                {
                    _taskPaneView?.DeleteView();
                }
                catch (Exception ex)
                {
                    Log.Warn("AddIn", "删除任务面板视图失败：" + ex.Message);
                }
                _taskPaneHost?.Dispose();
                _ribbon?.Dispose();
                // Cad 会话：宿主实例不退出进程，只释放 RCW；先 Dispose 会话再停 STA 线程
                try { _session?.Dispose(); } catch (Exception ex) { Log.Warn("AddIn", "释放 Cad 会话失败：" + ex.Message); }
                try { _sta?.Dispose(); } catch (Exception ex) { Log.Warn("AddIn", "停止 STA 线程失败：" + ex.Message); }
                _session = null;
                _docs = null;
                _drawings = null;
                _assemblies = null;
                _materials = null;
                _sta = null;
                _probe = null;
                _scheduler = null;
                _planner = null;
                _executor = null;
                _ollamaMgr = null;
                _taskPaneView = null;
                _taskPaneHost = null;
                _ribbon = null;
                _swApp = null;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("AddIn", "DisconnectFromSW 异常", ex);
                return false;
            }
        }

        private void CreateTaskPane()
        {
            string paneIcon = IconFactory.GetOrCreate(16);
            _taskPaneView = _swApp.CreateTaskpaneView2(paneIcon, "AI 助手");
            if (_taskPaneView == null)
            {
                throw new InvalidOperationException("CreateTaskpaneView2 返回 null。");
            }
            // SW 经 ProgID 实例化 WinForms 宿主（RegAsm 已注册该类）
            object created = _taskPaneView.AddControl("SwAiAssistant.TaskPaneHost", "");
            _taskPaneHost = created as TaskPaneHost;
            _taskPaneView.ShowView();
            _paneVisible = true;
        }

        private string SafeRevision()
        {
            try
            {
                return Convert.ToString(_swApp.RevisionNumber());
            }
            catch
            {
                return "<未知>";
            }
        }

        // ---- Ribbon 回调（SW 经 SetAddinCallbackInfo2 以名称反射调用，必须 public） ----

        /// <summary>
        /// 文档激活事件（COM 事件回调，SW UI 线程）：幂等修复「AI 助手」功能区标签页可见性。
        /// 返回值按 DSldWorksEvents 约定固定 0。
        /// </summary>
        private int OnActiveDocChange()
        {
            try
            {
                _ribbon?.EnsureTabsVisible();
            }
            catch (Exception ex)
            {
                Log.Warn("AddIn", "文档激活时修复功能区标签页失败：" + ex.Message);
            }
            return 0;
        }

        /// <summary>
        /// 文档销毁事件（M4-T19）：清空特征注册表，防止已关闭文档的 AI 特征记录残留
        /// （快照尺寸兜底只依赖登记表直读；回滚尺寸来源是快照 JSON 本身，清空不影响回滚）。
        /// 返回值按 DSldWorksEvents 约定固定 0。
        /// </summary>
        private int OnDocDestroy()
        {
            try
            {
                int cleared = _registry?.Clear() ?? 0;
                Log.Info("AddIn", $"文档关闭：特征注册表已清理（{cleared} 条）");
            }
            catch (Exception ex)
            {
                Log.Warn("AddIn", "文档关闭清理会话状态失败：" + ex.Message);
            }
            return 0;
        }

        /// <summary>显示/隐藏任务面板。</summary>
        public void ToggleTaskPane()
        {
            try
            {
                if (_taskPaneView == null) return;
                if (_paneVisible)
                {
                    _taskPaneView.HideView();
                    _paneVisible = false;
                }
                else
                {
                    _taskPaneView.ShowView();
                    _paneVisible = true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("AddIn", "ToggleTaskPane 失败", ex);
            }
        }

        /// <summary>打开任务面板并切到设置页。</summary>
        public void ShowSettings()
        {
            try
            {
                if (_taskPaneView != null && !_paneVisible)
                {
                    _taskPaneView.ShowView();
                    _paneVisible = true;
                }
                _taskPaneHost?.ShowSettings();
            }
            catch (Exception ex)
            {
                Log.Error("AddIn", "ShowSettings 失败", ex);
            }
        }

        /// <summary>打开日志目录（排障入口）。</summary>
        public void OpenLogsFolder()
        {
            try
            {
                AppPaths.Ensure();
                Process.Start(new ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("AddIn", "打开日志目录失败", ex);
            }
        }

        /// <summary>关于。</summary>
        public void ShowAbout()
        {
            System.Windows.MessageBox.Show(
                "SwAiAssistant — SolidWorks AI 辅助建模助手\n版本 0.2.0（M2）\n\n自然语言建模 · 对话修改与数据问答 · GB 材料 · 强校验闭环 · 快照回滚\n已知限制：草图约束/标注不自动施加，螺纹孔按光孔创建，筋板为实验能力（详见 release-notes）",
                "关于 SwAiAssistant",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        /// <summary>Ribbon「生成 GB 工程图」：展开面板并触发当前零件三视图工程图生成。</summary>
        public void GenerateDrawing()
        {
            try
            {
                if (_taskPaneView != null && !_paneVisible)
                {
                    _taskPaneView.ShowView();
                    _paneVisible = true;
                }
                _taskPaneHost?.GenerateDrawing();
            }
            catch (Exception ex)
            {
                Log.Error("AddIn", "GenerateDrawing 失败", ex);
            }
        }

        // ---- 按钮启用状态回调：返回 1 可用、0 禁用 ----

        public int AlwaysEnabled() => 1;

        /// <summary>仅零件文档环境可用（工程图命令要求活动零件）。</summary>
        public int PartDocEnabled()
        {
            try
            {
                var doc = _swApp?.IActiveDoc2;
                return doc != null &&
                    doc.GetType() == (int)swDocumentTypes_e.swDocPART ? 1 : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
