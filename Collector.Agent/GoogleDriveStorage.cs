using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;
using IOFile = System.IO.File;

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
            return IOFile.Exists(devPath) ? devPath : Path.Combine(AppContext.BaseDirectory, "secrets", "google-drive", "credentials.json");
        }
    }

    public string TokenPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FingerprintSystemMBT", "GoogleDrive", "token");

    public object GetStatus() => new { configured = IOFile.Exists(CredentialsPath), credentialsPath = CredentialsPath, authenticated = drive is not null };

    private async Task<DriveService> GetDriveAsync(CancellationToken cancellationToken)
    {
        if (drive is not null) return drive;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (drive is not null) return drive;
            if (!IOFile.Exists(CredentialsPath)) throw new FileNotFoundException($"ไม่พบ credentials.json กรุณาวางไฟล์ที่ {CredentialsPath}", CredentialsPath);
            await using var stream = IOFile.OpenRead(CredentialsPath);
            var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(secrets, Scopes, "fingerprintsystem", cancellationToken, new Google.Apis.Util.Store.FileDataStore(TokenPath, true));
            drive = new DriveService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "FingerprintSystemMBT Collector" });
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
        var file = new DriveFile { Name = $"collector-test-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt", Parents = [folder], MimeType = "text/plain" };
        return await UploadStreamAsync(service, file, stream, "text/plain", cancellationToken);
    }

    public async Task<object> UploadPngAsync(byte[] png, string fileName, CancellationToken cancellationToken = default)
        => await UploadPngAsync(png, fileName, ["FingerprintSystem-Test"], cancellationToken);

    public async Task<object> UploadPngAsync(byte[] png, string fileName, IReadOnlyList<string> folderPath, CancellationToken cancellationToken = default)
    {
        if (png.Length == 0) throw new ArgumentException("PNG data is empty.", nameof(png));
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("File name is required.", nameof(fileName));
        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";
        var service = await GetDriveAsync(cancellationToken);
        var folder = await EnsureFolderTreeAsync(service, folderPath, cancellationToken);
        await using var stream = new MemoryStream(png, writable: false);
        var file = new DriveFile { Name = fileName, Parents = [folder], MimeType = "image/png" };
        return await UploadStreamAsync(service, file, stream, "image/png", cancellationToken);
    }

    private static async Task<object> UploadStreamAsync(DriveService service, DriveFile file, Stream stream, string mimeType, CancellationToken cancellationToken)
    {
        var request = service.Files.Create(file, stream, mimeType);
        request.Fields = "id,name,webViewLink,parents,mimeType,size";
        await request.UploadAsync(cancellationToken);
        var uploaded = request.ResponseBody ?? throw new InvalidOperationException("Google Drive ไม่ส่งผลลัพธ์กลับมา");
        return new { uploaded.Id, uploaded.Name, uploaded.WebViewLink, uploaded.MimeType, uploaded.Size, FolderId = uploaded.Parents?.FirstOrDefault() };
    }

    private static async Task<string> EnsureFolderTreeAsync(DriveService service, IReadOnlyList<string> folderPath, CancellationToken cancellationToken)
    {
        string? parentId = null;
        foreach (var rawName in folderPath)
        {
            var name = rawName.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            parentId = await EnsureFolderAsync(service, name, parentId, cancellationToken);
        }
        return parentId ?? throw new InvalidOperationException("Google Drive folder path is empty.");
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
        var folder = new DriveFile { Name = name, MimeType = "application/vnd.google-apps.folder" };
        if (!string.IsNullOrWhiteSpace(parentId)) folder.Parents = [parentId];
        var create = service.Files.Create(folder);
        create.Fields = "id,name,parents";
        var created = await create.ExecuteAsync(cancellationToken);
        return created.Id ?? throw new InvalidOperationException("สร้างโฟลเดอร์ Google Drive ไม่สำเร็จ");
    }
}
