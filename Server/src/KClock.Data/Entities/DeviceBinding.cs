namespace KClock.Data.Entities;

/// <summary>
/// Ownership is a period, not a field (DESIGN.md §7). At most one row per device has
/// UnboundAt == null, enforced by the partial unique index device_binding_active.
/// </summary>
public class DeviceBinding
{
    public long Id { get; set; }
    public long DeviceId { get; set; }
    public Guid AccountId { get; set; }
    public string? DisplayName { get; set; }
    public string? Timezone { get; set; }
    public DateTimeOffset BoundAt { get; set; }
    public DateTimeOffset? UnboundAt { get; set; }
    public string? UnboundReason { get; set; }

    public Device? Device { get; set; }
    public Account? Account { get; set; }
    public List<DeviceAccess> Access { get; set; } = [];
    public List<DeviceCredential> Credentials { get; set; } = [];
}
