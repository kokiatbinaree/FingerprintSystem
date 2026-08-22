using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OfflineFingerprint.Collector.Data;
using OfflineFingerprint.Collector.Models;
using OfflineFingerprint.Collector.Services;

var collectorPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OfflineFingerprint.Collector"));
var dataPath = Path.Combine(collectorPath, "data");
Directory.CreateDirectory(dataPath);
var databasePath = Path.Combine(dataPath, "fingerprint.db");

var host = Host.CreateDefaultBuilder(args)
    .UseContentRoot(collectorPath)
    .ConfigureServices(services =>
    {
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<LocalKeyService>();
        services.AddSingleton<FingerprintStorageService>();
        services.AddHttpClient();
        services.AddScoped<GoogleDriveSyncService>();
    })
    .Build();

Console.WriteLine("FingerprintSystem Cloud Sync Worker");
Console.WriteLine($"Collector path: {collectorPath}");
Console.WriteLine($"SQLite path: {databasePath}");
Console.WriteLine("Watching for Pending/Failed fingerprint uploads. Press Ctrl+C to stop.");

await EnsureSchemaAsync(host.Services, CancellationToken.None);

using var stopCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopCts.Cancel();
};

while (!stopCts.IsCancellationRequested)
{
    try
    {
        int synced = await ProcessPendingAsync(host.Services, stopCts.Token);
        if (synced > 0)
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Synced {synced} fingerprint(s).");
    }
    catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Worker error: {ex.Message}");
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stopCts.Token);
    }
    catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
    {
        break;
    }
}

Console.WriteLine("Cloud Sync Worker stopped.");

static async Task<int> ProcessPendingAsync(IServiceProvider root, CancellationToken ct)
{
    List<Guid> ids;
    using (var scope = root.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retryCutoff = DateTime.UtcNow.AddSeconds(-30);

        var candidates = await db.FingerprintImages
            .AsNoTracking()
            .Where(x => x.SyncStatus != "Synced")
            .OrderBy(x => x.CapturedAtUtc)
            .Take(20)
            .Select(x => new { x.Id })
            .ToListAsync(ct);

        ids = new List<Guid>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var record = await db.CloudSyncRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.FingerprintImageId == candidate.Id && x.Provider == "GoogleDrive", ct);

            if (record?.Status == "Failed" && record.LastAttemptAtUtc.HasValue && record.LastAttemptAtUtc.Value > retryCutoff)
                continue;

            ids.Add(candidate.Id);
        }
    }

    int synced = 0;
    foreach (var id in ids)
    {
        try
        {
            using var scope = root.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<GoogleDriveSyncService>();
            var result = await sync.SyncFingerprintAsync(id, ct);
            if (result is not null)
                synced++;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sync {id} failed: {ex.Message}");
        }
    }

    return synced;
}

static async Task EnsureSchemaAsync(IServiceProvider root, CancellationToken ct)
{
    using var scope = root.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync(ct);
    await EnsureDriveFileIdColumnAsync(db, ct);
    await EnsureCloudSyncSchemaAsync(db, ct);
}

static async Task EnsureDriveFileIdColumnAsync(AppDbContext db, CancellationToken ct)
{
    await using var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
        await connection.OpenAsync(ct);

    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA table_info(FingerprintImages);";
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        if (string.Equals(reader.GetString(1), "DriveFileId", StringComparison.OrdinalIgnoreCase))
            return;
    }

    await db.Database.ExecuteSqlRawAsync("ALTER TABLE FingerprintImages ADD COLUMN DriveFileId TEXT NULL;", ct);
}

static async Task EnsureCloudSyncSchemaAsync(AppDbContext db, CancellationToken ct)
{
    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS CloudSyncRecords (
    Id TEXT NOT NULL CONSTRAINT PK_CloudSyncRecords PRIMARY KEY,
    FingerprintImageId TEXT NOT NULL,
    Provider TEXT NOT NULL,
    Status TEXT NOT NULL,
    DriveFileId TEXT NOT NULL,
    DriveWebViewLink TEXT NOT NULL,
    LastError TEXT NOT NULL,
    AttemptCount INTEGER NOT NULL,
    LastAttemptAtUtc TEXT NULL,
    SyncedAtUtc TEXT NULL,
    CONSTRAINT FK_CloudSyncRecords_FingerprintImages_FingerprintImageId
        FOREIGN KEY (FingerprintImageId) REFERENCES FingerprintImages (Id) ON DELETE CASCADE
);", ct);

    await db.Database.ExecuteSqlRawAsync(@"
CREATE UNIQUE INDEX IF NOT EXISTS IX_CloudSyncRecords_FingerprintImageId_Provider
ON CloudSyncRecords (FingerprintImageId, Provider);", ct);
}
