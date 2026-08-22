using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfflineFingerprint.Collector.Data;
using OfflineFingerprint.Collector.Services;

const string projectId = "fingerprintsystemmbt";
const string bucketName = "fingerprintsystemmbt.firebasestorage.app";

var collectorPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OfflineFingerprint.Collector"));
var databasePath = Path.Combine(collectorPath, "data", "fingerprint.db");
var credentialsPath = Path.GetFullPath(Path.Combine(collectorPath, "..", "Collector.Agent", "secrets", "google-drive", "credentials.json"));

Console.WriteLine("FingerprintSystem Firebase Storage Test");
Console.WriteLine($"Project: {projectId}");
Console.WriteLine($"Bucket: {bucketName}");
Console.WriteLine($"Collector path: {collectorPath}");
Console.WriteLine($"SQLite: {databasePath}");
Console.WriteLine($"Credentials: {credentialsPath}");

if (!File.Exists(credentialsPath))
    throw new FileNotFoundException("ไม่พบ Google credentials", credentialsPath);

var services = new ServiceCollection();
services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
services.AddSingleton<LocalKeyService>();
services.AddSingleton<FingerprintStorageService>(sp =>
{
    var env = new TestHostEnvironment(collectorPath);
    return new FingerprintStorageService(env, sp.GetRequiredService<LocalKeyService>());
});

await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
var storage = scope.ServiceProvider.GetRequiredService<FingerprintStorageService>();

var target = await db.FingerprintImages
    .AsNoTracking()
    .Join(db.Persons.AsNoTracking(), f => f.PersonId, p => p.Id, (f, p) => new { Fingerprint = f, Person = p })
    .Where(x => x.Person.PersonCode == "P000002")
    .OrderBy(x => x.Fingerprint.CapturedAtUtc)
    .FirstOrDefaultAsync();

if (target is null)
    throw new InvalidOperationException("ไม่พบลายนิ้วมือของ P000002");

Console.WriteLine($"Testing fingerprint: {target.Fingerprint.Id}");
Console.WriteLine($"Person: {target.Person.PersonCode} {target.Person.FirstName} {target.Person.LastName}");
Console.WriteLine($"Finger: {target.Fingerprint.FingerCode} / {target.Fingerprint.Position} / #{target.Fingerprint.SequenceNo}");

var png = await storage.ReadDecryptedAsync(target.Fingerprint.EncryptedFileName, CancellationToken.None);
var objectName = $"fingerprints/{target.Person.PersonCode}/{target.Fingerprint.Id:N}/{target.Fingerprint.FingerCode}-{target.Fingerprint.Position}-{target.Fingerprint.SequenceNo:00}.png";

var credential = GoogleCredential.FromFile(credentialsPath);
var client = await StorageClient.CreateAsync(credential);

using var stream = new MemoryStream(png, writable: false);
var uploaded = await client.UploadObjectAsync(
    bucketName,
    objectName,
    "image/png",
    stream,
    cancellationToken: CancellationToken.None);

Console.WriteLine("Firebase Storage upload succeeded.");
Console.WriteLine($"Bucket: {uploaded.Bucket}");
Console.WriteLine($"Object: {uploaded.Name}");
Console.WriteLine($"Size: {uploaded.Size} bytes");
Console.WriteLine($"gs://{uploaded.Bucket}/{uploaded.Name}");

sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public TestHostEnvironment(string contentRootPath)
    {
        ContentRootPath = contentRootPath;
        ContentRootFileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(contentRootPath);
    }

    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "OfflineFingerprint.FirebaseStorageTest";
    public string ContentRootPath { get; set; }
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
}
