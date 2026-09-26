@echo off
setlocal EnableExtensions EnableDelayedExpansion
title SwAiAssistant 安装

echo ============================================
echo   SwAiAssistant - SolidWorks AI 助手 安装
echo ============================================
echo.

rem ---- 管理员权限自检 ----
net session >nul 2>&1
if errorlevel 1 (
    echo [错误] 安装需要管理员权限。
    echo        请右键点击本脚本，选择“以管理员身份运行”。
    echo.
    pause
    exit /b 1
)

rem ---- 定位 64 位 RegAsm（.NET Framework 4.x）----
set "REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
if not exist "%REGASM%" (
    echo [错误] 未找到 64 位 RegAsm：%REGASM%
    echo        请确认本机已安装 .NET Framework 4.8（Windows 10/11 自带）。
    pause
    exit /b 1
)

set "PLUGIN_DIR=%~dp0"
set "DLL=%PLUGIN_DIR%SwAiAssistant.AddIn.dll"
if not exist "%DLL%" (
    echo [错误] 未找到 %DLL%
    echo        请确认安装脚本与插件文件在同一目录后重试。
    pause
    exit /b 1
)

echo 插件目录：%PLUGIN_DIR%
echo 正在注册 COM 组件与 SolidWorks Add-in...
echo.

rem /codebase 在注册表记录 DLL 路径，无需放入 GAC；
rem 插件内的 ComRegisterFunction 会同时写入 SolidWorks Addins 启动项。
"%REGASM%" "%DLL%" /codebase
if errorlevel 1 (
    echo.
    echo [错误] 注册失败，请把上方完整输出与日志反馈。
    echo        日志目录：%%AppData%%\SwAiAssistant\logs
    pause
    exit /b 1
)

echo.
echo ============================================
echo   安装完成！
echo   请启动 SolidWorks 2025/2026：
echo   - 功能区将出现“AI 助手”标签页
echo   - 右侧任务面板自动打开
echo   - 配置与日志：%%AppData%%\SwAiAssistant\
echo ============================================
echo.
pause
endlocal
