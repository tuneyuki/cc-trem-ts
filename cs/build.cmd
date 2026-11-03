@echo off
rem ---------------------------------------------------------------------------
rem build.cmd - build ccterm-cs.exe with the csc.exe that ships with Windows
rem             (.NET Framework 4.x, C# 5). No SDK, no NuGet.
rem
rem   build.cmd          -> bin\ccterm-cs.exe  (+ bin\assets\CascadiaMono-Regular.ttf)
rem
rem NOTE: keep this file pure ASCII. cmd.exe reads .cmd in the OEM codepage
rem (932 on a Japanese Windows), and UTF-8 text would corrupt later lines.
rem The C# sources are UTF-8, hence /codepage:65001 below.
rem ---------------------------------------------------------------------------
setlocal

set "ROOT=%~dp0"
set "OUT=%ROOT%bin"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto :no_csc

if not exist "%OUT%\assets" mkdir "%OUT%\assets"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /utf8output ^
  /out:"%OUT%\ccterm-cs.exe" /win32manifest:"%ROOT%app.manifest" ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  "%ROOT%src\*.cs"
if errorlevel 1 goto :failed

copy /y "%ROOT%..\assets\CascadiaMono-Regular.ttf" "%OUT%\assets\" >nul
if errorlevel 1 goto :failed

echo [build] ok: "%OUT%\ccterm-cs.exe"
exit /b 0

:no_csc
echo [build] csc.exe not found under %WINDIR%\Microsoft.NET\Framework*\v4.0.30319
echo [build] .NET Framework 4.x is required (it is part of Windows 10/11).
exit /b 1

:failed
echo [build] failed
exit /b 1
