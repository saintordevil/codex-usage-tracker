@echo off
setlocal
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_GENERATE_ASPNET_CERTIFICATE=false
set DOTNET_NOLOGO=1
set "DOCK_DOTNET=dotnet"
if defined USAGE_DOCK_DOTNET set "DOCK_DOTNET=%USAGE_DOCK_DOTNET%"
"%DOCK_DOTNET%" run --project Tests\UsageDock.Tests.csproj -c Release --disable-build-servers
if errorlevel 1 exit /b 1
"%DOCK_DOTNET%" publish App\UsageDock.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -p:PublishTrimmed=false --disable-build-servers -o release
if errorlevel 1 exit /b 1
for %%F in (README.md LICENSE LICENSE-OpenUsage.txt LICENSE-ZstdSharp.txt UPSTREAM.md) do copy /y "%%F" "release\%%F" >nul
copy /y "launchers\*.cmd" "release\" >nul
if not exist "release\assets" mkdir "release\assets"
if exist "assets\example-dark.png" copy /y "assets\*.png" "release\assets\" >nul
exit /b %errorlevel%
