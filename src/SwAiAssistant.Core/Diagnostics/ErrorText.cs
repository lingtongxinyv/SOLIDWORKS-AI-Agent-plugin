using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SwAiAssistant.Core.Diagnostics
{
    /// <summary>
    /// 异常中文化映射表（M4-T19）：把常见 COM/网络/CLR 异常翻译为可操作的中文提示。
    /// 映射不上的异常原样返回原始 Message（日志始终记录原始异常，此处只服务界面展示）。
    /// </summary>
    public static class ErrorText
    {
        /// <summary>
        /// 返回面向用户的中文错误说明。已知错误给「原因 + 建议」，未知错误原样返回。
        /// </summary>
        public static string Friendly(Exception ex)
        {
            if (ex == null) return "未知错误。";
            // 已中文化的业务异常直接透传（CadException/LlmException 构造时已是中文）
            string msg = ex.Message ?? "";
            switch (ex)
            {
                case OperationCanceledException _:
                    return "操作已取消。";
                case TimeoutException _:
                    return "操作超时。请检查 SolidWorks 是否繁忙，或稍后重试。";
                case COMException com:
                    return FriendlyHResult(com.HResult, msg);
                case HttpRequestException _:
                    return "网络请求失败：" + msg + "。请检查网络连接与模型服务地址（云端 API 或本机 Ollama）。";
                case UnauthorizedAccessException _:
                    return "访问被拒绝：" + msg + "。请尝试以管理员身份运行，或检查文件/目录权限。";
                case FileNotFoundException fnf:
                    return "文件未找到：" + (fnf.FileName ?? msg) + "。";
                case DirectoryNotFoundException _:
                    return "目录不存在：" + msg + "。";
                case IOException _:
                    return "文件读写失败：" + msg + "。文件可能被其他程序占用。";
                case InvalidOperationException _:
                    return msg;
                default:
                    return msg.Length > 0 ? msg : (ex.GetType().Name + "（无错误信息）");
            }
        }

        /// <summary>COM HRESULT → 中文原因与建议；未知返回 null。</summary>
        public static string FriendlyHResult(int hr, string rawMessage)
        {
            string hint;
            switch (unchecked((uint)hr))
            {
                case 0x80004005: // E_FAIL
                    hint = "SolidWorks 内部拒绝了该操作（E_FAIL）。常见于：当前选择状态不对、特征不满足建模条件、或文档处于异常状态。可尝试重建模型（Ctrl+Q）后重试。";
                    break;
                case 0x8001010A: // RPC_E_SERVERCALL_RETRYLATER
                    hint = "SolidWorks 正忙，暂时无法响应。请稍候重试（避免在 SW 弹出对话框时执行操作）。";
                    break;
                case 0x80010105: // RPC_E_SERVERFAULT
                    hint = "SolidWorks 执行该操作时内部出错。可尝试重建模型或重启 SolidWorks。";
                    break;
                case 0x80010108: // RPC_E_DISCONNECTED
                    hint = "与 SolidWorks 的连接已断开（SolidWorks 可能已关闭或崩溃）。请重启 SolidWorks 后重试。";
                    break;
                case 0x800706BA: // RPC_S_SERVER_UNAVAILABLE
                    hint = "SolidWorks 进程已不可用。请重启 SolidWorks 后重试。";
                    break;
                case 0x80040154: // REGDB_E_CLASSNOTREG
                    hint = "SolidWorks COM 组件未注册。请修复 SolidWorks 安装或以管理员身份重注册。";
                    break;
                case 0x80070005: // E_ACCESSDENIED
                    hint = "权限不足。请确认 SolidWorks 与插件以相同权限级别运行（不要一侧管理员一侧普通用户）。";
                    break;
                case 0x80070057: // E_INVALIDARG
                    hint = "传递给 SolidWorks 的参数无效。请检查数值是否在合理范围（如尺寸为正数）。";
                    break;
                case 0x8000FFFF: // E_UNEXPECTED
                    hint = "SolidWorks 发生未预期错误。可尝试重建模型（Ctrl+Q）或重启 SolidWorks。";
                    break;
                default:
                    return "SolidWorks COM 错误 0x" + unchecked((uint)hr).ToString("X8") +
                        (string.IsNullOrEmpty(rawMessage) ? "。" : "（" + rawMessage + "）。");
            }
            return hint + "（0x" + unchecked((uint)hr).ToString("X8") + "）";
        }

        /// <summary>把一段可能含英文技术细节的 message 与异常合并为单行中文展示文本。</summary>
        public static string Friendly(string operation, Exception ex)
        {
            if (ex == null) return operation + "失败。";
            // CadException 等已中文化：直接拼接操作名（避免双重「失败：」）
            string body = Friendly(ex);
            if (body.Contains("失败") || body.Contains("取消") || body.Contains("超时") || body.Contains("拒绝"))
            {
                return body;
            }
            return operation + "失败：" + body;
        }
    }
}
