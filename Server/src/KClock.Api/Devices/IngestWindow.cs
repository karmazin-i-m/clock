namespace KClock.Api.Devices;

/// <summary>
/// DESIGN.md §5.4's server-side acceptance window, kept in one place per §7's compression-delay
/// discussion: device ring buffer (30-60 min) &lt; ingest_max_lateness (2 h) &lt;&lt; compression delay
/// (2 days). Changing MaxLateness has implications for the compression policy in the
/// InitialSchema migration — they must stay ordered.
/// </summary>
public static class IngestWindow
{
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan MaxLateness = TimeSpan.FromHours(2);
    public static readonly DateTimeOffset EarliestValidTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static bool IsInWindow(DateTimeOffset ts, DateTimeOffset now) =>
        ts <= now + FutureTolerance && ts >= EarliestValidTimestamp && ts >= now - MaxLateness;
}
