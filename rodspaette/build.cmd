@echo off
setlocal
title Building Rodspaette PDF
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET 8 SDK is not installed.
  echo Get it here: https://dotnet.microsoft.com/download/dotnet/8.0
  echo Install it, then run this file again.
  pause
  exit /b 1
)

echo Building...
dotnet publish "src\Rodspaette.App\Rodspaette.App.csproj" -c Release -r win-x64 --self-contained false -o "publish" -nologo
if errorlevel 1 (
  echo.
  echo The build failed. Copy the error lines above and send them to Claude.
  pause
  exit /b 1
)

echo.
echo Installing for this user...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
  echo Install step failed. You can still run publish\RodspaettePDF.exe directly.
  pause
  exit /b 1
)
echo.
echo Done. Rodspaette PDF is in your Start menu.
timeout /t 4 >nul
