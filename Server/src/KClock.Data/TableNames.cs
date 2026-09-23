namespace KClock.Data;

/// <summary>
/// Shared between the EF-owned configurations (ClockDbContext) and the runtime-only,
/// ExcludeFromMigrations mappings (IngestDbContext) so both always name the same physical table.
/// </summary>
internal static class TableNames
{
    public const string Account = "account";
    public const string ExternalLogin = "external_login";
    public const string Device = "device";
    public const string DeviceBinding = "device_binding";
    public const string DeviceAccess = "device_access";
    public const string EnrollmentCode = "enrollment_code";
    public const string DeviceCredential = "device_credential";
    public const string Telemetry = "telemetry";
    public const string DeviceState = "device_state";
}
