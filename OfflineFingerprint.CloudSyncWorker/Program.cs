using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OfflineFingerprint.Collector.Data;
using OfflineFingerprint.Collector.Models;
using OfflineFingerprint.Collector.Services;

var collectorPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OfflineFingerprint.Collector"));
var dataPath = Path.Combine(collectorPath, "data");
Directory.CreateDirectory(dataPath);
var databasePath = Path.Combine(dataPath, "fingerprint.db");
var credentialsPath = Path.GetFullPath(Path.Combine(collectorPath, "..", "Collector.Agent", "secrets", "google-drive", "credentials.json"));

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Futronic:CollectorAgentBaseUrl"] = "http://127.0.0.1:15271",
        ["Firebase:ProjectId"] = "fingerprintsystemmbt",
        ["Firebase:CredentialsPath"] = credentialsPath
    })
    .Build();

var host = Host.CreateDefaultBuilder(args)
    .UseContentRoot(collectorPath)
    .ConfigureServices(services =>
    {
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<LocalKeyService>();
        services.AddSingleton<FingerprintStorageService>();
        services.AddHttpClient();
        services.AddScoped<GoogleDriveSyncService>();
        services.AddSingleton<FirestoreMetadataService>();
    })
    .Build();

Console.WriteLine("FingerprintSystem Cloud Sync Worker");
Console.WriteLine($"Collector path: {collectorPath}");
Console.WriteLine($"SQLite path: {databasePath}");
Console.WriteLine($"Firestore project: {configuration["Firebase:ProjectId"]}");
Console.WriteLine("Watching for Drive and Firestore sync. Press Ctrl+C to stop.");

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
        var synced = await ProcessPendingAsync(host.Services, stopCts.Token);
        if (synced > 0)
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Synced {synced} fingerprint(s) to Drive/Firestore.");
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
            .OrderBy(x => x.CapturedAtUtc)
            .Take(50)
            .Select(x => new { x.Id })
            .ToListAsync(ct);

        ids = new List<Guid>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var driveRecord = await db.CloudSyncRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.FingerprintImageId == candidate.Id && x.Provider == "GoogleDrive", ct);
            var firestoreRecord = await db.CloudSyncRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.FingerprintImageId == candidate.Id && x.Provider == "Firestore", ct);

            var driveReady = driveRecord?.Status == "Synced" && !string.IsNullOrWhiteSpace(driveRecord.DriveFileId);
            var firestoreReady = firestoreRecord?.Status == "Synced";

            if (driveReady && firestoreReady)
                continue;

            if (firestoreRecord?.Status == "Failed" && firestoreRecord.LastAttemptAtUtc.HasValue && firestoreRecord.LastAttemptAtUtc.Value > retryCutoff && driveReady)
                continue;

            if (driveRecord?.Status == "Failed" && driveRecord.LastAttemptAtUtc.HasValue && driveRecord.LastAttemptAtUtc.Value > retryCutoff)
                continue;

            ids.Add(candidate.Id);
        }
    }

    int synced = 0;
    foreach (var id in ids)
    {
        using var scope = root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var imageBefore = await db.FingerprintImages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (imageBefore is null)
                continue;

            if (!string.Equals(imageBefore.SyncStatus, "Synced", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(imageBefore.DriveFileId))
            {
                var driveSync = scope.ServiceProvider.GetRequiredService<GoogleDriveSyncService>();
                await driveSync.SyncFingerprintAsync(id, ct);
            }

            var image = await db.FingerprintImages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (image is null || string.IsNullOrWhiteSpace(image.DriveFileId))
                throw new InvalidOperationException("Fingerprint is not Drive-synced yet.");

            var person = await db.Persons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == image.PersonId, ct)
                ?? throw new KeyNotFoundException("Person not found.");

            var firestoreRecord = await db.CloudSyncRecords
                .FirstOrDefaultAsync(x => x.FingerprintImageId == id && x.Provider == "Firestore", ct);

            if (firestoreRecord is not null && firestoreRecord.Status == "Synced")
            {
                synced++;
                continue;
            }

            if (firestoreRecord is null)
            {
                firestoreRecord = new CloudSyncRecord
                {
                    Id = Guid.NewGuid(),
                    FingerprintImageId = id,
                    Provider = "Firestore"
                };
                db.Set<CloudSyncRecord>().Add(firestoreRecord);
            }

            firestoreRecord.Status = "Syncing";
            firestoreRecord.AttemptCount++;
            firestoreRecord.LastAttemptAtUtc = DateTime.UtcNow;
            firestoreRecord.LastError = "";
            await db.SaveChangesAsync(ct);

            try
            {
                var firestore = scope.ServiceProvider.GetRequiredService<FirestoreMetadataService>();
                var link = $"https://drive.google.com/file/d/{image.DriveFileId}/view?usp=drivesdk";
                await firestore.UpsertFingerprintAsync(person, image, image.DriveFileId, link, ct);

                firestoreRecord.Status = "Synced";
                firestoreRecord.DriveFileId = image.DriveFileId;
                firestoreRecord.DriveWebViewLink = link;
                firestoreRecord.SyncedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                synced++;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Firestore synced: {person.PersonCode} / {image.FingerCode} / {image.Position} / #{image.SequenceNo}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                firestoreRecord.Status = "Failed";
                firestoreRecord.LastError = ex.Message;
                await db.SaveChangesAsync(ct);
                throw;
            }
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
    CONSTRAINT FK_CloudSyncRecords_FingerprintImageId_FingerprintImages
        FOREIGN KEY (FingerprintImageId) REFERENCES FingerprintImages (Id) ON DELETE CASCADE
);", ct);

    await db.Database.ExecuteSqlRawAsync(@"
CREATE UNIQUE INDEX IF NOT EXISTS IX_CloudSyncRecords_FingerprintImageId_Provider
ON CloudSyncRecords (FingerprintImageId, Provider);", ct);
}
