using System;
using SwAiAssistant.Core.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAiAssistant.AddIn.Ribbon
{
    /// <summary>
    /// 创建 SolidWorks CommandManager 命令组「AI 助手」与功能区选项卡。
    /// 命令回调经 SwAddIn 上的 public 方法名反射触发。
    /// 基于 SW2026 Interop 实测签名编写（CreateCommandGroup2 7 参、
    /// AddCommandTab(DocumentType, TabName)、Small/LargeMainIcon 字符串）。
    /// </summary>
    internal sealed class RibbonController : IDisposable
    {
        private readonly ISldWorks _swApp;
        private readonly int _cookie;
        private ICommandManager _commandManager;
        private ICommandGroup _commandGroup;

        // 命令组与命令的固定 ID（IgnorePreviousVersion=true 保证重装幂等）
        private const int GroupId = 9101;
        private const int CmdTogglePane = 1;
        private const int CmdSettings = 2;
        private const int CmdDrawing = 3;
        private const int CmdLogs = 4;
        private const int CmdAbout = 5;

        public RibbonController(ISldWorks swApp, int cookie)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _cookie = cookie;
        }

        public void Create()
        {
            _commandManager = _swApp.GetCommandManager(_cookie);
            if (_commandManager == null)
            {
                throw new InvalidOperationException("GetCommandManager 返回 null。");
            }

            int errors = 0;
            _commandGroup = _commandManager.CreateCommandGroup2(
                GroupId,
                "AI 助手",
                "AI 助手",
                "SwAiAssistant：自然语言辅助建模、校验与问答",
                0,            // Position
                true,         // IgnorePreviousVersion
                ref errors);

            // 注意：swCreateCommandGroupErrors 取值与直觉相反——1=成功，0=失败，2=ID超出工具栏范围
            if (_commandGroup == null || errors != (int)swCreateCommandGroupErrors.swCreateCommandGroup_Success)
            {
                throw new InvalidOperationException($"CreateCommandGroup2 失败，errors={errors}");
            }

            // 图标：运行时生成的多尺寸 PNG，列表以换行分隔；MainIcon 为单文件路径
            try
            {
                _commandGroup.SmallIconList = Hosting.IconFactory.GetIconList(new[] { 16, 24 });
                _commandGroup.LargeIconList = Hosting.IconFactory.GetIconList(new[] { 32, 40, 64 });
                _commandGroup.SmallMainIcon = Hosting.IconFactory.GetOrCreate(24);
                _commandGroup.LargeMainIcon = Hosting.IconFactory.GetOrCreate(32);
            }
            catch (Exception ex)
            {
                // 图标不影响命令注册本身
                Log.Warn("Ribbon", "设置命令组图标失败（继续无图标注册）：" + ex.Message);
            }

            int itemType =
                (int)swCommandItemType_e.swMenuItem |
                (int)swCommandItemType_e.swToolbarItem;

            _commandGroup.AddCommandItem2(
                "显示/隐藏 AI 面板", 0,
                "显示或隐藏 AI 助手面板",
                "在 SolidWorks 右侧任务窗格显示或隐藏 AI 助手面板",
                0, "ToggleTaskPane", "AlwaysEnabled",
                GroupId + CmdTogglePane, itemType);

            _commandGroup.AddCommandItem2(
                "AI 设置", 1,
                "AI 模型与插件设置",
                "配置云端/Ollama 模型、能力探测与执行模式",
                0, "ShowSettings", "AlwaysEnabled",
                GroupId + CmdSettings, itemType);

            _commandGroup.AddCommandItem2(
                "生成 GB 工程图", 2,
                "由当前零件生成 GB 第一角三视图工程图",
                "一键生成三视图、自动插入模型尺寸并导出 PDF（.slddrw 保存到插件临时目录）",
                0, "GenerateDrawing", "PartDocEnabled",
                GroupId + CmdDrawing, itemType);

            _commandGroup.AddCommandItem2(
                "打开日志目录", 3,
                "打开插件日志目录",
                "排障入口：打开 %AppData%\\SwAiAssistant\\logs",
                0, "OpenLogsFolder", "AlwaysEnabled",
                GroupId + CmdLogs, itemType);

            _commandGroup.AddCommandItem2(
                "关于", 4,
                "关于 SwAiAssistant",
                "查看插件版本与介绍",
                0, "ShowAbout", "AlwaysEnabled",
                GroupId + CmdAbout, itemType);

            _commandGroup.HasMenu = true;
            _commandGroup.HasToolbar = true;
            _commandGroup.Activate();

            // 功能区选项卡按文档类型注册（零件/装配/工程图环境均可见）
            EnsureTabsVisible();

            Log.Info("Ribbon", "命令组「AI 助手」注册完成");
        }

        /// <summary>
        /// 确保三类文档环境的功能区选项卡存在且可见（幂等）。
        /// 已知问题：SW 启动初期（无文档）创建的选项卡可能不出现在标签条——
        /// SwAddIn 在首次文档激活事件时再次调用本方法修复。
        /// </summary>
        public void EnsureTabsVisible()
        {
            if (_commandManager == null) return;
            CreateRibbonTab(swDocumentTypes_e.swDocPART);
            CreateRibbonTab(swDocumentTypes_e.swDocASSEMBLY);
            CreateRibbonTab(swDocumentTypes_e.swDocDRAWING);
        }

        private void CreateRibbonTab(swDocumentTypes_e docType)
        {
            try
            {
                ICommandTab tab = _commandManager.AddCommandTab((int)docType, "AI 助手");
                if (tab == null)
                {
                    Log.Warn("Ribbon", $"AddCommandTab({docType}) 返回 null。");
                    return;
                }

                ICommandTabBox box = tab.AddCommandTabBox();
                int[] ids =
                {
                    GroupId + CmdTogglePane,
                    GroupId + CmdSettings,
                    GroupId + CmdDrawing,
                    GroupId + CmdLogs,
                    GroupId + CmdAbout
                };
                int[] styles = new int[ids.Length];
                for (int i = 0; i < styles.Length; i++)
                {
                    styles[i] = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextHorizontal;
                }
                box.AddCommands(ids, styles);

                // 关键坑位：SW2018+ 经 API 新建的 CommandTab 默认 Visible=false，
                // 不在标签条显示（需右键手工勾选）——必须显式置为可见。
                tab.Visible = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Ribbon", $"为 {docType} 创建功能区选项卡失败（不影响插件加载）：" + ex.Message);
            }
        }

        public void Dispose()
        {
            try
            {
                // RuntimeOnly=true：仅移除本次运行时注册的组，注册表数据保留
                _commandManager?.RemoveCommandGroup2(GroupId, true);
            }
            catch (Exception ex)
            {
                Log.Warn("Ribbon", "移除命令组失败：" + ex.Message);
            }
            _commandGroup = null;
            _commandManager = null;
        }
    }
}
