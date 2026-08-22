using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using OfflineFingerprint.Collector.Models;
using OfflineFingerprint.Collector.Services;

namespace OfflineFingerprint.CloudSyncWorker;

public sealed class FirebaseStorageSyncService
{
    private readonly FingerprintStorageService _storage;
    private readonly IConfiguration _configuration;

    public FirebaseStorageSyncService(FingerprintStorageService storage, IConfiguration configuration)
    {
        _storage = storage;
        _configuration = configuration;
    }

    public async Task<(string ObjectName, string StorageUri)> UploadAsync(
        Person person,
        FingerprintImage image,
        CancellationToken ct)
    {
        var credentialsPath = _configuration["Firebase:CredentialsPath"]
            ?? throw new InvalidOperationException("Firebase:CredentialsPath is not configured.");
        var bucketName = _configuration["Firebase:BucketName"]
            ?? throw new InvalidOperationException("Firebase:BucketName is not configured.");

        if (!File.Exists(credentialsPath))
            throw new FileNotFoundException("Firebase service-account.json not found.", credentialsPath);

        var png = await _storage.ReadDecryptedAsync(image.EncryptedFileName, ct);
        var objectName = $"fingerprints/{person.PersonCode}/{image.Id:N}/{image.FingerCode}-{image.Position}-{image.SequenceNo:00}.png";

        var credential = GoogleCredential.FromFile(credentialsPath);
        var client = await StorageClient.CreateAsync(credential);

        await using var stream = new MemoryStream(png, writable: false);
        await client.UploadObjectAsync(
            bucketName,
            objectName,
            "image/png",
            stream,
            new UploadObjectOptions { IfGenerationMatch = 0 },
            ct);

        return (objectName, $"gs://{bucketName}/{objectName}");
    }
}
