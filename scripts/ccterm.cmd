@echo off
rem ---------------------------------------------------------------------------
rem ccterm - open my-term-ts (Claude Code terminal) in the current folder.
rem
rem   ccterm            start claude in this folder
rem   ccterm --shell    start a plain shell instead
rem
rem The app uses its startup working directory, so pass the APP path explicitly
rem and let the current directory carry through untouched.
rem
rem APP defaults to this repository (one level above scripts\). If you copy this
rem file somewhere on your PATH - next to claude.exe, for example - replace it
rem with the absolute path of the repository.
rem
rem NOTE: keep this file pure ASCII. cmd.exe reads .cmd in the OEM codepage
rem (932 on a Japanese Windows), and UTF-8 comments would corrupt later lines.
rem ---------------------------------------------------------------------------
setlocal

set "APP=%~dp0.."
set "ELECTRON=%APP%\node_modules\electron\dist\electron.exe"

if not exist "%ELECTRON%" goto :no_electron
if not exist "%APP%\dist\main\main.js" goto :no_build

rem Launch electron.exe directly and detach with start, so no console lingers.
start "" "%ELECTRON%" "%APP%" %*
exit /b 0

:no_electron
echo [ccterm] electron not found: "%ELECTRON%"
echo [ccterm] run: cd /d "%APP%"  then  npm install
exit /b 1

:no_build
echo [ccterm] build output not found: "%APP%\dist\main\main.js"
echo [ccterm] run: cd /d "%APP%"  then  npm run build
exit /b 1
