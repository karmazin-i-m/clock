namespace KClock.Data.Entities;

public static class DeviceAccessRole
{
    public const string Owner = "owner";
    public const string Viewer = "viewer";
}

/// <summary>
/// Keyed on the binding, not the device — closing a binding closes every access granted under
/// it with no separate cleanup (DESIGN.md §7).
/// </summary>
public class DeviceAccess
{
    public long BindingId { get; set; }
    public Guid AccountId { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public DeviceBinding? Binding { get; set; }
    public Account? Account { get; set; }
}
