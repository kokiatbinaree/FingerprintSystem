using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace OfflineFingerprint.Collector.Services;

public sealed class FingerprintCaptureSessionService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _operationPath;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public FingerprintCaptureSessionService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _baseUrl = config["Futronic:BaseUrl"] ?? "http://127.0.0.1:15270";
        _operationPath = config["Futronic:OperationPath"] ?? "/fpoperation";
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<Guid> StartAsync(CancellationToken ct)
    {
        if (_sessions.Values.Any(x => x.Status is "starting" or "inprogress"))
            throw new InvalidOperationException("A fingerprint capture is already in progress.");

        using var response = await _http.PostAsJsonAsync(
            _baseUrl + _operationPath,
            new { operation = "capture", lfd = "no", invert = "yes" },
            ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        string operationId = GetString(root, "id") ?? throw new InvalidOperationException("Bridge returned no operation id.");
        int width = GetInt(root, "devwidth") ?? 320;
        int height = GetInt(root, "devheight") ?? 480;

        Guid sessionId = Guid.NewGuid();
        _sessions[sessionId] = new Session(sessionId, operationId, width, height);
        return sessionId;
    }

    public async Task<PreviewSnapshot> PollAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException("Capture session not found.");

        using var stateResponse = await _http.GetAsync(
            $"{_baseUrl}{_operationPath}/{session.OperationId}", ct);
        stateResponse.EnsureSuccessStatusCode();
        using var stateDoc = await JsonDocument.ParseAsync(
            await stateResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = stateDoc.RootElement;

        session.Status = GetString(root, "state") ?? session.Status;
        session.StatusMessage = GetString(root, "status") ?? session.StatusMessage;
        session.Width = GetInt(root, "devwidth") ?? session.Width;
        session.Height = GetInt(root, "devheight") ?? session.Height;

        try
        {
            using var imageResponse = await _http.GetAsync(
                $"{_baseUrl}{_operationPath}/{session.OperationId}/image", ct);
            if (imageResponse.IsSuccessStatusCode)
            {
                byte[] gray = await imageResponse.Content.ReadAsByteArrayAsync(ct);
                if (gray.Length == session.Width * session.Height)
                {
                    session.LatestGray = gray;
                    session.LastFrameHash = Convert.ToHexString(SHA256.HashData(gray)).ToLowerInvariant();
                }
            }
        }
        catch { }

        bool done = string.Equals(session.Status, "done", StringComparison.OrdinalIgnoreCase);
        bool success = string.Equals(session.StatusMessage, "success", StringComparison.OrdinalIgnoreCase);

        if (done && !success)
            session.Error = "Fingerprint capture failed.";

        string? imageBase64 = session.LatestGray is { Length: > 0 }
            ? Convert.ToBase64String(PngEncoder.EncodeGrayscale(session.LatestGray, session.Width, session.Height))
            : null;

        return new PreviewSnapshot(
            session.Id,
            done ? (success ? "done" : "failed") : "inprogress",
            session.Width,
            session.Height,
            imageBase64,
            session.LatestGray is { Length: > 0 },
            session.LastFrameHash,
            session.Error);
    }

    public async Task<CapturedImage> ConfirmAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException("Capture session not found.");

        var snapshot = await PollAsync(sessionId, ct);
        if (snapshot.Status != "done" || session.LatestGray is not { Length: > 0 })
            throw new InvalidOperationException("Capture is not complete yet.");

        _sessions.TryRemove(sessionId, out _);
        return new CapturedImage(session.LatestGray, session.Width, session.Height);
    }

    public async Task CancelAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        try
        {
            await _http.PutAsync($"{_baseUrl}{_operationPath}/{session.OperationId}/cancel", null, ct);
        }
        catch { }
    }

    private static string? GetString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element)) return null;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            _ => null
        };
    }

    private static int? GetInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element)) return null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int n)) return n;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out n)) return n;
        return null;
    }

    private sealed class Session(Guid id, string operationId, int width, int height)
    {
        public Guid Id { get; } = id;
        public string OperationId { get; } = operationId;
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public string Status { get; set; } = "starting";
        public string StatusMessage { get; set; } = "";
        public byte[]? LatestGray { get; set; }
        public string? LastFrameHash { get; set; }
        public string? Error { get; set; }
    }

    public sealed record PreviewSnapshot(
        Guid SessionId,
        string Status,
        int Width,
        int Height,
        string? PngBase64,
        bool HasImage,
        string? FrameHash,
        string? Error);

    public sealed record CapturedImage(byte[] GrayBytes, int Width, int Height);
}
