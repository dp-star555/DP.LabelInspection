@echo off
setlocal
cd /d "%~dp0"
set "TARGET=net8.0-windows"
if /I "%~1"=="net48" set "TARGET=net48"
dotnet build samples\DP.LabelInspection.Demo.WinForms\DP.LabelInspection.Demo.WinForms.csproj -c Release -f %TARGET% --nologo
if errorlevel 1 (
  echo Build failed. Install an appropriate .NET SDK and check the output above.
  pause
  exit /b 1
)
if /I "%TARGET%"=="net48" (
  start "" /wait "samples\DP.LabelInspection.Demo.WinForms\bin\Release\net48\DP.LabelInspection.Demo.WinForms.exe"
) else (
  dotnet samples\DP.LabelInspection.Demo.WinForms\bin\Release\net8.0-windows\DP.LabelInspection.Demo.WinForms.dll
)
endlocal
