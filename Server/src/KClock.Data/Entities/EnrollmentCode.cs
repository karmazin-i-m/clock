namespace KClock.Data.Entities;

/// <summary>
/// Stored only as sha256(normalized) — the plaintext code never touches the database
/// (DESIGN.md §5.1).
/// </summary>
public class EnrollmentCode
{
    public required byte[] CodeHash { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public long? ConsumedDeviceId { get; set; }

    public Account? Account { get; set; }
}
