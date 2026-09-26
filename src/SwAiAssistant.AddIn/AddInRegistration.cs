using System;
using Microsoft.Win32;
using SwAiAssistant.Core.Logging;

namespace SwAiAssistant.AddIn
{
    /// <summary>
    /// SolidWorks 第二层注册的实际逻辑（HKLM\SOFTWARE\SolidWorks\Addins\{CLSID}）。
    /// 注意：RegAsm 只会调用「被注册类自身」标注了 ComRegisterFunction 的静态方法，
    /// 因此本类只提供辅助实现，由 SwAddIn 内的标注方法转发调用。
    /// 写 HKLM 需要管理员权限，由安装.bat 提权后调用。
    /// </summary>
    internal static class AddInRegistration
    {
        private const string AddinsKeyPath = @"SOFTWARE\SolidWorks\Addins";

        public static void RegisterAddInKey(Type type)
        {
            try
            {
                string keyPath = $@"{AddinsKeyPath}\{type.GUID:B}";
                using (var key = Registry.LocalMachine.CreateSubKey(keyPath))
                {
                    if (key != null)
                    {
                        // 默认值 1 = SolidWorks 启动时自动加载此插件
                        key.SetValue(null, 1, RegistryValueKind.DWord);
                        key.SetValue("Title", "AI 建模助手");
                        key.SetValue("Description", "SwAiAssistant — SolidWorks AI 辅助建模助手（自然语言建模 / 校验 / 问答）");
                    }
                }
                Log.Info("AddIn", $"已写入 SolidWorks Addins 注册表项：{keyPath}");
            }
            catch (Exception ex)
            {
                Log.Error("AddIn", "写入 SolidWorks Addins 注册表项失败（请确认以管理员身份运行安装脚本）", ex);
                throw;
            }
        }

        public static void UnregisterAddInKey(Type type)
        {
            try
            {
                string keyPath = $@"{AddinsKeyPath}\{type.GUID:B}";
                Registry.LocalMachine.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
                Log.Info("AddIn", $"已删除 SolidWorks Addins 注册表项：{keyPath}");
            }
            catch (Exception ex)
            {
                Log.Error("AddIn", "删除 SolidWorks Addins 注册表项失败", ex);
            }
        }
    }
}
