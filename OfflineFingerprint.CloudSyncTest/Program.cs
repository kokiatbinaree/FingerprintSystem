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
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<LocalKeyService>();
        services.AddSingleton<FingerprintStorageService>();
        services.AddHttpClient();
        services.AddSingleton<GoogleDriveSyncService>();
    })
    .Build();

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
var storage = scope.ServiceProvider.GetRequiredService<FingerprintStorageService>();
var sync = scope.ServiceProvider.GetRequiredService<GoogleDriveSyncService>();

Console.WriteLine($"Collector path: {collectorPath}");
Console.WriteLine($"SQLite path: {databasePath}");

await db.Database.EnsureCreatedAsync();

// Existing databases may predate the CloudSyncRecord/DriveFileId additions.
// Apply only the missing schema elements; never delete or recreate the database.
await EnsureDriveFileIdColumnAsync(db);
await EnsureCloudSyncSchemaAsync(db);

var fingerprint = await db.FingerprintImages
    .AsNoTracking()
    .OrderBy(x => x.CapturedAtUtc)
    .FirstOrDefaultAsync();

if (fingerprint is null)
{
    var person = await db.Persons.FirstOrDefaultAsync(x => x.PersonCode == "SYNC-TEST");
    if (person is null)
    {
        person = new Person
        {
            Id = Guid.NewGuid(),
            PersonCode = "SYNC-TEST",
            FirstName = "Cloud",
            LastName = "Sync Test",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        db.Persons.Add(person);
        await db.SaveChangesAsync();
    }

    const int width = 320;
    const int height = 480;
    var gray = new byte[width * height];
    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int v = 210;
            int dx = x - width / 2;
            int dy = y - height / 2;
            if ((dx * dx) / 400 + (dy * dy) / 1600 < 25 && ((x + y) % 17 < 3)) v = 70;
            gray[y * width + x] = (byte)v;
        }
    }

    var stored = await storage.SaveGrayAsync(person.Id, "L3", "right", gray, width, height, CancellationToken.None);
    fingerprint = new FingerprintImage
    {
        Id = Guid.NewGuid(),
        PersonId = person.Id,
        FingerCode = "L3",
        Position = "right",
        SequenceNo = 1,
        EncryptedFileName = stored.FileName,
        Width = width,
        Height = height,
        CapturedAtUtc = DateTime.UtcNow,
        SyncStatus = "Pending"
    };
    db.FingerprintImages.Add(fingerprint);
    await db.SaveChangesAsync();
    Console.WriteLine($"Created local test fingerprint: {fingerprint.Id}");
}

Console.WriteLine($"Syncing fingerprint {fingerprint.Id} ...");
var result = await sync.SyncFingerprintAsync(fingerprint.Id, CancellationToken.None);
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

var saved = await db.CloudSyncRecords
    .AsNoTracking()
    .FirstAsync(x => x.FingerprintImageId == fingerprint.Id && x.Provider == "GoogleDrive");
Console.WriteLine($"Cloud status: {saved.Status}");
Console.WriteLine($"Drive file id: {saved.DriveFileId}");
Console.WriteLine($"Drive link: {saved.DriveWebViewLink}");

static async Task EnsureDriveFileIdColumnAsync(AppDbContext db)
{
    var hasColumn = false;
    await using var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
        await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA table_info(FingerprintImages);";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        if (string.Equals(reader.GetString(1), "DriveFileId", StringComparison.OrdinalIgnoreCase))
        {
            hasColumn = true;
            break;
        }
    }

    if (!hasColumn)
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE FingerprintImages ADD COLUMN DriveFileId TEXT NULL;");
}

static async Task EnsureCloudSyncSchemaAsync(AppDbContext db)
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
);");

    await db.Database.ExecuteSqlRawAsync(@"
CREATE UNIQUE INDEX IF NOT EXISTS IX_CloudSyncRecords_FingerprintImageId_Provider
ON CloudSyncRecords (FingerprintImageId, Provider);");
}
