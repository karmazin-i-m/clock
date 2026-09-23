namespace KClock.Data.Entities;

/// <summary>
/// Global, outlives every binding. hardware_id is the MAC — the durable identity that makes
/// enrollment-as-transfer coherent (DESIGN.md §5.1). Never delete this row; deleting it would
/// orphan a previous owner's telemetry.
/// </summary>
public class Device
{
    public long Id { get; set; }
    public required string HardwareId { get; set; }
    public required string Profile { get; set; }
    public string? Model { get; set; }
    public int? PolicySampleS { get; set; }
    public int? PolicyFlushS { get; set; }
    public int? PolicyMaxBatch { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<DeviceBinding> Bindings { get; set; } = [];
}
