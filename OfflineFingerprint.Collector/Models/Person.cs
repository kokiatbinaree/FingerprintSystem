namespace OfflineFingerprint.Collector.Models;

public class Person
{
    public Guid Id { get; set; }
    public string PersonCode { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? NationalId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<FingerprintImage> FingerprintImages { get; set; } = [];
}
