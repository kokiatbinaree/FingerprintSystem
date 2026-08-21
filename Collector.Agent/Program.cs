using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Json;

const string bridgeBase = "http://127.0.0.1:15270/fpoperation";
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:15271");

var app = builder.Build();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "Collector.Agent" }));

app.MapGet("/scanner/status", async (CancellationToken ct) =>
{
    try
    {
        using var response = await http.PostAsJsonAsync(bridgeBase, new { operation = "capture", lfd = "no", invert = "yes" }, ct);
        if (!response.IsSuccessStatusCode)
            return Results.Ok(new { ready = false, status = (int)response.StatusCode });

        var payload = await response.Content.ReadFromJsonAsync<BridgeResponse>(cancellationToken: ct);
        return Results.Ok(new { ready = payload?.status == "success", bridge = payload?.status });
    }
    catch
    {
        return Results.Ok(new { ready = false, status = "bridge-unreachable" });
    }
});

app.MapPost("/scanner/capture", async (CaptureRequest request, CancellationToken ct) =>
{
    try
    {
        using var start = await http.PostAsJsonAsync(bridgeBase, new { operation = "capture", lfd = "no", invert = "yes" }, ct);
        if (!start.IsSuccessStatusCode)
            return Results.Problem("Futronic Bridge could not start capture.", statusCode: 502);

        var op = await start.Content.ReadFromJsonAsync<BridgeResponse>(cancellationToken: ct);
        if (op is null || string.IsNullOrWhiteSpace(op.id))
            return Results.Problem("Bridge returned no operation id.", statusCode: 502);

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(request.TimeoutSeconds, 5, 60));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, ct);
            var stateResponse = await http.GetAsync($"{bridgeBase}/{op.id}", ct);
            if (!stateResponse.IsSuccessStatusCode)
                continue;

            var state = await stateResponse.Content.ReadFromJsonAsync<BridgeResponse>(cancellationToken: ct);
            if (state?.state == "inprogress")
                continue;

            if (state?.state == "done" && state.status == "success")
            {
                var imageResponse = await http.GetAsync($"{bridgeBase}/{op.id}/image", ct);
                if (!imageResponse.IsSuccessStatusCode)
                    return Results.Problem("Bridge returned no image.", statusCode: 502);

                var gray = await imageResponse.Content.ReadAsByteArrayAsync(ct);
                int width = ParsePositiveInt(state.devwidth) ?? 320;
                int height = ParsePositiveInt(state.devheight) ?? Math.Max(1, gray.Length / Math.Max(1, width));
                var png = GrayToPng(gray, width, height);

                try { await http.PutAsync($"{bridgeBase}/{op.id}/cancel", null, ct); } catch { }

                return Results.Ok(new { width, height, contentType = "image/png", pngBase64 = Convert.ToBase64String(png) });
            }

            if (state?.state == "done" && state.status == "fail")
                return Results.Problem("Fingerprint capture failed.", statusCode: 409);
        }

        try { await http.PutAsync($"{bridgeBase}/{op.id}/cancel", null, ct); } catch { }
        return Results.StatusCode((int)HttpStatusCode.RequestTimeout);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.StatusCode((int)HttpStatusCode.RequestTimeout);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

app.Run();

static int? ParsePositiveInt(string? value) => int.TryParse(value, out var n) && n > 0 ? n : null;

static byte[] GrayToPng(byte[] gray, int width, int height)
{
    if (gray.Length < width * height)
        throw new InvalidOperationException("Bridge image buffer is smaller than expected.");

    using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
    for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int value = gray[(y * width) + x];
            bmp.SetPixel(x, y, Color.FromArgb(value, value, value));
        }

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

public sealed record CaptureRequest(int TimeoutSeconds = 30);
public sealed class BridgeResponse
{
    public string? status { get; set; }
    public string? state { get; set; }
    public string? operation { get; set; }
    public string? id { get; set; }
    public string? devwidth { get; set; }
    public string? devheight { get; set; }
}
