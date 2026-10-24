using KClock.Data;
using KClock.Data.Entities;
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
    /// <summary>
    /// What a binding nobody has given a zone yet is read in. The product is Ukrainian, and
    /// the ESP's own DefaultTz is Kyiv's rule, so this is what the clock is already showing.
    /// </summary>
    public const string DefaultTimezone = "Europe/Kyiv";

    private const int DisplayNameMaxLength = 40;

    public static async Task<IResult> ListAsync(ClockDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var rows = await db.DeviceBindings
            .Where(b => b.UnboundAt == null)
            .OrderBy(b => b.BoundAt)
            .Join(db.Devices, b => b.DeviceId, d => d.Id, (b, d) => new { Binding = b, Device = d })
            .ToListAsync(ct);

        var deviceIds = rows.Select(r => r.Device.Id).ToList();
        var states = await db.DeviceStates
            .Where(s => deviceIds.Contains(s.DeviceId))
            .ToDictionaryAsync(s => s.DeviceId, ct);
        var latest = await TelemetryQueries.LatestAsync(db, rows.Select(r => (r.Device.Id, r.Binding.Id)).ToList(), ct);

        var now = clock.GetUtcNow();
        return Results.Ok(rows.Select(r => ToDto(
            r.Device, r.Binding, states.GetValueOrDefault(r.Device.Id), latest.GetValueOrDefault(r.Binding.Id), now)));
    }

    public static async Task<IResult> GetAsync(long id, ClockDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (await FindActiveAsync(db, id, ct) is not (var device, var binding))
        {
            return Results.NotFound();
        }

        var state = await db.DeviceStates.SingleOrDefaultAsync(s => s.DeviceId == id, ct);
        var latest = await TelemetryQueries.LatestAsync(db, [(device.Id, binding.Id)], ct);
        return Results.Ok(ToDto(device, binding, state, latest.GetValueOrDefault(binding.Id), clock.GetUtcNow()));
    }

    public static async Task<IResult> SummaryAsync(long id, ClockDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (await FindActiveAsync(db, id, ct) is not (var device, var binding))
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        return Results.Ok(ToDto(await TelemetryQueries.SummaryAsync(db, device.Id, binding.Id, now, ct), now));
    }

    public static async Task<IResult> SeriesAsync(
        long id, string? metric, string? period, ClockDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (!TelemetryQueries.IsMetric(metric) || !TelemetryQueries.IsPeriod(period))
        {
            return Results.BadRequest(new { error = "metric must be temp|pressure|humidity, period day|week|month|year" });
        }

        if (await FindActiveAsync(db, id, ct) is not (var device, var binding))
        {
            return Results.NotFound();
        }

        return Results.Ok(ToDto(await TelemetryQueries.SeriesAsync(
            db, device.Id, binding.Id, metric, period, EffectiveTimezone(binding), clock.GetUtcNow(), ct)));
    }

    public static async Task<IResult> UpdateAsync(
        long id, UpdateDeviceRequest request, ClockDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var displayName = request.DisplayName?.Trim();
        if (displayName is { Length: 0 or > DisplayNameMaxLength })
        {
            return Results.BadRequest(new { error = $"display_name must be 1–{DisplayNameMaxLength} characters" });
        }

        // The zone reaches time_bucket() in every chart query, where a name Postgres does not
        // know fails the whole request — so it is checked on the way in, once.
        if (request.Timezone is not null && !IsIanaTimezone(request.Timezone))
        {
            return Results.BadRequest(new { error = "timezone must be an IANA zone name, e.g. Europe/Kyiv" });
        }

        var binding = await db.DeviceBindings.SingleOrDefaultAsync(b => b.DeviceId == id && b.UnboundAt == null, ct);
        if (binding is null)
        {
            return Results.NotFound();
        }

        if (displayName is not null)
        {
            binding.DisplayName = displayName;
        }

        var timezoneChanged = request.Timezone is not null && request.Timezone != binding.Timezone;
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

        // The zone reaches the clock in the same downlink as the display settings, so a new
        // zone is a new settings version.
        if (timezoneChanged && !await SettingsEndpoints.BumpAsync(db, binding.Id, clock, ct))
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

    public static string EffectiveTimezone(DeviceBinding binding) => binding.Timezone ?? DefaultTimezone;

    /// <summary>
    /// On Linux .NET also resolves Windows zone ids ("FLE Standard Time") by converting them,
    /// and Postgres knows none of those, so the id has to be IANA as given, not merely findable.
    /// </summary>
    private static bool IsIanaTimezone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) && zone.HasIanaId && zone.Id == id;

    private static async Task<(Device Device, DeviceBinding Binding)?> FindActiveAsync(ClockDbContext db, long id, CancellationToken ct)
    {
        var row = await db.DeviceBindings
            .Where(b => b.UnboundAt == null && b.DeviceId == id)
            .Join(db.Devices, b => b.DeviceId, d => d.Id, (b, d) => new { Binding = b, Device = d })
            .SingleOrDefaultAsync(ct);
        return row is null ? null : (row.Device, row.Binding);
    }

    private static object ToDto(Device device, DeviceBinding binding, DeviceState? state, TelemetryQueries.Latest? latest, DateTimeOffset now)
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
            timezone = EffectiveTimezone(binding),
            bound_at = binding.BoundAt,
            online,
            fw_version = state?.FwVersion,
            last_seen_at = state?.LastIngestAt,
            last = ToDto(latest?.Reading),
            pressure_trend = latest?.Trend,
            pressure_delta_3h = latest?.PressureDelta3h,
        };
    }

    internal static object? ToDto(TelemetryQueries.Reading? reading) => reading is null
        ? null
        : new
        {
            ts = reading.Ts,
            temp_c = reading.TempC,
            pressure_mmhg = reading.PressureMmhg,
            humidity_pct = reading.HumidityPct,
        };

    internal static object ToDto(TelemetryQueries.Summary summary, DateTimeOffset to) => new
    {
        from = to - TimeSpan.FromDays(1),
        to,
        temp_min_c = summary.TempMinC,
        temp_max_c = summary.TempMaxC,
        humidity_min_pct = summary.HumidityMinPct,
        humidity_max_pct = summary.HumidityMaxPct,
        readings = summary.Readings,
    };

    internal static object ToDto(TelemetryQueries.Series series) => new
    {
        metric = series.Metric,
        period = series.Period,
        step_s = series.StepS,
        from = series.From,
        to = series.To,
        points = series.Points,
    };
}
