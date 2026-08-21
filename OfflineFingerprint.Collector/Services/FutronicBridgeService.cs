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
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync(
                _baseUrl + _operationPath,
                new { operation = "capture", lfd = "no", invert = "yes" },
                ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<CaptureResult> CaptureAsync(CancellationToken ct)
    {
        using var start = await _http.PostAsJsonAsync(
            _baseUrl + _operationPath,
            new { operation = "capture", lfd = "no", invert = "yes" },
            ct);

        start.EnsureSuccessStatusCode();

        using JsonDocument doc = await JsonDocument.ParseAsync(
            await start.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);

        JsonElement root = doc.RootElement;
        string id = root.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Bridge did not return operation id.");

        int width = GetInt(root, "devwidth") ?? 0;
        int height = GetInt(root, "devheight") ?? 0;

        for (int i = 0; i < 150; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var stateRes = await _http.GetAsync(
                $"{_baseUrl}{_operationPath}/{id}", ct);

            stateRes.EnsureSuccessStatusCode();

            using JsonDocument stateDoc = await JsonDocument.ParseAsync(
                await stateRes.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct);

            JsonElement stateRoot = stateDoc.RootElement;
            string stateValue = GetString(stateRoot, "state") ?? "";
            string status = GetString(stateRoot, "status") ?? "";

            width = GetInt(stateRoot, "devwidth") ?? width;
            height = GetInt(stateRoot, "devheight") ?? height;

            if (stateValue.Equals("inprogress", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(200, ct);
                continue;
            }

            if (stateValue.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                if (!status.Equals("success", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fingerprint capture failed.");

                using var imageRes = await _http.GetAsync(
                    $"{_baseUrl}{_operationPath}/{id}/image", ct);

                imageRes.EnsureSuccessStatusCode();

                byte[] bytes = await imageRes.Content.ReadAsByteArrayAsync(ct);

                if (width <= 0 || height <= 0)
                    throw new InvalidOperationException("Bridge did not provide valid fingerprint dimensions.");

                long expected = (long)width * height;
                if (bytes.LongLength != expected)
                {
                    throw new InvalidOperationException(
                        $"Invalid fingerprint image size. Expected {expected} bytes ({width}x{height}), received {bytes.LongLength} bytes.");
                }

                try
                {
                    await _http.PutAsync($"{_baseUrl}{_operationPath}/{id}/cancel", null, ct);
                }
                catch
                {
                    // Capture is already complete; cancel is only cleanup.
                }

                return new CaptureResult(bytes, width, height);
            }

            if (stateValue.Equals("done", StringComparison.OrdinalIgnoreCase) &&
                status.Equals("fail", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Fingerprint capture failed.");
            }

            await Task.Delay(200, ct);
        }

        try
        {
            await _http.PutAsync($"{_baseUrl}{_operationPath}/{id}/cancel", null, ct);
        }
        catch
        {
            // Best-effort cleanup after timeout.
        }

        throw new TimeoutException("Fingerprint capture timed out.");
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static int? GetInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            return number;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed))
            return parsed;

        return null;
    }

    public sealed record CaptureResult(byte[] GrayBytes, int Width, int Height);
}
