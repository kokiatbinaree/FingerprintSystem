using Microsoft.EntityFrameworkCore;
using OfflineFingerprint.Collector.Data;
using OfflineFingerprint.Collector.Models;
using OfflineFingerprint.Collector.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5140");
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data"));
builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton<LocalKeyService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddHttpClient<FutronicBridgeService>();
builder.Services.AddSingleton<FingerprintCaptureSessionService>(sp =>
    new FingerprintCaptureSessionService(
        new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
        sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<FingerprintStorageService>();
string[] origins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://127.0.0.1:5173", "http://localhost:5173", "http://192.168.1.122:5173"];
builder.Services.AddCors(o => o.AddPolicy("Web", p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    var admin = db.Users.FirstOrDefault(x => x.Username == "admin");
    if (admin is null)
        db.Users.Add(new AppUser { Id = Guid.NewGuid(), Username = "admin", DisplayName = "Administrator", Role = "Admin", PasswordHash = PasswordService.Hash("ChangeMe123!"), CreatedAtUtc = DateTime.UtcNow, IsActive = true });
    else
    {
        admin.PasswordHash = PasswordService.Hash("ChangeMe123!");
        admin.IsActive = true;
        admin.Role = "Admin";
    }
    db.SaveChanges();
}

app.UseCors("Web");
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/auth/login", async (LoginRequest req, AppDbContext db, TokenService tokens) =>
{
    var user = await db.Users.FirstOrDefaultAsync(x => x.Username == req.Username && x.IsActive);
    if (user is null || !PasswordService.Verify(req.Password, user.PasswordHash)) return Results.Unauthorized();
    string token = tokens.Issue(user.Id, user.Role);
    return Results.Ok(new { token, user = new { user.Id, user.Username, user.DisplayName, user.Role } });
});

app.MapGet("/api/persons", async (string? search, AppDbContext db, HttpRequest req, TokenService tokens) =>
{
    if (!TryAuth(req, tokens, out _)) return Results.Unauthorized();
    IQueryable<Person> q = db.Persons.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(search))
    {
        string s = search.Trim().ToLower();
        q = q.Where(x => x.PersonCode.ToLower().Contains(s) || x.FirstName.ToLower().Contains(s) || x.LastName.ToLower().Contains(s));
    }
    return Results.Ok(await q.OrderBy(x => x.PersonCode).Select(x => new { x.Id, x.PersonCode, x.FirstName, x.LastName, x.NationalId, x.Note }).ToListAsync());
});

app.MapPost("/api/persons", async (Person person, AppDbContext db, HttpRequest req, TokenService tokens) =>
{
    if (!TryAuth(req, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(person.PersonCode) || string.IsNullOrWhiteSpace(person.FirstName)) return Results.BadRequest("PersonCode and FirstName are required.");
    if (await db.Persons.AnyAsync(x => x.PersonCode == person.PersonCode)) return Results.Conflict("PersonCode already exists.");
    person.Id = Guid.NewGuid(); person.CreatedAtUtc = person.UpdatedAtUtc = DateTime.UtcNow;
    db.Persons.Add(person); await db.SaveChangesAsync();
    return Results.Created($"/api/persons/{person.Id}", person);
});

app.MapPut("/api/persons/{id:guid}", async (Guid id, Person input, AppDbContext db, HttpRequest req, TokenService tokens) =>
{
    if (!TryAuth(req, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    var person = await db.Persons.FindAsync(id); if (person is null) return Results.NotFound();
    person.PersonCode = input.PersonCode; person.FirstName = input.FirstName; person.LastName = input.LastName; person.NationalId = input.NationalId; person.Note = input.Note; person.UpdatedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(); return Results.Ok(person);
});

app.MapDelete("/api/persons/{id:guid}", async (Guid id, AppDbContext db, HttpRequest req, TokenService tokens) =>
{
    if (!TryAuth(req, tokens, out var auth) || auth.Role is not "Admin") return Results.Forbid();
    var person = await db.Persons.FindAsync(id); if (person is null) return Results.NotFound();
    db.Persons.Remove(person); await db.SaveChangesAsync(); return Results.NoContent();
});

app.MapGet("/api/scanner/status", async (FutronicBridgeService bridge, HttpRequest req, TokenService tokens) =>
{
    if (!TryAuth(req, tokens, out _)) return Results.Unauthorized();
    return Results.Ok(new { ready = await bridge.PingAsync(CancellationToken.None) });
});

app.MapPost("/api/capture/preview", async (CaptureRequest req, FutronicBridgeService bridge, HttpRequest http, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(http, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    if (!IsValidFinger(req.FingerCode)) return Results.BadRequest("Invalid finger code.");
    if (!IsValidPosition(req.Position)) return Results.BadRequest("Invalid position.");
    var result = await bridge.CaptureAsync(ct);
    byte[] png = PngEncoder.EncodeGrayscale(result.GrayBytes, result.Width, result.Height);
    return Results.Ok(new { width = result.Width, height = result.Height, contentType = "image/png", pngBase64 = Convert.ToBase64String(png), grayBase64 = Convert.ToBase64String(result.GrayBytes) });
});

app.MapPost("/api/capture/realtime/start", async (CaptureRequest req, FingerprintCaptureSessionService sessions, HttpRequest http, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(http, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    if (!IsValidFinger(req.FingerCode)) return Results.BadRequest("Invalid finger code.");
    if (!IsValidPosition(req.Position)) return Results.BadRequest("Invalid position.");
    try
    {
        Guid sessionId = await sessions.StartAsync(ct);
        return Results.Ok(new { sessionId, status = "starting" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(ex.Message);
    }
});

app.MapGet("/api/capture/realtime/{sessionId:guid}", async (Guid sessionId, FingerprintCaptureSessionService sessions, HttpRequest http, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(http, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    try
    {
        var snapshot = await sessions.PollAsync(sessionId, ct);
        return Results.Ok(snapshot);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound("Capture session not found.");
    }
});

app.MapPost("/api/capture/realtime/{sessionId:guid}/confirm", async (Guid sessionId, ConfirmCaptureRequest req, FingerprintCaptureSessionService sessions, AppDbContext db, FingerprintStorageService storage, HttpRequest http, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(http, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    if (!IsValidFinger(req.FingerCode)) return Results.BadRequest("Invalid finger code.");
    if (!IsValidPosition(req.Position)) return Results.BadRequest("Invalid position.");
    if (await db.Persons.FindAsync([req.PersonId], ct) is not Person person) return Results.NotFound("Person not found.");
    try
    {
        var captured = await sessions.ConfirmAsync(sessionId, ct);
        int next = await db.FingerprintImages.Where(x => x.PersonId == req.PersonId && x.FingerCode == req.FingerCode && x.Position == req.Position).Select(x => (int?)x.SequenceNo).MaxAsync(ct) ?? 0;
        var stored = await storage.SaveGrayAsync(req.PersonId, req.FingerCode, req.Position, captured.GrayBytes, captured.Width, captured.Height, ct);
        var row = new FingerprintImage { Id = Guid.NewGuid(), PersonId = person.Id, FingerCode = req.FingerCode, Position = req.Position, SequenceNo = next + 1, EncryptedFileName = stored.FileName, Width = stored.Width, Height = stored.Height, CapturedAtUtc = DateTime.UtcNow, SyncStatus = "Pending" };
        db.FingerprintImages.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { row.Id, row.FingerCode, row.Position, row.SequenceNo, row.Width, row.Height, row.SyncStatus });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound("Capture session not found.");
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(ex.Message);
    }
});

app.MapPost("/api/capture/realtime/{sessionId:guid}/cancel", async (Guid sessionId, FingerprintCaptureSessionService sessions, HttpRequest http, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(http, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    await sessions.CancelAsync(sessionId, ct);
    return Results.NoContent();
});

app.MapPost("/api/capture/confirm", async (ConfirmCaptureRequest req, AppDbContext db, FingerprintStorageService storage, HttpRequest http, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(http, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    if (!IsValidFinger(req.FingerCode)) return Results.BadRequest("Invalid finger code.");
    if (!IsValidPosition(req.Position)) return Results.BadRequest("Invalid position.");
    if (await db.Persons.FindAsync([req.PersonId], ct) is not Person person) return Results.NotFound("Person not found.");
    byte[] gray;
    try { gray = Convert.FromBase64String(req.GrayBase64); }
    catch (FormatException) { return Results.BadRequest("Invalid fingerprint image data."); }
    if (req.Width <= 0 || req.Height <= 0 || gray.Length != req.Width * req.Height) return Results.BadRequest("Invalid fingerprint image dimensions.");
    int next = await db.FingerprintImages.Where(x => x.PersonId == req.PersonId && x.FingerCode == req.FingerCode && x.Position == req.Position).Select(x => (int?)x.SequenceNo).MaxAsync(ct) ?? 0;
    var stored = await storage.SaveGrayAsync(req.PersonId, req.FingerCode, req.Position, gray, req.Width, req.Height, ct);
    var row = new FingerprintImage { Id = Guid.NewGuid(), PersonId = person.Id, FingerCode = req.FingerCode, Position = req.Position, SequenceNo = next + 1, EncryptedFileName = stored.FileName, Width = stored.Width, Height = stored.Height, CapturedAtUtc = DateTime.UtcNow, SyncStatus = "Pending" };
    db.FingerprintImages.Add(row);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { row.Id, row.FingerCode, row.Position, row.SequenceNo, row.Width, row.Height, row.SyncStatus });
});

app.MapGet("/api/fingerprints/person/{personId:guid}", async (Guid personId, AppDbContext db, HttpRequest req, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(req, tokens, out var auth) || auth.Role is not ("Admin" or "Collector" or "Analyst")) return Results.Forbid();
    var rows = await db.FingerprintImages.AsNoTracking().Where(x => x.PersonId == personId).OrderBy(x => x.FingerCode).ThenBy(x => x.Position).ThenBy(x => x.SequenceNo).ToListAsync(ct);
    return Results.Ok(rows.Select(x => new { x.Id, x.FingerCode, x.Position, x.SequenceNo, x.Width, x.Height, x.CapturedAtUtc, x.SyncStatus }));
});

app.MapGet("/api/fingerprints/{id:guid}/preview", async (Guid id, AppDbContext db, FingerprintStorageService storage, HttpRequest req, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(req, tokens, out var auth) || auth.Role is not ("Admin" or "Collector" or "Analyst")) return Results.Forbid();
    var item = await db.FingerprintImages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct); if (item is null) return Results.NotFound();
    return Results.File(await storage.ReadDecryptedAsync(item.EncryptedFileName, ct), "image/png");
});

app.MapGet("/api/fingerprints/{id:guid}/download", async (Guid id, AppDbContext db, FingerprintStorageService storage, HttpRequest req, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(req, tokens, out var auth) || auth.Role is not ("Admin" or "Analyst")) return Results.Forbid();
    var item = await db.FingerprintImages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct); if (item is null) return Results.NotFound();
    string fileName = $"{item.FingerCode}-{item.Position}-{item.SequenceNo:00}.png";
    return Results.File(await storage.ReadDecryptedAsync(item.EncryptedFileName, ct), "image/png", fileName);
});

app.MapDelete("/api/fingerprints/{id:guid}", async (Guid id, AppDbContext db, FingerprintStorageService storage, HttpRequest req, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(req, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    var item = await db.FingerprintImages.FirstOrDefaultAsync(x => x.Id == id, ct);
    if (item is null) return Results.NotFound();
    await storage.DeleteAsync(item.EncryptedFileName, ct);
    db.FingerprintImages.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapDelete("/api/fingerprints/person/{personId:guid}/{fingerCode}/{position}/{sequenceNo:int}", async (Guid personId, string fingerCode, string position, int sequenceNo, AppDbContext db, FingerprintStorageService storage, HttpRequest req, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(req, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    if (!IsValidFinger(fingerCode) || !IsValidPosition(position) || sequenceNo <= 0) return Results.BadRequest("Invalid fingerprint target.");
    var item = await db.FingerprintImages.FirstOrDefaultAsync(x => x.PersonId == personId && x.FingerCode == fingerCode && x.Position == position && x.SequenceNo == sequenceNo, ct);
    if (item is null) return Results.NotFound();
    await storage.DeleteAsync(item.EncryptedFileName, ct);
    db.FingerprintImages.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapDelete("/api/fingerprints/person/{personId:guid}/{fingerCode}", async (Guid personId, string fingerCode, AppDbContext db, FingerprintStorageService storage, HttpRequest req, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(req, tokens, out var auth) || auth.Role is not ("Admin" or "Collector")) return Results.Forbid();
    if (!IsValidFinger(fingerCode)) return Results.BadRequest("Invalid finger code.");
    var items = await db.FingerprintImages.Where(x => x.PersonId == personId && x.FingerCode == fingerCode).ToListAsync(ct);
    foreach (var item in items) await storage.DeleteAsync(item.EncryptedFileName, ct);
    db.FingerprintImages.RemoveRange(items);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { deleted = items.Count });
});

app.MapDelete("/api/fingerprints/person/{personId:guid}", async (Guid personId, AppDbContext db, FingerprintStorageService storage, HttpRequest req, TokenService tokens, CancellationToken ct) =>
{
    if (!TryAuth(req, tokens, out var auth) || auth.Role is not "Admin") return Results.Forbid();
    var items = await db.FingerprintImages.Where(x => x.PersonId == personId).ToListAsync(ct);
    foreach (var item in items) await storage.DeleteAsync(item.EncryptedFileName, ct);
    db.FingerprintImages.RemoveRange(items);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { deleted = items.Count });
});

app.MapFallbackToFile("index.html");
app.Run();

static bool TryAuth(HttpRequest request, TokenService tokens, out TokenService.TokenInfo info)
{
    info = null!;
    string? header = request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
    return tokens.TryGet(header[7..].Trim(), out info);
}

static bool IsValidFinger(string value) => value is "L1" or "L2" or "L3" or "L4" or "L5" or "R1" or "R2" or "R3" or "R4" or "R5";
static bool IsValidPosition(string value) => value is "left" or "center" or "right";

public sealed record LoginRequest(string Username, string Password);
public sealed record CaptureRequest(Guid PersonId, string FingerCode, string Position);
public sealed record ConfirmCaptureRequest(Guid PersonId, string FingerCode, string Position, int Width, int Height, string GrayBase64);
