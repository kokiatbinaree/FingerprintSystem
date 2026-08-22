using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OfflineFingerprint.Collector.Data;
using OfflineFingerprint.Collector.Models;
using OfflineFingerprint.Collector.Services;

var collectorPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OfflineFingerprint.Collector"));
var dataPath = Path.Combine(collectorPath, "data");
Directory.CreateDirectory(dataPath);
var databasePath = Path.Combine(dataPath, "fingerprint.db");
var credentialsPath = Path.Combine(collectorPath, "secrets", "google-drive", "credentials.json");

Console.WriteLine($"Collector path: {collectorPath}");
Console.WriteLine($"SQLite path: {databasePath}");
Console.WriteLine($"Google credentials: {credentialsPath}");
Console.WriteLine("Firestore project: fingerprintsystemmbt");

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Firebase:ProjectId"] = "fingerprintsystemmbt",
        ["Firebase:CredentialsPath"] = credentialsPath
    })
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddHttpClient();
services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
services.AddSingleton<FirestoreMetadataService>();

using var provider = services.BuildServiceProvider();

var db = provider.GetRequiredService<AppDbContext>();
await db.Database.EnsureCreatedAsync();

var image = await db.FingerprintImages
    .AsNoTracking()
    .Where(x => x.SyncStatus == "Synced" && !string.IsNullOrWhiteSpace(x.DriveFileId))
    .OrderBy(x => x.CapturedAtUtc)
    .FirstOrDefaultAsync();

if (image is null)
{
    Console.WriteLine("ไม่พบ fingerprint ที่ Sync กับ Google Drive แล้วสำหรับทดสอบ Firestore");
    return;
}

var person = await db.Persons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == image.PersonId);
if (person is null)
{
    Console.WriteLine($"ไม่พบ Person สำหรับ fingerprint {image.Id}");
    return;
}

Console.WriteLine($"Testing fingerprint: {image.Id}");
Console.WriteLine($"Person: {person.PersonCode} {person.FirstName} {person.LastName}");
Console.WriteLine($"Finger: {image.FingerCode} / {image.Position} / #{image.SequenceNo}");
Console.WriteLine($"DriveFileId: {image.DriveFileId}");

var firestore = provider.GetRequiredService<FirestoreMetadataService>();
var written = await firestore.UpsertFingerprintAsync(
    person,
    image,
    image.DriveFileId!,
    $"https://drive.google.com/file/d/{image.DriveFileId}/view?usp=drivesdk");

Console.WriteLine("Firestore write succeeded.");
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(written, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

using var read = await firestore.GetFingerprintAsync(image.Id);
Console.WriteLine("Firestore read succeeded.");
Console.WriteLine(read.RootElement.GetRawText());
