@echo off
cd /d "%~dp0.."
dotnet run --project OfflineFingerprint.CloudSyncWorker\OfflineFingerprint.CloudSyncWorker.csproj
pause
