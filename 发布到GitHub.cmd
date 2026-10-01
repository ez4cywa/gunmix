@echo off
rem ============================================================
rem  一键发布到 GitHub（在 app 目录下运行）
rem
rem  前提：先完成 GitHub 登录（这一步需要你本人操作，我无法代做）：
rem      gh auth login -h github.com
rem      → 选 HTTPS → 选 Login with a web browser → 按提示在浏览器确认
rem
rem  然后运行本脚本：
rem      发布到GitHub.cmd  [仓库名]
rem
rem  仓库名默认 gunmix；默认创建为 PUBLIC 公开仓库。
rem  想改成私有，把下面 gh repo create 那行的 --public 换成 --private。
rem ============================================================
setlocal
set REPO=%~1
if "%REPO%"=="" set REPO=gunmix

set GIT="C:\Program Files\Git\cmd\git.exe"
set GH="C:\Program Files\GitHub CLI\gh.exe"

echo [1/3] 检查 GitHub 登录状态...
%GH% auth status 2>&1 | findstr /C:"Failed to log in" >nul
if not errorlevel 1 (
    echo.
    echo   未登录或登录已失效。请先运行：
    echo       gh auth login -h github.com
    echo   完成后重新运行本脚本。
    echo.
    pause
    exit /b 1
)

echo [2/3] 创建远程仓库 %REPO% 并推送...
%GH% repo create %REPO% --public --source . --remote origin --push --description "Windows 桌面枪声分层与动画音效装配工具"
if errorlevel 1 (
    echo.
    echo   创建/推送失败。若仓库名已存在，改用：
    echo       发布到GitHub.cmd  其他仓库名
    echo.
    pause
    exit /b 1
)

echo [3/3] 完成
%GH% repo view --web
endlocal
