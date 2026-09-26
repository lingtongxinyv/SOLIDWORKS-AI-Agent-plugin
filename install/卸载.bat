@echo off
setlocal EnableExtensions
title SwAiAssistant 卸载

echo ============================================
echo   SwAiAssistant - SolidWorks AI 助手 卸载
echo ============================================
echo.

net session >nul 2>&1
if errorlevel 1 (
    echo [错误] 卸载需要管理员权限。
    echo        请右键点击本脚本，选择“以管理员身份运行”。
    pause
    exit /b 1
)

set "REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
set "PLUGIN_DIR=%~dp0"
set "DLL=%PLUGIN_DIR%SwAiAssistant.AddIn.dll"

if not exist "%DLL%" (
    echo [错误] 未找到 %DLL%
    pause
    exit /b 1
)

echo 正在注销 COM 组件与 SolidWorks Add-in...
"%REGASM%" "%DLL%" /u
if errorlevel 1 (
    echo.
    echo [警告] RegAsm 注销报告错误，请检查上方输出。
)

echo.
echo ============================================
echo   卸载完成。重启 SolidWorks 后“AI 助手”标签页将消失。
echo.
echo   说明：%%AppData%%\SwAiAssistant\（模型配置、API 密钥、
echo   日志）未被删除；如需彻底清除请手动删除该目录。
echo ============================================
echo.
pause
endlocal
