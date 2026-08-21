using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;

namespace Collector.Agent;

public sealed class GoogleDriveStorage
{
    private static readonly string[] Scopes = { DriveService.Scope.DriveFile };
    private readonly SemaphoreSlim gate = new(1, 1);
    private DriveService? drive;

    public string CredentialsPath
    {
        get
        {
            var devPath = Path.Combine(Directory.GetCurrentDirectory(), "secrets", "google-drive", "credentials.json");
            return File.Exists(devPath)
                ? devPath
                : Path.Combine(AppContext.BaseDirectory, "secrets", "google-drive", "credentials.json");
        }
    }

    public string TokenPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FingerprintSystemMBT", "GoogleDrive", "token");

    public object GetStatus() => new
    {
        configured = File.Exists(CredentialsPath),
        credentialsPath = CredentialsPath,
        authenticated = drive is not null
    };

    private async Task<DriveService> GetDriveAsync(CancellationToken cancellationToken)
    {
        if (drive is not null) return drive;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (drive is not null) return drive;
            if (!File.Exists(CredentialsPath))
                throw new FileNotFoundException($"ไม่พบ credentials.json กรุณาวางไฟล์ที่ {CredentialsPath}", CredentialsPath);

            await using var stream = File.OpenRead(CredentialsPath);
            var secrets = (await GoogleClientSecrets.LoadAsync(stream, cancellationToken)).Secrets;
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                Scopes,
                "fingerprintsystem",
                cancellationToken,
                new Google.Apis.Util.Store.FileDataStore(TokenPath, true));

            drive = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "FingerprintSystemMBT Collector"
            });
            return drive;
        }
        finally { gate.Release(); }
    }

    public async Task<object> UploadTestAsync(CancellationToken cancellationToken = default)
    {
        var service = await GetDriveAsync(cancellationToken);
        var folder = await EnsureFolderAsync(service, "FingerprintSystem-Test", null, cancellationToken);
        var content = $"FingerprintSystem Google Drive test\r\nUTC: {DateTimeOffset.UtcNow:O}\r\n";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var file = new Google.Apis.Drive.v3.Data.File
        {
            Name = $"collector-test-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt",
            Parents = new List<string> { folder },
            MimeType = "text/plain"
        };
        var request = service.Files.Create(file, stream, "text/plain");
        request.Fields = "id,name,webViewLink,parents";
        await request.UploadAsync(cancellationToken);
        var uploaded = request.ResponseBody ?? throw new InvalidOperationException("Google Drive ไม่ส่งผลลัพธ์กลับมา");
        return new { uploaded.Id, uploaded.Name, uploaded.WebViewLink };
    }

    private static async Task<string> EnsureFolderAsync(DriveService service, string name, string? parentId, CancellationToken cancellationToken)
    {
        var escaped = name.Replace("'", "\\'");
        var q = $"name = '{escaped}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        if (!string.IsNullOrWhiteSpace(parentId)) q += $" and '{parentId}' in parents";
        var search = service.Files.List();
        search.Q = q;
        search.Fields = "files(id,name,parents)";
        search.PageSize = 10;
        var result = await search.ExecuteAsync(cancellationToken);
        var existing = result.Files?.FirstOrDefault();
        if (existing?.Id is not null) return existing.Id;
        var folder = new Google.Apis.Drive.v3.Data.File { Name = name, MimeType = "application/vnd.google-apps.folder" };
        if (!string.IsNullOrWhiteSpace(parentId)) folder.Parents = new List<string> { parentId };
        var create = service.Files.Create(folder);
        create.Fields = "id,name,parents";
        var created = await create.ExecuteAsync(cancellationToken);
        return created.Id ?? throw new InvalidOperationException("สร้างโฟลเดอร์ Google Drive ไม่สำเร็จ");
    }
}
