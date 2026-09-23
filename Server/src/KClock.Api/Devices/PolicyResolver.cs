using KClock.Data.Entities;

namespace KClock.Api.Devices;

/// <summary>
/// DESIGN.md §5.5's two profiles, each field overridable per device by a nullable column
/// (DESIGN.md §5.5) — one flaky unit can be slowed down without a deploy or a reflash.
/// </summary>
public static class PolicyResolver
{
    public static PolicyDto Resolve(Device device)
    {
        var isEsp32 = device.Profile == "esp32";
        return new PolicyDto(
            SampleS: device.PolicySampleS ?? 10,
            FlushS: device.PolicyFlushS ?? (isEsp32 ? 30 : 60),
            MaxBatch: device.PolicyMaxBatch ?? (isEsp32 ? 60 : 12),
            V: 0);
    }
}
