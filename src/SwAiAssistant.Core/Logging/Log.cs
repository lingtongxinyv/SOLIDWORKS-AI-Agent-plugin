using System;
using System.Globalization;
using System.IO;
using System.Text;
using SwAiAssistant.Core.Configuration;

namespace SwAiAssistant.Core.Logging
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    /// <summary>
    /// 全局文件日志。按日切分（app-yyyyMMdd.log），线程安全，自动 flush。
    /// 关键链路（模型调用/特征树/COM 调用/回读值/校验结果）统一经此记录。
    /// </summary>
    public static class Log
    {
        private static readonly object SyncRoot = new object();
        private static LogLevel _minLevel = LogLevel.Debug;
        private static string _currentFile;

        /// <summary>最低输出级别（可由配置覆盖）。</summary>
        public static LogLevel MinimumLevel
        {
            get => _minLevel;
            set => _minLevel = value;
        }

        /// <summary>测试挂钩：非空时代替 %AppData% 日志目录（生产代码勿设）。</summary>
        public static string DirectoryOverride { get; set; }

        public static void Debug(string message) => Write(LogLevel.Debug, null, message);
        public static void Info(string message) => Write(LogLevel.Info, null, message);
        public static void Warn(string message) => Write(LogLevel.Warn, null, message);
        public static void Error(string message) => Write(LogLevel.Error, null, message);
        public static void Error(string message, Exception ex) => Write(LogLevel.Error, null, message, ex);

        public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
        public static void Info(string category, string message) => Write(LogLevel.Info, category, message);
        public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
        public static void Error(string category, string message) => Write(LogLevel.Error, category, message);
        public static void Error(string category, string message, Exception ex) => Write(LogLevel.Error, category, message, ex);

        private static void Write(LogLevel level, string category, string message, Exception ex = null)
        {
            if (level < _minLevel)
            {
                return;
            }

            try
            {
                string dir = DirectoryOverride;
                if (string.IsNullOrEmpty(dir))
                {
                    AppPaths.Ensure();
                    dir = AppPaths.Logs;
                }
                else
                {
                    Directory.CreateDirectory(dir);
                }
                string file = Path.Combine(
                    dir,
                    "app-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                sb.Append(" [").Append(level.ToString().ToUpperInvariant()).Append(']');
                sb.Append(" [T").Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append(']');
                if (!string.IsNullOrEmpty(category))
                {
                    sb.Append(" [").Append(category).Append(']');
                }
                sb.Append(' ').Append(message);
                if (ex != null)
                {
                    sb.AppendLine();
                    sbAppendException(sb, ex);
                }

                lock (SyncRoot)
                {
                    File.AppendAllText(file, sb.ToString() + Environment.NewLine, Encoding.UTF8);
                    _currentFile = file;
                }
            }
            catch
            {
                // 日志失败绝不能影响主流程。
            }
        }

        private static void sbAppendException(StringBuilder sb, Exception ex)
        {
            sb.Append("    -> ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.AppendLine().Append(ex.StackTrace);
            }
            if (ex.InnerException != null)
            {
                sb.AppendLine();
                sbAppendException(sb, ex.InnerException);
            }
        }

        /// <summary>当前日志文件完整路径（供 UI"打开日志目录"使用）。</summary>
        public static string CurrentFile => _currentFile;
    }
}
