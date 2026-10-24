namespace KClock.Data.Entities;

/// <summary>
/// Deliberately a separate table from Device: Device is cold and read by every browser
/// request, DeviceState is written on every batch. Keeping them apart keeps the hot write off
/// the row the UI reads (DESIGN.md §7).
/// </summary>
public class DeviceState
{
    public long DeviceId { get; set; }
    public DateTimeOffset? LastIngestAt { get; set; }
    public long? LastSeq { get; set; }
    public string? FwVersion { get; set; }

    /// <summary>
    /// The settings version the clock said it holds in its last batch (cfgv, DESIGN.md §5.2) —
    /// behind DeviceSettings.Version until it has picked a change up.
    /// </summary>
    public int? Cfgv { get; set; }
}
