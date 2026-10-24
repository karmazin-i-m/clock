using KClock.Data;
using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KClock.Api.Accounts;

public record DisplaySettingsRequest(int DimFrom, int DimUntil, int MarqueeMinutes);

/// <summary>
/// The display settings the clock is sent with its next batch (DESIGN.md §5.2, "config").
/// The cloud is authoritative (§3): once an owner saves here, the clock runs these values
/// whatever was typed into its local page before, and a clock never saved here keeps its own.
/// </summary>
public static class SettingsEndpoints
{
    public static async Task<IResult> GetAsync(long id, ClockDbContext db, CancellationToken ct)
    {
        var binding = await db.DeviceBindings.SingleOrDefaultAsync(b => b.DeviceId == id && b.UnboundAt == null, ct);
        if (binding is null)
        {
            return Results.NotFound();
        }

        // No row yet means the cloud has never sent anything: the page starts from the
        // firmware's own defaults, at version 0, which is also what such a clock reports.
        var settings = await db.DeviceSettings.SingleOrDefaultAsync(s => s.BindingId == binding.Id, ct)
            ?? DeviceSettings.Defaults(binding.Id);
        var applied = await db.DeviceStates.Where(s => s.DeviceId == id).Select(s => s.Cfgv).SingleOrDefaultAsync(ct);

        return Results.Ok(new
        {
            dim_from = settings.DimFrom,
            dim_until = settings.DimUntil,
            marquee_minutes = settings.MarqueeMinutes,
            version = settings.Version,
            applied_version = applied,
        });
    }

    public static async Task<IResult> PutAsync(long id, DisplaySettingsRequest request, ClockDbContext db, TimeProvider clock, CancellationToken ct)
    {
        // The firmware's own ranges (the Nano's linkSetConfig), which the table's CHECKs repeat.
        if (request.DimFrom is < 0 or > 24 || request.DimUntil is < 0 or > 24 || request.MarqueeMinutes is < 0 or > 60)
        {
            return Results.BadRequest(new { error = "dim_from and dim_until must be 0–24, marquee_minutes 0–60" });
        }

        var binding = await db.DeviceBindings.SingleOrDefaultAsync(b => b.DeviceId == id && b.UnboundAt == null, ct);
        if (binding is null)
        {
            return Results.NotFound();
        }

        return await BumpAsync(db, binding.Id, clock, ct, settings =>
        {
            settings.DimFrom = (short)request.DimFrom;
            settings.DimUntil = (short)request.DimUntil;
            settings.MarqueeMinutes = (short)request.MarqueeMinutes;
        })
            ? Results.NoContent()
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Applies a change and gives the settings a new version, creating the row from the
    /// firmware defaults if there is none — also what a time zone change does, since the zone
    /// travels in the same downlink. False when RLS refused: not the owner, or the binding is
    /// no longer open (device_settings_insert_owner / _update_owner, DESIGN.md §9).
    /// </summary>
    public static async Task<bool> BumpAsync(ClockDbContext db, long bindingId, TimeProvider clock, CancellationToken ct, Action<DeviceSettings>? change = null)
    {
        var settings = await db.DeviceSettings.SingleOrDefaultAsync(s => s.BindingId == bindingId, ct);
        if (settings is null)
        {
            settings = DeviceSettings.Defaults(bindingId);
            db.DeviceSettings.Add(settings);
        }

        change?.Invoke(settings);
        settings.Version = await db.Database
            .SqlQueryRaw<int>("""SELECT nextval('device_settings_version')::int AS "Value" """)
            .SingleAsync(ct);
        settings.UpdatedAt = clock.GetUtcNow();

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege })
        {
            return false;
        }
    }
}
