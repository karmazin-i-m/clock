namespace KClock.Data.Entities;

/// <summary>
/// Issued per binding, not per device. TokenHash is sha256(token) — the token itself is never
/// stored (DESIGN.md §6).
/// </summary>
public class DeviceCredential
{
    public long Id { get; set; }
    public long DeviceId { get; set; }
    public long BindingId { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    public Device? Device { get; set; }
    public DeviceBinding? Binding { get; set; }
}
