using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OfflineFingerprint.Collector.Data;
using OfflineFingerprint.Collector.Services;

var collectorPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OfflineFingerprint.Collector"));
var databasePath = Path.Combine(collectorPath, "data", "fingerprint.db");
var driveCredentialsPath = Path.GetFullPath(Path.Combine(collectorPath, "..", "Collector.Agent", "secrets", "google-drive", "credentials.json"));

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Firebase:ProjectId"] = "fingerprintsystemmbt",
        ["Firebase:BucketName"] = "fingerprintsystemmbt.firebasestorage.app",
        ["Firebase:CredentialsPath"] = driveCredentialsPath
    })
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddHttpClient();
services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
services.AddSingleton<FirestoreMetadataService>();

await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
var firestore = scope.ServiceProvider.GetRequiredService<FirestoreMetadataService>();

var rows = await db.FingerprintImages
    .AsNoTracking()
    .Join(db.Persons.AsNoTracking(), f => f.PersonId, p => p.Id, (f, p) => new { Fingerprint = f, Person = p })
    .OrderBy(x => x.Fingerprint.CapturedAtUtc)
    .ToListAsync();

Console.WriteLine("FingerprintSystem Firestore Storage Metadata Backfill");
Console.WriteLine($"Rows found: {rows.Count}");

var success = 0;
var failed = 0;
foreach (var row in rows)
{
    try
    {
        var drive = await db.CloudSyncRecords.AsNoTracking()
            .FirstOrDefaultAsync(x => x.FingerprintImageId == row.Fingerprint.Id && x.Provider == "GoogleDrive");
        var driveFileId = drive?.DriveFileId ?? row.Fingerprint.DriveFileId;
        if (string.IsNullOrWhiteSpace(driveFileId))
        {
            Console.WriteLine($"SKIP {row.Person.PersonCode} {row.Fingerprint.Id}: no DriveFileId");
            continue;
        }

        var driveLink = drive?.DriveWebViewLink ?? $"https://drive.google.com/file/d/{driveFileId}/view?usp=drivesdk";
        await firestore.UpsertFingerprintAsync(row.Person, row.Fingerprint, driveFileId, driveLink, CancellationToken.None);
        success++;
        Console.WriteLine($"OK {row.Person.PersonCode} / {row.Fingerprint.FingerCode} / {row.Fingerprint.Position} / #{row.Fingerprint.SequenceNo}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {row.Person.PersonCode} / {row.Fingerprint.Id}: {ex.Message}");
    }
}

Console.WriteLine($"Backfill complete. Success={success}, Failed={failed}");
