using System.Net.WebSockets;
using FutronicBridge;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:15271");
builder.Services.AddSingleton<FutronicScanner>();
var app = builder.Build();
app.UseWebSockets();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "Collector.Agent" }));

app.MapGet("/scanner/status", (FutronicScanner scanner) =>
{
    var s = scanner.GetStatus();
    return Results.Ok(new
    {
        ready = s.Open || scanner.Open(),
        width = s.Width,
        height = s.Height,
        imageSize = s.ImageSize,
        fingerPresent = s.FingerPresent,
        lastFrame = s.LastFrame
    });
});

app.MapGet("/device", (FutronicScanner scanner) => Results.Json(scanner.GetStatus()));
app.MapPost("/device/open", (FutronicScanner scanner) => Results.Ok(new { ok = scanner.Open() }));
app.MapPost("/device/close", (FutronicScanner scanner) =>
{
    scanner.Close();
    return Results.Ok(new { ok = true });
});

app.MapGet("/image", (FutronicScanner scanner) =>
{
    var frame = scanner.GetCurrentFrame();
    return frame is null ? Results.NoContent() : Results.File(frame.Png, "image/png");
});

app.MapPost("/capture", (FutronicScanner scanner) =>
{
    var frame = scanner.GetCurrentFrame();
    return frame is null
        ? Results.BadRequest(new { ok = false, error = "No fingerprint frame available." })
        : Results.File(frame.Png, "image/png", "fingerprint.png");
});

app.Map("/ws/preview", async (HttpContext context, FutronicScanner scanner) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket required.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await scanner.StreamAsync(socket, context.RequestAborted);
});

app.Run();
