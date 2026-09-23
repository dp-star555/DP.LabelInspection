@echo off
setlocal
cd /d "%~dp0"
set "TARGET=net8.0-windows"
if /I "%~1"=="net48" set "TARGET=net48"
dotnet build samples\DP.LabelInspection.Demo.Wpf\DP.LabelInspection.Demo.Wpf.csproj -c Release -f %TARGET% --nologo
if errorlevel 1 (pause & exit /b 1)
if /I "%TARGET%"=="net48" (
  start "" /wait "samples\DP.LabelInspection.Demo.Wpf\bin\Release\net48\DP.LabelInspection.Demo.Wpf.exe"
) else (
  dotnet samples\DP.LabelInspection.Demo.Wpf\bin\Release\net8.0-windows\DP.LabelInspection.Demo.Wpf.dll
)
endlocal
