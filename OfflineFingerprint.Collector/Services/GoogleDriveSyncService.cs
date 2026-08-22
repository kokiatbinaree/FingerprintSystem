using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using OfflineFingerprint.Collector.Data;
using OfflineFingerprint.Collector.Models;

namespace OfflineFingerprint.Collector.Services;

public sealed class GoogleDriveSyncService
{
    private readonly AppDbContext _db;
    private readonly FingerprintStorageService _storage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public GoogleDriveSyncService(
        AppDbContext db,
        FingerprintStorageService storage,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _db = db;
        _storage = storage;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<object> SyncFingerprintAsync(Guid fingerprintId, CancellationToken ct)
    {
        var image = await _db.FingerprintImages.FindAsync([fingerprintId], ct)
            ?? throw new KeyNotFoundException("Fingerprint image not found.");

        var record = await _db.Set<CloudSyncRecord>()
            .FirstOrDefaultAsync(x => x.FingerprintImageId == fingerprintId && x.Provider == "GoogleDrive", ct);

        if (record is not null && record.Status == "Synced" && !string.IsNullOrWhiteSpace(record.DriveFileId))
            return new { ok = true, status = record.Status, driveFileId = record.DriveFileId, webViewLink = record.DriveWebViewLink };

        record ??= new CloudSyncRecord
        {
            Id = Guid.NewGuid(),
            FingerprintImageId = fingerprintId,
            Provider = "GoogleDrive"
        };
        record.Status = "Syncing";
        record.AttemptCount++;
        record.LastAttemptAtUtc = DateTime.UtcNow;
        record.LastError = "";
        _db.Update(record);
        await _db.SaveChangesAsync(ct);

        try
        {
            byte[] png = await _storage.ReadDecryptedAsync(image.EncryptedFileName, ct);
            var person = await _db.Persons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == image.PersonId, ct)
                ?? throw new KeyNotFoundException("Person not found.");

            string baseUrl = _configuration["Futronic:CollectorAgentBaseUrl"] ?? "http://127.0.0.1:15271";
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromMinutes(2);

            var request = new
            {
                fingerprintId,
                personCode = person.PersonCode,
                fingerCode = image.FingerCode,
                position = image.Position,
                sequenceNo = image.SequenceNo,
                pngBase64 = Convert.ToBase64String(png)
            };

            using var response = await client.PostAsJsonAsync("drive/upload-fingerprint", request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Collector.Agent Google Drive upload failed: HTTP {(int)response.StatusCode} {body}");

            var result = System.Text.Json.JsonSerializer.Deserialize<DriveUploadResponse>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Invalid Google Drive upload response.");

            record.Status = "Synced";
            record.DriveFileId = result.Result?.Id ?? "";
            record.DriveWebViewLink = result.Result?.WebViewLink ?? "";
            record.SyncedAtUtc = DateTime.UtcNow;
            image.SyncStatus = "Synced";
            await _db.SaveChangesAsync(ct);

            return new { ok = true, status = record.Status, driveFileId = record.DriveFileId, webViewLink = record.DriveWebViewLink };
        }
        catch (Exception ex)
        {
            record.Status = "Failed";
            record.LastError = ex.Message;
            image.SyncStatus = "Failed";
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    private sealed record DriveUploadResponse(bool Ok, Guid FingerprintId, DriveResult? Result);
    private sealed record DriveResult(string Id, string Name, string? WebViewLink, string? MimeType, long? Size, string? FolderId);
}
