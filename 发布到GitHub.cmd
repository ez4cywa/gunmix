@echo off
rem ============================================================
rem  Publish to GitHub.  Run this from the app folder.
rem
rem  One-time prerequisite (must be done by you, interactively):
rem      gh auth login -h github.com
rem      pick HTTPS -> Login with a web browser
rem
rem  Then:
rem      publish_to_github.cmd  [repo-name]
rem
rem  repo-name defaults to "gunmix". Creates a PUBLIC repository.
rem  To make it private, change --public to --private below.
rem ============================================================
setlocal
set REPO=%~1
if "%REPO%"=="" set REPO=gunmix
set DESC=Windows desktop tool for layering gunshot sounds and assembling animation audio

echo [1/3] Checking GitHub authentication...
gh auth status >nul 2>&1
if errorlevel 1 (
    echo.
    echo   Not logged in. Please run first:
    echo       gh auth login -h github.com
    echo.
    pause
    exit /b 1
)

echo [2/3] Creating repo %REPO% and pushing...
gh repo create %REPO% --public --source . --remote origin --push --description "%DESC%"
if errorlevel 1 (
    echo.
    echo   Create/push failed. If the name is taken, try another one:
    echo       publish_to_github.cmd  some-other-name
    echo.
    pause
    exit /b 1
)

echo [3/3] Done.
gh repo view --web
endlocal
