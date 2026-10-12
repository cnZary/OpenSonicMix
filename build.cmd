@echo off
rem SonicCubePanel 完整编译入口, 参数透传给 build.ps1
rem 用法: build.cmd [-Static] [-SkipTests] [-SkipAot] [-NoClean] [-Configuration Debug]
where pwsh >nul 2>nul
if %ERRORLEVEL%==0 (
    pwsh -NoProfile -File "%~dp0build.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
)
exit /b %ERRORLEVEL%
