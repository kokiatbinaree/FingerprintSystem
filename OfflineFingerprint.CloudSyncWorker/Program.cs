using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OfflineFingerprint.CloudSyncWorker;
using OfflineFingerprint.Collector.Data;
using OfflineFingerprint.Collector.Models;
using OfflineFingerprint.Collector.Services;

var collectorPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OfflineFingerprint.Collector"));
var dataPath = Path.Combine(collectorPath, "data");
Directory.CreateDirectory(dataPath);
var databasePath = Path.Combine(dataPath, "fingerprint.db");
var firebaseCredentialsPath = Path.GetFullPath(Path.Combine(collectorPath, "..", "Collector.Agent", "secrets", "firebase", "service-account.json"));
var driveCredentialsPath = Path.GetFullPath(Path.Combine(collectorPath, "..", "Collector.Agent", "secrets", "google-drive", "credentials.json"));

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Futronic:CollectorAgentBaseUrl"] = "http://127.0.0.1:15271",
        ["Firebase:ProjectId"] = "fingerprintsystemmbt",
        ["Firebase:BucketName"] = "fingerprintsystemmbt.firebasestorage.app",
        ["Firebase:CredentialsPath"] = firebaseCredentialsPath,
        ["GoogleDrive:CredentialsPath"] = driveCredentialsPath
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
        services.AddSingleton<FirebaseStorageSyncService>();
    })
    .Build();

Console.WriteLine("FingerprintSystem Cloud Sync Worker");
Console.WriteLine($"Firestore project: {configuration["Firebase:ProjectId"]}");
Console.WriteLine($"Firebase Storage bucket: {configuration["Firebase:BucketName"]}");
Console.WriteLine("Watching for Drive, Firestore and Firebase Storage sync. Press Ctrl+C to stop.");

await EnsureSchemaAsync(host.Services, CancellationToken.None);

using var stopCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopCts.Cancel(); };

while (!stopCts.IsCancellationRequested)
{
    try
    {
        var synced = await ProcessPendingAsync(host.Services, stopCts.Token);
        if (synced > 0)
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Fully synced {synced} fingerprint(s) to Drive/Firestore/Storage.");
    }
    catch (OperationCanceledException) when (stopCts.IsCancellationRequested) { break; }
    catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Worker error: {ex.Message}"); }

    try { await Task.Delay(TimeSpan.FromSeconds(10), stopCts.Token); }
    catch (OperationCanceledException) when (stopCts.IsCancellationRequested) { break; }
}

Console.WriteLine("Cloud Sync Worker stopped.");

