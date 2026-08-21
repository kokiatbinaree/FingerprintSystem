@echo off
setlocal
cd /d "%~dp0"
echo === OfflineFingerprint Collector API ===
dotnet restore
if errorlevel 1 exit /b 1
dotnet build
if errorlevel 1 exit /b 1
echo Build complete.
