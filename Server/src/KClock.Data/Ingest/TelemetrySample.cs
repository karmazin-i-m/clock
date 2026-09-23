namespace KClock.Data.Ingest;

/// <summary>
/// Already sentinel-scrubbed by the caller: the wire's -999/-1 markers must become null before
/// reaching this type, never travel further as magic numbers (DESIGN.md §7).
/// </summary>
public record TelemetrySample(DateTimeOffset Ts, short? TemperatureDc, short? PressureMmhg, short? HumidityPct);
