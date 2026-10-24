namespace KClock.Data.Entities;

/// <summary>
/// What the clock's own panel does — night dimming and the marquee — held by the cloud and
/// sent down in the telemetry reply (the v2 config downlink, DESIGN.md §5.2). Keyed on the
/// binding rather than the device: these are one owner's choices, and a clock that changes
/// hands starts its new period with none, keeping whatever it already runs.
///
/// The time zone is part of the same downlink but stays on DeviceBinding, where the browser
/// already edits it; changing it bumps Version here all the same.
///
/// Version comes from one global sequence, not a per-row counter. The clock remembers the
/// last version it applied across owners, so a per-binding count starting again at 1 could
/// collide with what a previous owner's 1 left on it, and the new owner's first change would
/// never be sent.
/// </summary>
public class DeviceSettings
{
    public long BindingId { get; set; }
    public short DimFrom { get; set; }
    public short DimUntil { get; set; }
    public short MarqueeMinutes { get; set; }
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The firmware's compiled defaults (DIM_FROM_HOUR, DIM_UNTIL_HOUR, MARQUEE_PERIOD_MINUTES).</summary>
    public static DeviceSettings Defaults(long bindingId) => new()
    {
        BindingId = bindingId,
        DimFrom = 22,
        DimUntil = 7,
        MarqueeMinutes = 5,
    };
}
