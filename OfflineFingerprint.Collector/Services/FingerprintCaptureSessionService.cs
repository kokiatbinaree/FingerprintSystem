using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace OfflineFingerprint.Collector.Services;

public sealed class FingerprintCaptureSessionService
{
    private readonly HttpClient _http;
    private readonly string _agentUrl;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public FingerprintCaptureSessionService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _agentUrl = config["Futronic:LiveAgentBaseUrl"] ?? "http://127.0.0.1:15271";
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<Guid> StartAsync(CancellationToken ct)
    {
        if (_sessions.Values.Any(x => !x.Done))
            throw new InvalidOperationException("A fingerprint capture is already in progress.");

        using var response = await _http.PostAsync($"{_agentUrl}/device/open", null, ct);
        response.EnsureSuccessStatusCode();

        Guid sessionId = Guid.NewGuid();
        _sessions[sessionId] = new Session(sessionId);
        return sessionId;
    }

    public async Task<PreviewSnapshot> PollAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException("Capture session not found.");

        if (session.Done)
            return Snapshot(session);

        bool fingerPresent = await ReadFingerPresentAsync(ct);
        if (fingerPresent) session.HadFinger = true;

        if (!session.HadFinger || fingerPresent)
        {
            byte[]? png = await ReadCurrentPngAsync(ct);
            if (png is not null)
            {
                DecodePngToGray(png, out var gray, out var width, out var height);
                session.LatestGray = gray;
                session.Width = width;
                session.Height = height;
                session.LastPng = png;
            }
        }

        if (session.HadFinger && !fingerPresent)
        {
            session.Done = session.LatestGray is { Length: > 0 };
            if (!session.Done) session.Error = "Fingerprint frame was not received.";
        }

        return Snapshot(session);
    }

    public async Task<CapturedImage> ConfirmAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException("Capture session not found.");

        if (!session.Done || session.LatestGray is not { Length: > 0 })
            throw new InvalidOperationException("Lift your finger after a live frame has been captured, then confirm.");

        _sessions.TryRemove(sessionId, out _);
        return new CapturedImage(session.LatestGray, session.Width, session.Height);
    }

    public async Task CancelAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_sessions.TryRemove(sessionId, out _)) return;
        try { await _http.PostAsync($"{_agentUrl}/device/close", null, ct); } catch { }
    }

    private async Task<bool> ReadFingerPresentAsync(CancellationToken ct)
    {
        try
        {
            var status = await _http.GetFromJsonAsync<AgentStatus>($"{_agentUrl}/scanner/status", ct);
            return status?.FingerPresent == true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<byte[]?> ReadCurrentPngAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"{_agentUrl}/image", ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static PreviewSnapshot Snapshot(Session session)
    {
        string? base64 = session.LastPng is { Length: > 0 }
            ? Convert.ToBase64String(session.LastPng)
            : null;

        return new PreviewSnapshot(
            session.Id,
            session.Done ? "done" : "inprogress",
            session.Width,
            session.Height,
            base64,
            session.LastPng is { Length: > 0 },
            session.Error);
    }

    private static void DecodePngToGray(byte[] png, out byte[] gray, out int width, out int height)
    {
        using Image<L8> image = Image.Load<L8>(png);
        width = image.Width;
        height = image.Height;
        gray = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowSpan(y);
            for (int x = 0; x < width; x++)
                gray[y * width + x] = row[x].PackedValue;
        }
    }

    private sealed class Session(Guid id)
    {
        public Guid Id { get; } = id;
        public int Width { get; set; } = 320;
        public int Height { get; set; } = 480;
        public bool HadFinger { get; set; }
        public bool Done { get; set; }
        public byte[]? LatestGray { get; set; }
        public byte[]? LastPng { get; set; }
        public string? Error { get; set; }
    }

    private sealed record AgentStatus(bool Ready, int Width, int Height, int ImageSize, bool FingerPresent, DateTimeOffset LastFrame);

    public sealed record PreviewSnapshot(Guid SessionId,string Status,int Width,int Height,string? PngBase64,bool HasImage,string? Error);
    public sealed record CapturedImage(byte[] GrayBytes,int Width,int Height);
}
