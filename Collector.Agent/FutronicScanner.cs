using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Net.WebSockets;
using System.Text.Json;

namespace FutronicBridge;

public sealed class FutronicScanner : IDisposable
{
    private IntPtr _handle;
    private readonly object _gate = new();
    private FtrImageSize _size;
    private bool _lastFinger;
    private DateTimeOffset _lastFrameTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FtrFakeReplicaParameters
    {
        [MarshalAs(UnmanagedType.Bool)] public bool Calculated;
        public int CalculatedSum1;
        public int CalculatedSumFuzzy;
        public int CalculatedSumEmpty;
        public int CalculatedSum2;
        public double CalculatedTremor;
        public double CalculatedValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FtrFrameParameters
    {
        public int ContrastOnDose2;
        public int ContrastOnDose4;
        public int Dose;
        public int BrightnessOnDose1;
        public int BrightnessOnDose2;
        public int BrightnessOnDose3;
        public int BrightnessOnDose4;
        public FtrFakeReplicaParameters FakeReplicaParams;
        public FtrFakeReplicaParameters Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FtrImageSize
    {
        public int Width;
        public int Height;
        public int ImageSize;
    }

    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr ftrScanOpenDevice();

    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void ftrScanCloseDevice(IntPtr ftrHandle);

    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ftrScanGetImageSize(IntPtr ftrHandle, out FtrImageSize imageSize);

    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ftrScanGetImage(IntPtr ftrHandle, int dose, byte[] buffer);

    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ftrScanIsFingerPresent(IntPtr ftrHandle, out FtrFrameParameters frameParameters);

    public bool IsOpen => _handle != IntPtr.Zero;

    public bool Open()
    {
        lock (_gate)
        {
            if (IsOpen) return true;
            _handle = ftrScanOpenDevice();
            if (_handle == IntPtr.Zero) return false;
            if (!ftrScanGetImageSize(_handle, out _size))
            {
                ftrScanCloseDevice(_handle);
                _handle = IntPtr.Zero;
                return false;
            }
            return true;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            ftrScanCloseDevice(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public ScannerStatus GetStatus()
    {
        lock (_gate)
        {
            return new ScannerStatus(IsOpen, _size.Width, _size.Height, _size.ImageSize, _lastFinger, _lastFrameTime);
        }
    }

    public Frame? GetCurrentFrame()
    {
        lock (_gate)
        {
            if (!IsOpen && !Open()) return null;
            var raw = ReadFrameLocked();
            return raw is null ? null : BuildFrame(raw);
        }
    }

    private byte[]? ReadFrameLocked()
    {
        if (_handle == IntPtr.Zero) return null;
        try
        {
            ftrScanIsFingerPresent(_handle, out var fp);
            _lastFinger = fp.Dose != -1;
            var raw = new byte[_size.ImageSize];
            if (!ftrScanGetImage(_handle, 4, raw)) return null;
            _lastFrameTime = DateTimeOffset.UtcNow;
            return raw;
        }
        catch
        {
            return null;
        }
    }

    private Frame BuildFrame(byte[] raw)
    {
        using var bmp = new Bitmap(_size.Width, _size.Height, PixelFormat.Format8bppIndexed);
        var palette = bmp.Palette;
        for (int i = 0; i < 256; i++) palette.Entries[i] = Color.FromArgb(255, i, i, i);
        bmp.Palette = palette;
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
        try
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(raw, y * bmp.Width, IntPtr.Add(data.Scan0, y * data.Stride), bmp.Width);
            }
        }
        finally { bmp.UnlockBits(data); }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new Frame(raw, ms.ToArray(), _lastFinger, _lastFrameTime);
    }

    public async Task StreamAsync(WebSocket socket, CancellationToken ct)
    {
        if (!IsOpen && !Open())
        {
            await SendJson(socket, new { type = "error", error = "FS80H could not be opened." }, ct);
            return;
        }

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var frame = GetCurrentFrame();
            if (frame is not null)
            {
                var header = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "frame",
                    width = _size.Width,
                    height = _size.Height,
                    finger = frame.FingerPresent,
                    timestamp = frame.Timestamp
                });
                await socket.SendAsync(header, WebSocketMessageType.Text, true, ct);
                await socket.SendAsync(frame.Png, WebSocketMessageType.Binary, true, ct);
            }
            await Task.Delay(80, ct);
        }
    }

    private static Task SendJson(WebSocket socket, object value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public void Dispose() => Close();
}

public sealed record ScannerStatus(bool Open, int Width, int Height, int ImageSize, bool FingerPresent, DateTimeOffset LastFrame);
public sealed record Frame(byte[] Raw, byte[] Png, bool FingerPresent, DateTimeOffset Timestamp);
