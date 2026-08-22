using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using OfflineFingerprint.Collector.Models;

namespace OfflineFingerprint.Collector.Services;

public sealed class FirestoreMetadataService
{
    private const string DatastoreScope = "https://www.googleapis.com/auth/datastore";
    private readonly SemaphoreSlim gate = new(1, 1);
    private UserCredential? credential;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _projectId;
    private readonly string _credentialsPath;
    private readonly string _tokenPath;
    private readonly string _storageBucket;

    public FirestoreMetadataService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _projectId = configuration["Firebase:ProjectId"] ?? "fingerprintsystemmbt";
        _storageBucket = configuration["Firebase:BucketName"] ?? "fingerprintsystemmbt.firebasestorage.app";

        var configured = configuration["Firestore:CredentialsPath"]
            ?? configuration["GoogleDrive:CredentialsPath"]
            ?? configuration["Firebase:CredentialsPath"];
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "secrets", "google-drive", "credentials.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Collector.Agent", "secrets", "google-drive", "credentials.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Collector.Agent", "secrets", "google-drive", "credentials.json")
        };

        _credentialsPath = candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists)
            ?? Path.GetFullPath(candidates.First(p => !string.IsNullOrWhiteSpace(p))!);

        _tokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FingerprintSystemMBT", "Firestore", "token");
    }

    public async Task<object> UpsertFingerprintAsync(
        Person person,
        FingerprintImage image,
        string driveFileId,
        string driveWebViewLink,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driveFileId))
            throw new ArgumentException("Drive file id is required.", nameof(driveFileId));

        var documentId = image.Id.ToString();
        var url = BuildDocumentUrl("fingerprints", documentId);
        var storageObject = BuildStorageObjectName(person, image);
        var storageUri = $"gs://{_storageBucket}/{storageObject}";

        var body = new
        {
            fields = new Dictionary<string, object>
            {
                ["fingerprintId"] = StringField(image.Id.ToString()),
                ["personId"] = StringField(person.Id.ToString()),
                ["personCode"] = StringField(person.PersonCode),
                ["firstName"] = StringField(person.FirstName),
                ["lastName"] = StringField(person.LastName),
                ["fingerCode"] = StringField(image.FingerCode),
                ["position"] = StringField(image.Position),
                ["sequenceNo"] = IntegerField(image.SequenceNo),
                ["capturedAtUtc"] = TimestampField(image.CapturedAtUtc),
                ["driveFileId"] = StringField(driveFileId),
                ["driveWebViewLink"] = StringField(driveWebViewLink),
                ["firebaseStorageBucket"] = StringField(_storageBucket),
                ["firebaseStorageObject"] = StringField(storageObject),
                ["firebaseStorageUri"] = StringField(storageUri),
                ["syncStatus"] = StringField("Synced")
            }
        };

        using var response = await SendAsync(HttpMethod.Patch, url, body, ct);
        return await ParseDocumentAsync(response, ct);
    }

    public async Task<JsonDocument> GetFingerprintAsync(Guid fingerprintId, CancellationToken ct = default)
    {
        var url = BuildDocumentUrl("fingerprints", fingerprintId.ToString());
        using var response = await SendAsync(HttpMethod.Get, url, null, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(text);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(ct);
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException($"Firestore request failed: HTTP {status} {error}");
        }

        return response;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (credential is not null)
            return await credential.GetAccessTokenForRequestAsync(null, ct);

        await gate.WaitAsync(ct);
        try
        {
            if (credential is not null)
                return await credential.GetAccessTokenForRequestAsync(null, ct);

            if (!File.Exists(_credentialsPath))
                throw new FileNotFoundException($"ไม่พบ credentials.json สำหรับ Firestore: {_credentialsPath}", _credentialsPath);

            await using var stream = File.OpenRead(_credentialsPath);
            var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                [DatastoreScope],
                "firestore",
                ct,
                new FileDataStore(_tokenPath, true));

            return await credential.GetAccessTokenForRequestAsync(null, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private string BuildDocumentUrl(string collection, string documentId)
        => $"https://firestore.googleapis.com/v1/projects/{Uri.EscapeDataString(_projectId)}/databases/(default)/documents/{collection}/{Uri.EscapeDataString(documentId)}";

    private static string BuildStorageObjectName(Person person, FingerprintImage image)
        => $"fingerprints/{person.PersonCode}/{image.Id:N}/{image.FingerCode}-{image.Position}-{image.SequenceNo:00}.png";

    private static object StringField(string value) => new { stringValue = value ?? string.Empty };
    private static object IntegerField(int value) => new { integerValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    private static object TimestampField(DateTime value) => new { timestampValue = new DateTimeOffset(value.ToUniversalTime()).ToString("O") };

    private static async Task<object> ParseDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText())!;
    }
}
