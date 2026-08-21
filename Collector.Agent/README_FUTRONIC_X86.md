# Futronic Collector.Agent (x86)

This Agent uses the native `ftrScanAPI.dll` directly and must run as 32-bit (`win-x86`).

## Install the native DLL

Copy your working `ftrScanAPI.dll` into this folder:

`Collector.Agent\ftrScanAPI.dll`

The project is configured to copy that DLL to the build/publish output.

## Run

From `D:\FingerprintSystemMBT\FingerprintSystem\Collector.Agent`:

```bat
dotnet build
dotnet run
```

The Agent listens on:

`http://127.0.0.1:15271`

Health:

`http://127.0.0.1:15271/health`

Realtime WebSocket:

`ws://127.0.0.1:15271/ws/preview`

## Browser flow

The Web UI uses the WebSocket for live fingerprint frames. When the operator stops the live preview, the latest 320x480 grayscale frame is sent to the Collector API `/api/capture/confirm` only after the operator confirms the image.
