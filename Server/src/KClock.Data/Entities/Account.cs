namespace KClock.Data.Entities;

public class Account
{
    public Guid Id { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public List<ExternalLogin> ExternalLogins { get; set; } = [];
    public List<DeviceBinding> DeviceBindings { get; set; } = [];
}
