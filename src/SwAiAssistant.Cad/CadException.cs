using System;
using System.Runtime.InteropServices;
using SwAiAssistant.Core.Diagnostics;

namespace SwAiAssistant.Cad
{
    /// <summary>
    /// Cad 层统一异常：把 SolidWorks COM 错误翻译为可操作的中文信息。
    /// 原始异常始终保留在 InnerException 中供日志排查。
    /// </summary>
    public sealed class CadException : Exception
    {
        public CadException(string message) : base(message)
        {
        }

        public CadException(string message, Exception inner) : base(message, inner)
        {
        }

        /// <summary>把 COM/CLR 异常包装为中文 CadException（M4-T19：经 ErrorText 中文化映射）。</summary>
        public static CadException FromCom(string operation, Exception ex)
        {
            if (ex is CadException ce) return ce;
            if (ex is COMException com)
            {
                return new CadException($"{operation}失败：{ErrorText.FriendlyHResult(com.HResult, com.Message)}", ex);
            }
            return new CadException($"{operation}失败：{ErrorText.Friendly(ex)}", ex);
        }
    }
}
