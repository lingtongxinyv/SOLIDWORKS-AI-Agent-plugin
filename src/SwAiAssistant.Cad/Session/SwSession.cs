using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SwAiAssistant.Core.Logging;
using SwAiAssistant.Core.Threading;
using SolidWorks.Interop.sldworks;

namespace SwAiAssistant.Cad.Session
{
    /// <summary>
    /// SolidWorks 会话：连接已运行实例或后台启动新实例、版本探测、
    /// 零件模板自动发现与进程级清理。
    /// 线程约束：本类所有公开方法内部均封送到 StaExecutor 的 STA 线程，调用方可在任意线程使用。
    /// COM API 签名经 SW2026 redist（api\redist）反射实测；仅使用 2025/2026 长期稳定成员。
    /// </summary>
    public sealed class SwSession : IDisposable
    {
        /// <summary>swUserPreferenceStringValue_e.swDefaultTemplatePart（默认零件模板首选项）。</summary>
        private const int SwDefaultTemplatePart = 8;

        private static readonly string[] StartProgIds =
        {
            "SldWorks.Application.34", // SW2026
            "SldWorks.Application.33", // SW2025
            "SldWorks.Application"     // 版本无关（最后注册版本）
        };

        private readonly StaExecutor _sta;
        private readonly bool _ownedByUs;
        private readonly int _ownedProcessId;
        private ISldWorks _app;
        private bool _disposed;

        private SwSession(StaExecutor sta, ISldWorks app, bool ownedByUs, int ownedProcessId)
        {
            _sta = sta ?? throw new ArgumentNullException(nameof(sta));
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _ownedByUs = ownedByUs;
            _ownedProcessId = ownedProcessId;
        }

        /// <summary>在 STA 线程上执行工作；已在线程上时内联执行。</summary>
        public T OnSta<T>(Func<T> work) => _sta.Run(work);

        /// <summary>
        /// 插件内使用：包装 SolidWorks 经 ConnectToSW 传入的既有实例。
        /// 该实例生命周期归 SolidWorks，Dispose 只释放 RCW，绝不退出进程。
        /// </summary>
        public static SwSession FromHostedInstance(StaExecutor sta, ISldWorks hostedApp)
        {
            var session = new SwSession(sta, hostedApp, ownedByUs: false, ownedProcessId: 0);
            Log.Info("Cad", "SwSession：已接管 SolidWorks 宿主实例（Revision=" + session.Revision + "）");
            return session;
        }

