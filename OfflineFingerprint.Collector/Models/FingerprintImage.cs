namespace OfflineFingerprint.Collector.Models;

public class FingerprintImage
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public Person Person { get; set; } = null!;
    public string FingerCode { get; set; } = "";
    public string Position { get; set; } = "";
    public int SequenceNo { get; set; }
    public string EncryptedFileName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public string SyncStatus { get; set; } = "Pending";
}
