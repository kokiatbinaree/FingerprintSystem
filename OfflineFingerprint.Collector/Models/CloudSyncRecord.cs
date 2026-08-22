namespace OfflineFingerprint.Collector.Models;

public class CloudSyncRecord
{
    public Guid Id { get; set; }
    public Guid FingerprintImageId { get; set; }
    public string Provider { get; set; } = "GoogleDrive";
    public string Status { get; set; } = "Pending";
    public string DriveFileId { get; set; } = "";
    public string DriveWebViewLink { get; set; } = "";
    public string LastError { get; set; } = "";
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? SyncedAtUtc { get; set; }
}
