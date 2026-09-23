namespace KClock.Data.Entities;

/// <summary>
/// PK is (DeviceId, Ts) — natural key, order-independent, idempotent by construction
/// (DESIGN.md §5.4). BindingId rides along as an ordinary NOT NULL column rather than taking
/// part in the key (DESIGN.md §7's "ownership is a period" discussion) — a row's owner is
/// resolved by joining through the binding whose [BoundAt, UnboundAt) period contains Ts.
/// Sentinel values (-999 temperature, -1 pressure/humidity) never reach this table: they
/// become NULL at ingest, in one place (DESIGN.md §7).
/// </summary>
public class Telemetry
{
    public long DeviceId { get; set; }
    public DateTimeOffset Ts { get; set; }
    public long BindingId { get; set; }
    public short? TemperatureDc { get; set; }
    public short? PressureMmhg { get; set; }
    public short? HumidityPct { get; set; }
    public short? WifiRssiDbm { get; set; }
    public int? EspFreeHeapB { get; set; }
    public int? EspUptimeS { get; set; }
    public int? LinkGood { get; set; }
    public int? LinkDropped { get; set; }
    public int? LinkRejected { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
