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
    public string? EspVersion { get; set; }
    public string? NanoVersion { get; set; }
}
