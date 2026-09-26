using System;
using System.IO;

namespace SwAiAssistant.Core.Configuration
{
    /// <summary>
    /// 统一的本机数据目录：%AppData%\SwAiAssistant（配置/日志/临时文件）。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>根目录（漫游 ApplicationData，随 Windows 用户配置走）。</summary>
        public static string Root { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SwAiAssistant");

        /// <summary>日志目录。</summary>
        public static string Logs { get; } = Path.Combine(Root, "logs");

        /// <summary>配置目录（模型配置、全局设置；密钥以 DPAPI 密文存放）。</summary>
        public static string Config { get; } = Path.Combine(Root, "config");

        /// <summary>临时目录（截图、导出中间件等，可随时清理）。</summary>
        public static string Temp { get; } = Path.Combine(Root, "temp");

        /// <summary>快照目录（AI 执行前自动快照，一键回滚数据源；M4-T16）。</summary>
        public static string Snapshots { get; } = Path.Combine(Root, "snapshots");

        /// <summary>确保全部目录存在；插件启动早期调用一次。</summary>
        public static void Ensure()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Logs);
            Directory.CreateDirectory(Config);
            Directory.CreateDirectory(Temp);
            Directory.CreateDirectory(Snapshots);
        }
    }
}
