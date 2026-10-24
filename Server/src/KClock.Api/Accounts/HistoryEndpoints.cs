using KClock.Data;
using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KClock.Api.Accounts;

/// <summary>
/// Former clocks: closed bindings this account still has access under. Addressed by binding,
/// not device, because the device may since have come back to the same account (A → B → A),
/// and then one device id stands for two separate periods of ownership (DESIGN.md §7). Read
/// only by construction — device_binding_update_owner refuses a closed binding, and nothing
/// here writes (DESIGN.md §9).
/// </summary>
public static class HistoryEndpoints
{
    public static async Task<IResult> ListAsync(ClockDbContext db, CancellationToken ct)
    {
        var rows = await db.DeviceBindings
            .Where(b => b.UnboundAt != null)
            .OrderByDescending(b => b.UnboundAt)
            .Join(db.Devices, b => b.DeviceId, d => d.Id, (b, d) => new { Binding = b, Device = d })
            .ToListAsync(ct);
        return Results.Ok(rows.Select(r => ToDto(r.Device, r.Binding)));
    }

    public static async Task<IResult> GetAsync(long bindingId, ClockDbContext db, CancellationToken ct)
    {
        if (await FindClosedAsync(db, bindingId, ct) is not (var device, var binding))
        {
            return Results.NotFound();
        }

        var latest = await TelemetryQueries.LatestAsync(db, [(device.Id, binding.Id)], ct);
        var summary = await TelemetryQueries.SummaryAsync(db, device.Id, binding.Id, binding.UnboundAt!.Value, ct);
        return Results.Ok(new
        {
            clock = ToDto(device, binding),
            last = DevicesEndpoints.ToDto(latest.GetValueOrDefault(binding.Id)?.Reading),
            summary = DevicesEndpoints.ToDto(summary, binding.UnboundAt.Value),
        });
    }

    /// <summary>
    /// The window ends where the ownership did, not now: a year chart of a clock given away
    /// in March should be a year up to March, not nine empty months.
    /// </summary>
    public static async Task<IResult> SeriesAsync(
        long bindingId, string? metric, string? period, ClockDbContext db, CancellationToken ct)
    {
        if (!TelemetryQueries.IsMetric(metric) || !TelemetryQueries.IsPeriod(period))
        {
            return Results.BadRequest(new { error = "metric must be temp|pressure|humidity, period day|week|month|year" });
        }

        if (await FindClosedAsync(db, bindingId, ct) is not (var device, var binding))
        {
            return Results.NotFound();
        }

        return Results.Ok(DevicesEndpoints.ToDto(await TelemetryQueries.SeriesAsync(
            db, device.Id, binding.Id, metric, period, DevicesEndpoints.EffectiveTimezone(binding), binding.UnboundAt!.Value, ct)));
    }

    private static async Task<(Device Device, DeviceBinding Binding)?> FindClosedAsync(ClockDbContext db, long bindingId, CancellationToken ct)
    {
        var row = await db.DeviceBindings
            .Where(b => b.Id == bindingId && b.UnboundAt != null)
            .Join(db.Devices, b => b.DeviceId, d => d.Id, (b, d) => new { Binding = b, Device = d })
            .SingleOrDefaultAsync(ct);
        return row is null ? null : (row.Device, row.Binding);
    }

    /// <summary>
    /// unbound_reason is passed through as the server wrote it — "owner requested" or
    /// "re-enrolled" — so the page can say a clock was taken by a new enrollment rather than
    /// let it silently vanish (DESIGN.md §5.1's accepted exposure).
    /// </summary>
    private static object ToDto(Device device, DeviceBinding binding) => new
    {
        binding_id = binding.Id,
        device_id = device.Id,
        model = device.Model,
        display_name = binding.DisplayName,
        timezone = DevicesEndpoints.EffectiveTimezone(binding),
        bound_at = binding.BoundAt,
        unbound_at = binding.UnboundAt,
        unbound_reason = binding.UnboundReason,
    };
}