        /// <summary>
        /// 独立进程（集成测试台）使用：优先接管已运行实例；没有则按 ProgID 链启动新实例。
        /// </summary>
        /// <param name="startVisible">新实例是否显示主窗口（后台测试传 false）。</param>
        /// <param name="startedNew">输出：是否由本进程启动了新 SolidWorks。</param>
        public static SwSession ConnectOrStart(StaExecutor sta, bool startVisible, out bool startedNew)
        {
            if (sta == null) throw new ArgumentNullException(nameof(sta));

            bool started = false;
            var session = sta.Run(() =>
            {
                // 1) 已运行实例：ROT 接管。
                //    竞态防护：SW 进程可能正在启动尚未注册 ROT（此时 CreateInstance 会
                //    "接管"该实例却被误判为自有实例，Dispose 时 ExitApp 误关用户 SW）——
                //    先重试 ROT 最多 60 秒，确认真的没有活动实例再启动。
                var rotDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (true)
                {
                    try
                    {
                        object running = Marshal.GetActiveObject("SldWorks.Application");
                        if (running is ISldWorks runningApp)
                        {
                            Log.Info("Cad", "SwSession：ROT 接管已运行的 SolidWorks");
                            return new SwSession(sta, runningApp, false, 0);
                        }
                    }
                    catch (COMException)
                    {
                        // ROT 中没有实例
                    }

                    bool swProcessExists = GetSwProcessIds().Length > 0;
                    if (!swProcessExists || DateTime.UtcNow >= rotDeadline)
                    {
                        break;
                    }
                    Log.Info("Cad", "SwSession：检测到 SLDWORKS.exe 正在启动，等待其注册 ROT…");
                    Thread.Sleep(2000);
                }

                // 2) 启动新实例
                var beforePids = GetSwProcessIds();
                ISldWorks app = null;
                Exception lastError = null;
                foreach (var progId in StartProgIds)
                {
                    try
                    {
                        Type t = Type.GetTypeFromProgID(progId);
                        if (t == null) continue;
                        app = Activator.CreateInstance(t) as ISldWorks;
                        if (app != null)
                        {
                            Log.Info("Cad", $"SwSession：经 {progId} 启动新实例");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }
                if (app == null)
                {
                    throw new CadException("启动 SolidWorks 失败：未找到可用的 SldWorks.Application ProgID（2025/2026）。", lastError);
                }

                // 等 SW 完成初始化（大进程冷启动可达数十秒）
                WaitReady(app, TimeSpan.FromSeconds(90));

                try
                {
                    app.UserControl = false;
                    app.Visible = startVisible;
                }
                catch (Exception ex)
                {
                    Log.Warn("Cad", "设置 Visible/UserControl 失败（继续）：" + ex.Message);
                }

                started = true;
                int newPid = GetSwProcessIds().Except(beforePids).FirstOrDefault();
                return new SwSession(sta, app, true, newPid);
            });

            startedNew = started;
            return session;
        }

        /// <summary>SolidWorks 内部版本号（如 "34.0.0" = SW2026）。</summary>
        public string Revision => OnSta(() =>
        {
            try { return Convert.ToString(_app.RevisionNumber()); }
            catch (Exception ex) { throw CadException.FromCom("读取 SolidWorks 版本", ex); }
        });

        /// <summary>原始 COM 对象：仅限 STA 线程内触碰（优先使用本类封装方法）。</summary>
        public ISldWorks App => _app;

        /// <summary>是否由本进程启动了该实例（决定 Dispose 是否负责退出）。</summary>
        public bool OwnedByUs => _ownedByUs;

        /// <summary>自有实例的 SLDWORKS.exe 进程 ID（接管模式为 0；M4-T19 孤儿清理标记用）。</summary>
        public int OwnedProcessId => _ownedProcessId;

        /// <summary>当前活动文档；无文档时返回 null。</summary>
        public IModelDoc2 GetActiveDocument()
        {
            return OnSta(() =>
            {
                try { return _app.IActiveDoc2; }
                catch (Exception ex) { throw CadException.FromCom("获取活动文档", ex); }
            });
        }

        /// <summary>枚举当前打开的全部文档。</summary>
        public IModelDoc2[] GetDocuments()
        {
            return OnSta(() =>
            {
                try
                {
                    object docs = _app.GetDocuments();
                    if (docs is object[] arr)
                    {
                        return arr.OfType<IModelDoc2>().ToArray();
                    }
                    return new IModelDoc2[0];
                }
                catch (Exception ex) { throw CadException.FromCom("枚举文档", ex); }
            });
        }

        /// <summary>用自动发现的模板新建零件文档；新文档成为活动文档。</summary>
        public IModelDoc2 NewPartDocument()
        {
            return OnSta(() =>
            {
                string template = FindPartTemplate();
                try
                {
                    object doc = _app.NewDocument(template, 0, 0, 0);
                    if (doc is IModelDoc2 model)
                    {
                        Log.Info("Cad", "已新建零件文档：" + SafeTitle(model));
                        return model;
                    }
                    throw new CadException("新建零件失败：SolidWorks NewDocument 返回空（模板可能损坏：" + template + "）。");
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom("新建零件文档", ex); }
            });
        }

        /// <summary>
        /// 零件模板自动发现：
        /// 1) SW 首选项默认零件模板（存在性校验）；
        /// 2) ProgramData 下按版本年目录回退（当年 → 前一年）；
        /// 3) 全部失败抛中文 CadException。
        /// </summary>
        public string FindPartTemplate()
        {
            return OnSta(() =>
            {
                try
                {
                    string preferred = _app.GetUserPreferenceStringValue(SwDefaultTemplatePart);
                    if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred))
                    {
                        return preferred;
                    }

                    int year = RevisionYear();
                    foreach (int y in new[] { year, year - 1 })
                    {
                        string dir = Path.Combine(
                            System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
                            "SOLIDWORKS", $"SOLIDWORKS {y}", "templates");
                        if (!Directory.Exists(dir)) continue;
                        string canonical = Path.Combine(dir, "Part.prtdot");
                        if (File.Exists(canonical)) return canonical;
                        string any = Directory.GetFiles(dir, "*.prtdot").FirstOrDefault();
                        if (any != null) return any;
                    }
                }
                catch (Exception ex) when (!(ex is CadException))
                {
                    throw CadException.FromCom("查找零件模板", ex);
                }
                throw new CadException("找不到零件模板（.prtdot）：请在 SolidWorks「选项 → 默认模板」中设置零件模板后重试。");
            });
        }

        /// <summary>版本号换算版本年：SW 内部主版本 34 → 2026（1992 + 主版本）。</summary>
        private int RevisionYear()
        {
            try
            {
                string rev = Revision;
                int major = int.Parse(rev.Split('.')[0]);
                return 1992 + major;
            }
            catch
            {
                return DateTime.Now.Year;
            }
        }

        private static void WaitReady(ISldWorks app, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    _ = app.RevisionNumber();
                    return;
                }
                catch (COMException)
                {
                    Thread.Sleep(1000);
                }
            }
            throw new CadException("SolidWorks 启动后 90 秒内未就绪，请检查 SolidWorks 是否能正常启动。");
        }

        private static int[] GetSwProcessIds()
        {
            try
            {
                return Process.GetProcessesByName("SLDWORKS").Select(p => p.Id).ToArray();
            }
            catch
            {
                return new int[0];
            }
        }

        private static string SafeTitle(IModelDoc2 doc)
        {
            try { return doc.GetTitle(); } catch { return "<未知>"; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _sta.Run(() =>
            {
                if (_ownedByUs && _app != null)
                {
                    try
                    {
                        Log.Info("Cad", "SwSession：退出由本进程启动的 SolidWorks");
                        _app.ExitApp();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Cad", "ExitApp 调用失败：" + ex.Message);
                    }

                    if (_ownedProcessId > 0)
                    {
                        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                        while (DateTime.UtcNow < deadline)
                        {
                            try
                            {
                                var p = Process.GetProcessById(_ownedProcessId);
                                if (p.HasExited) break;
                            }
                            catch (ArgumentException)
                            {
                                break; // 进程已不存在
                            }
                            Thread.Sleep(500);
                        }
                    }
                }

                if (_app != null)
                {
                    try { Marshal.FinalReleaseComObject(_app); }
                    catch { /* 忽略 */ }
                    _app = null;
                }
            });
        }
    }
}
