using KClock.Data;
using Microsoft.EntityFrameworkCore;

namespace KClock.Api.Accounts;

public record UpdateDeviceRequest(string? DisplayName, string? Timezone);

/// <summary>
/// There is no DELETE /api/devices/{id} — the device row is global, outlives every binding,
/// and is keyed on a MAC that physically exists. What a user deletes is their binding
/// (DESIGN.md §12), via DELETE /api/devices/{id}/binding.
/// </summary>
public static class DevicesEndpoints
{
    public static async Task<IResult> ListAsync(ClockDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var rows = await db.DeviceBindings
            .Where(b => b.UnboundAt == null)
            .Join(db.Devices, b => b.DeviceId, d => d.Id, (b, d) => new { Binding = b, Device = d })
            .ToListAsync(ct);

        var deviceIds = rows.Select(r => r.Device.Id).ToList();
        var states = await db.DeviceStates
            .Where(s => deviceIds.Contains(s.DeviceId))
            .ToDictionaryAsync(s => s.DeviceId, ct);

        var now = clock.GetUtcNow();
        return Results.Ok(rows.Select(r => ToDto(r.Device, r.Binding, states.GetValueOrDefault(r.Device.Id), now)));
    }

    public static async Task<IResult> GetAsync(long id, ClockDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var row = await db.DeviceBindings
            .Where(b => b.UnboundAt == null && b.DeviceId == id)
            .Join(db.Devices, b => b.DeviceId, d => d.Id, (b, d) => new { Binding = b, Device = d })
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        var state = await db.DeviceStates.SingleOrDefaultAsync(s => s.DeviceId == id, ct);
        return Results.Ok(ToDto(row.Device, row.Binding, state, clock.GetUtcNow()));
    }

    public static async Task<IResult> UpdateAsync(long id, UpdateDeviceRequest request, ClockDbContext db, CancellationToken ct)
    {
        var binding = await db.DeviceBindings.SingleOrDefaultAsync(b => b.DeviceId == id && b.UnboundAt == null, ct);
        if (binding is null)
        {
            return Results.NotFound();
        }

        if (request.DisplayName is not null)
        {
            binding.DisplayName = request.DisplayName;
        }

        if (request.Timezone is not null)
        {
            binding.Timezone = request.Timezone;
        }

        // RLS's device_binding_update_owner policy is the real enforcement (role = 'owner',
        // still open) — a non-owner's UPDATE matches zero rows and EF surfaces that as a
        // concurrency exception, caught below (DESIGN.md §8's "database as the backstop").
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return Results.NoContent();
    }

    public static async Task<IResult> UnbindAsync(long id, ClockDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var binding = await db.DeviceBindings.SingleOrDefaultAsync(b => b.DeviceId == id && b.UnboundAt == null, ct);
        if (binding is null)
        {
            return Results.NotFound();
        }

        binding.UnboundAt = clock.GetUtcNow();
        binding.UnboundReason = "owner requested";

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return Results.NoContent();
    }

    private static object ToDto(Data.Entities.Device device, Data.Entities.DeviceBinding binding, Data.Entities.DeviceState? state, DateTimeOffset now)
    {
        var flushS = device.PolicyFlushS ?? (device.Profile == "esp32" ? 30 : 60);
        var online = state?.LastIngestAt is { } lastIngest && now - lastIngest < TimeSpan.FromSeconds(3 * flushS);
        return new
        {
            id = device.Id,
            hardware_id = device.HardwareId,
            profile = device.Profile,
            model = device.Model,
            display_name = binding.DisplayName,
            timezone = binding.Timezone,
            bound_at = binding.BoundAt,
            online,
        };
    }
}
