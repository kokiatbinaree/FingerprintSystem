using System.Net.WebSockets;
using FutronicBridge;
using Collector.Agent;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:15271");
builder.Services.AddSingleton<FutronicScanner>();
builder.Services.AddSingleton<GoogleDriveStorage>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://127.0.0.1:5173", "http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "Collector.Agent" }));

app.MapGet("/scanner/status", (FutronicScanner scanner) =>
{
    var s = scanner.GetStatus();
    return Results.Ok(new { ready = s.Open || scanner.Open(), width = s.Width, height = s.Height, imageSize = s.ImageSize, fingerPresent = s.FingerPresent, lastFrame = s.LastFrame });
});

app.MapGet("/device", (FutronicScanner scanner) => Results.Json(scanner.GetStatus()));
app.MapPost("/device/open", (FutronicScanner scanner) => Results.Ok(new { ok = scanner.Open() }));
app.MapPost("/device/close", (FutronicScanner scanner) => { scanner.Close(); return Results.Ok(new { ok = true }); });
app.MapGet("/image", (FutronicScanner scanner) => { var frame = scanner.GetCurrentFrame(); return frame is null ? Results.NoContent() : Results.File(frame.Png, "image/png"); });
app.MapPost("/capture", (FutronicScanner scanner) => { var frame = scanner.GetCurrentFrame(); return frame is null ? Results.BadRequest(new { ok = false, error = "No fingerprint frame available." }) : Results.File(frame.Png, "image/png", "fingerprint.png"); });

app.MapGet("/drive/status", (GoogleDriveStorage drive) => Results.Ok(drive.GetStatus()));
app.MapPost("/drive/test-upload", async (GoogleDriveStorage drive, CancellationToken ct) =>
{
    try { return Results.Ok(new { ok = true, result = await drive.UploadTestAsync(ct) }); }
    catch (Exception ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Google Drive test failed"); }
});

app.MapPost("/drive/upload-current-image", async (FutronicScanner scanner, GoogleDriveStorage drive, CancellationToken ct) =>
{
    try
    {
        var frame = scanner.GetCurrentFrame();
        if (frame is null) return Results.BadRequest(new { ok = false, error = "No fingerprint frame available." });
        var fileName = $"fingerprint-test-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png";
        return Results.Ok(new { ok = true, result = await drive.UploadPngAsync(frame.Png, fileName, ct) });
    }
    catch (Exception ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Google Drive PNG upload failed"); }
});

app.MapPost("/drive/upload-fingerprint", async (FingerprintDriveUploadRequest request, GoogleDriveStorage drive, CancellationToken ct) =>
{
    try
    {
        if (request.FingerprintId == Guid.Empty) return Results.BadRequest("FingerprintId is required.");
        if (string.IsNullOrWhiteSpace(request.PersonCode)) return Results.BadRequest("PersonCode is required.");
        if (string.IsNullOrWhiteSpace(request.FingerCode)) return Results.BadRequest("FingerCode is required.");
        if (string.IsNullOrWhiteSpace(request.Position)) return Results.BadRequest("Position is required.");
        if (request.SequenceNo <= 0) return Results.BadRequest("SequenceNo must be positive.");
        if (string.IsNullOrWhiteSpace(request.PngBase64)) return Results.BadRequest("PngBase64 is required.");
        byte[] png;
        try { png = Convert.FromBase64String(request.PngBase64); } catch (FormatException) { return Results.BadRequest("Invalid PNG base64 data."); }
        var fileName = $"{Sanitize(request.FingerCode)}-{Sanitize(request.Position)}-{request.SequenceNo:00}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{request.FingerprintId:N}.png";
        var result = await drive.UploadPngAsync(png, fileName, ["FingerprintSystem", "Persons", Sanitize(request.PersonCode), Sanitize(request.FingerCode), Sanitize(request.Position)], ct);
        return Results.Ok(new { ok = true, fingerprintId = request.FingerprintId, result });
    }
    catch (Exception ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Fingerprint Google Drive upload failed"); }
});

app.Map("/ws/preview", async (HttpContext context, FutronicScanner scanner) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; await context.Response.WriteAsync("WebSocket required."); return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await scanner.StreamAsync(socket, context.RequestAborted);
});

app.Run();

static string Sanitize(string value)
{
    var chars = value.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
    var cleaned = new string(chars).Trim('_');
    return cleaned.Length == 0 ? "unknown" : cleaned;
}

public sealed record FingerprintDriveUploadRequest(Guid FingerprintId, string PersonCode, string FingerCode, string Position, int SequenceNo, string PngBase64);
