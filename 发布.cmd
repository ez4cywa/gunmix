@echo off
rem 枪声分层工作台 - 构建 / 测试 / 发布
rem 需要 .NET 10 SDK（dotnet --list-sdks 查看）

pushd "%~dp0"

if "%~1"=="test" goto :test
if "%~1"=="debug" goto :debug

echo [1/2] 构建自包含发布包 → publish-v0.3.3\
dotnet publish src/GunMix.App/GunMix.App.csproj -c Release -r win-x64 --self-contained true -o publish-v0.3.3
if errorlevel 1 goto :fail

echo [2/2] 运行测试
dotnet test tests/GunMix.Core.Tests/GunMix.Core.Tests.csproj
if errorlevel 1 goto :fail

echo 完成：publish-v0.3.3\GunMix.App.exe
popd
exit /b 0

:test
dotnet test tests/GunMix.Core.Tests/GunMix.Core.Tests.csproj
popd
exit /b %errorlevel%

:debug
dotnet build src/GunMix.App/GunMix.App.csproj -c Debug
popd
exit /b %errorlevel%

:fail
echo 构建或测试失败。
popd
exit /b 1
