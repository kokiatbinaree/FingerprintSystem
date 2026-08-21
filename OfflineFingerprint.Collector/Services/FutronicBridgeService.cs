using System.Text.Json;

namespace OfflineFingerprint.Collector.Services;

public sealed class FutronicBridgeService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _operationPath;
    public FutronicBridgeService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _baseUrl = config["Futronic:BaseUrl"] ?? "http://127.0.0.1:15270";
        _operationPath = config["Futronic:OperationPath"] ?? "/fpoperation";
        _http.Timeout = TimeSpan.FromSeconds(8);
    }
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync(_baseUrl + _operationPath, new { operation = "capture", lfd = "no", invert = "yes" }, ct);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }
    public async Task<CaptureResult> CaptureAsync(CancellationToken ct)
    {
        using var start = await _http.PostAsJsonAsync(_baseUrl + _operationPath, new { operation = "capture", lfd = "no", invert = "yes" }, ct);
        start.EnsureSuccessStatusCode();
        using JsonDocument doc = await JsonDocument.ParseAsync(await start.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        string id = doc.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("Bridge did not return operation id.");
        int width = doc.RootElement.TryGetProperty("devwidth", out var w) ? w.GetInt32() : 0;
        int height = doc.RootElement.TryGetProperty("devheight", out var h) ? h.GetInt32() : 0;
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var stateRes = await _http.GetAsync($"{_baseUrl}{_operationPath}/{id}", ct);
            stateRes.EnsureSuccessStatusCode();
            using JsonDocument state = await JsonDocument.ParseAsync(await stateRes.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            string stateValue = state.RootElement.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            string status = state.RootElement.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            if (state.RootElement.TryGetProperty("devwidth", out var sw)) width = sw.GetInt32();
            if (state.RootElement.TryGetProperty("devheight", out var sh)) height = sh.GetInt32();
            if (stateValue == "done")
            {
                if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fingerprint capture failed.");
                using var imageRes = await _http.GetAsync($"{_baseUrl}{_operationPath}/{id}/image", ct);
                imageRes.EnsureSuccessStatusCode();
                byte[] bytes = await imageRes.Content.ReadAsByteArrayAsync(ct);
                if (width <= 0 || height <= 0 || bytes.Length != width * height) throw new InvalidOperationException("Invalid fingerprint image dimensions from bridge.");
                return new CaptureResult(bytes, width, height);
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException("Fingerprint capture timed out.");
    }
    public sealed record CaptureResult(byte[] GrayBytes, int Width, int Height);
}
