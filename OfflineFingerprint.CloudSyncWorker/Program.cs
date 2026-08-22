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
    List<SyncCandidate> queue;
    int candidateCount;
    int driveReadyCount = 0;
    int firestoreReadyCount = 0;
    int retrySkippedCount = 0;

    using (var scope = root.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retryCutoff = DateTime.UtcNow.AddSeconds(-30);

        var images = await db.FingerprintImages
            .AsNoTracking()
            .OrderBy(x => x.CapturedAtUtc)
            .Take(50)
            .Select(x => new SyncImageSnapshot(
                x.Id,
                x.PersonId,
                x.FingerCode,
                x.Position,
                x.SequenceNo,
                x.CapturedAtUtc,
                x.SyncStatus,
                x.DriveFileId,
                x.EncryptedFileName))
            .ToListAsync(ct);

        var imageIds = images.Select(x => x.Id).ToHashSet();
        var records = imageIds.Count == 0
            ? []
            : await db.CloudSyncRecords
                .AsNoTracking()
                .Where(x => imageIds.Contains(x.FingerprintImageId) && (x.Provider == "GoogleDrive" || x.Provider == "Firestore"))
                .ToListAsync(ct);

        var recordMap = records
            .GroupBy(x => (x.FingerprintImageId, x.Provider))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.LastAttemptAtUtc).First());

        candidateCount = images.Count;
        queue = new List<SyncCandidate>(images.Count);

        foreach (var image in images)
        {
            recordMap.TryGetValue((image.Id, "GoogleDrive"), out var driveRecord);
            recordMap.TryGetValue((image.Id, "Firestore"), out var firestoreRecord);

            var driveReady = string.Equals(image.SyncStatus, "Synced", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(image.DriveFileId)
                && driveRecord?.Status == "Synced";
            var firestoreReady = firestoreRecord?.Status == "Synced";

            if (driveReady) driveReadyCount++;
            if (firestoreReady) firestoreReadyCount++;

            if (driveReady && firestoreReady)
                continue;

            if (firestoreRecord?.Status == "Failed"
                && firestoreRecord.LastAttemptAtUtc.HasValue
                && firestoreRecord.LastAttemptAtUtc.Value > retryCutoff
                && driveReady)
            {
                retrySkippedCount++;
                continue;
            }

            if (driveRecord?.Status == "Failed"
                && driveRecord.LastAttemptAtUtc.HasValue
                && driveRecord.LastAttemptAtUtc.Value > retryCutoff)
            {
                retrySkippedCount++;
                continue;
            }

            queue.Add(new SyncCandidate(image, driveRecord, firestoreRecord));
        }
    }

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Scan: candidates={candidateCount}, driveSynced={driveReadyCount}, firestoreSynced={firestoreReadyCount}, queued={queue.Count}, retrySkipped={retrySkippedCount}");

    int synced = 0;
    foreach (var candidate in queue)
    {
        using var scope = root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var imageBefore = await db.FingerprintImages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == candidate.Image.Id, ct);
            if (imageBefore is null)
                continue;

            if (!string.Equals(imageBefore.SyncStatus, "Synced", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(imageBefore.DriveFileId))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Drive sync start: {candidate.Image.Id}");
                var driveSync = scope.ServiceProvider.GetRequiredService<GoogleDriveSyncService>();
                await driveSync.SyncFingerprintAsync(candidate.Image.Id, ct);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Drive sync done: {candidate.Image.Id}");
            }

            var image = await db.FingerprintImages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == candidate.Image.Id, ct);
            if (image is null || string.IsNullOrWhiteSpace(image.DriveFileId))
                throw new InvalidOperationException("Fingerprint is not Drive-synced yet.");

            var person = await db.Persons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == image.PersonId, ct)
                ?? throw new KeyNotFoundException("Person not found.");

            var firestoreRecord = await db.CloudSyncRecords
                .FirstOrDefaultAsync(x => x.FingerprintImageId == candidate.Image.Id && x.Provider == "Firestore", ct);

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
                    FingerprintImageId = candidate.Image.Id,
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
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Firestore sync start: {person.PersonCode} / {image.FingerCode} / {image.Position} / #{image.SequenceNo}");
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
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sync {candidate.Image.Id} failed: {ex.Message}");
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

sealed record SyncImageSnapshot(
    Guid Id,
    Guid PersonId,
    string FingerCode,
    string Position,
    int SequenceNo,
    DateTime CapturedAtUtc,
    string SyncStatus,
    string? DriveFileId,
    string EncryptedFileName);

sealed record SyncCandidate(
    SyncImageSnapshot Image,
    CloudSyncRecord? DriveRecord,
    CloudSyncRecord? FirestoreRecord);