static async Task<int> ProcessPendingAsync(IServiceProvider root, CancellationToken ct)
{
    List<Guid> queue;
    int candidateCount;
    int driveReady = 0, firestoreReady = 0, storageReady = 0;

    using (var scope = root.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var images = await db.FingerprintImages.AsNoTracking().OrderBy(x => x.CapturedAtUtc).Take(50).ToListAsync(ct);
        var ids = images.Select(x => x.Id).ToHashSet();
        var records = await db.CloudSyncRecords.AsNoTracking()
            .Where(x => ids.Contains(x.FingerprintImageId) && (x.Provider == "GoogleDrive" || x.Provider == "Firestore" || x.Provider == "FirebaseStorage"))
            .ToListAsync(ct);
        var map = records.GroupBy(x => (x.FingerprintImageId, x.Provider)).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.LastAttemptAtUtc).First());

        queue = new List<Guid>();
        foreach (var image in images)
        {
            map.TryGetValue((image.Id, "GoogleDrive"), out var drive);
            map.TryGetValue((image.Id, "Firestore"), out var fire);
            map.TryGetValue((image.Id, "FirebaseStorage"), out var storage);
            var d = image.SyncStatus.Equals("Synced", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(image.DriveFileId) && drive?.Status == "Synced";
            var f = fire?.Status == "Synced";
            var s = storage?.Status == "Synced";
            if (d) driveReady++; if (f) firestoreReady++; if (s) storageReady++;
            if (!(d && f && s)) queue.Add(image.Id);
        }
        candidateCount = images.Count;
    }

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Scan: candidates={candidateCount}, driveSynced={driveReady}, firestoreSynced={firestoreReady}, storageSynced={storageReady}, queued={queue.Count}");

    var completed = 0;
    foreach (var id in queue)
    {
        using var scope = root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var image = await db.FingerprintImages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (image is null) continue;
        var person = await db.Persons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == image.PersonId, ct);
        if (person is null) continue;

        try
        {
            var drive = await db.CloudSyncRecords.FirstOrDefaultAsync(x => x.FingerprintImageId == id && x.Provider == "GoogleDrive", ct);
            if (drive?.Status != "Synced" || string.IsNullOrWhiteSpace(image.DriveFileId))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Drive sync start: {id}");
                await scope.ServiceProvider.GetRequiredService<GoogleDriveSyncService>().SyncFingerprintAsync(id, ct);
                image = await db.FingerprintImages.FirstOrDefaultAsync(x => x.Id == id, ct) ?? image;
            }
        }
        catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Drive sync failed: {id}: {ex.Message}"); }

        try
        {
            var storage = await db.CloudSyncRecords.FirstOrDefaultAsync(x => x.FingerprintImageId == id && x.Provider == "FirebaseStorage", ct);
            if (storage is null) { storage = new CloudSyncRecord { Id = Guid.NewGuid(), FingerprintImageId = id, Provider = "FirebaseStorage" }; db.CloudSyncRecords.Add(storage); }
            if (storage.Status != "Synced")
            {
                storage.Status = "Syncing"; storage.AttemptCount++; storage.LastAttemptAtUtc = DateTime.UtcNow; storage.LastError = ""; await db.SaveChangesAsync(ct);
                var result = await scope.ServiceProvider.GetRequiredService<FirebaseStorageSyncService>().UploadAsync(person, image, ct);
                storage.Status = "Synced"; storage.DriveFileId = result.ObjectName; storage.DriveWebViewLink = result.StorageUri; storage.SyncedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Firebase Storage synced: {person.PersonCode} / {image.FingerCode} / {image.Position} / #{image.SequenceNo}");
            }
        }
        catch (Exception ex)
        {
            var storage = await db.CloudSyncRecords.FirstOrDefaultAsync(x => x.FingerprintImageId == id && x.Provider == "FirebaseStorage", ct);
            if (storage is null) { storage = new CloudSyncRecord { Id = Guid.NewGuid(), FingerprintImageId = id, Provider = "FirebaseStorage" }; db.CloudSyncRecords.Add(storage); }
            storage.Status = "Failed"; storage.LastError = ex.Message; storage.LastAttemptAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Firebase Storage failed: {id}: {ex.Message}");
        }

        try
        {
            image = await db.FingerprintImages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (image is null || string.IsNullOrWhiteSpace(image.DriveFileId)) throw new InvalidOperationException("Fingerprint is not Drive-synced yet.");
            var fire = await db.CloudSyncRecords.FirstOrDefaultAsync(x => x.FingerprintImageId == id && x.Provider == "Firestore", ct);
            if (fire is null) { fire = new CloudSyncRecord { Id = Guid.NewGuid(), FingerprintImageId = id, Provider = "Firestore" }; db.CloudSyncRecords.Add(fire); }
            if (fire.Status != "Synced")
            {
                fire.Status = "Syncing"; fire.AttemptCount++; fire.LastAttemptAtUtc = DateTime.UtcNow; fire.LastError = ""; await db.SaveChangesAsync(ct);
                var link = $"https://drive.google.com/file/d/{image.DriveFileId}/view?usp=drivesdk";
                await scope.ServiceProvider.GetRequiredService<FirestoreMetadataService>().UpsertFingerprintAsync(person, image, image.DriveFileId, link, ct);
                fire.Status = "Synced"; fire.DriveFileId = image.DriveFileId; fire.DriveWebViewLink = link; fire.SyncedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Firestore synced: {person.PersonCode} / {image.FingerCode} / {image.Position} / #{image.SequenceNo}");
            }
        }
        catch (Exception ex)
        {
            var fire = await db.CloudSyncRecords.FirstOrDefaultAsync(x => x.FingerprintImageId == id && x.Provider == "Firestore", ct);
            if (fire is null) { fire = new CloudSyncRecord { Id = Guid.NewGuid(), FingerprintImageId = id, Provider = "Firestore" }; db.CloudSyncRecords.Add(fire); }
            fire.Status = "Failed"; fire.LastError = ex.Message; fire.LastAttemptAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Firestore failed: {id}: {ex.Message}");
        }

        var finalDrive = await db.CloudSyncRecords.AsNoTracking().FirstOrDefaultAsync(x => x.FingerprintImageId == id && x.Provider == "GoogleDrive", ct);
        var finalFire = await db.CloudSyncRecords.AsNoTracking().FirstOrDefaultAsync(x => x.FingerprintImageId == id && x.Provider == "Firestore", ct);
        var finalStorage = await db.CloudSyncRecords.AsNoTracking().FirstOrDefaultAsync(x => x.FingerprintImageId == id && x.Provider == "FirebaseStorage", ct);
        if (finalDrive?.Status == "Synced" && finalFire?.Status == "Synced" && finalStorage?.Status == "Synced") completed++;
    }
    return completed;
}

static async Task EnsureSchemaAsync(IServiceProvider root, CancellationToken ct)
{
    using var scope = root.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync(ct);
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
    CONSTRAINT FK_CloudSyncRecords_FingerprintImageId_FingerprintImages FOREIGN KEY (FingerprintImageId) REFERENCES FingerprintImages (Id) ON DELETE CASCADE
);", ct);
    await db.Database.ExecuteSqlRawAsync(@"CREATE UNIQUE INDEX IF NOT EXISTS IX_CloudSyncRecords_FingerprintImageId_Provider ON CloudSyncRecords (FingerprintImageId, Provider);", ct);
}
