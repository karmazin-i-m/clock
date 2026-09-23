using System.Security.Claims;
using KClock.Data;
using KClock.Data.Entities;
using KClock.Data.Ingest;
using Microsoft.EntityFrameworkCore;

namespace KClock.Api.Devices;

/// <summary>
/// POST /d/v1/telemetry — DESIGN.md §5.2, §5.4. Out-of-window samples are dropped and counted,
/// never individually rejected with a 400 — only an entirely-out-of-window batch is (§5.4).
/// Sentinel scrubbing happens once, here, before anything reaches TelemetryBatchWriter — the
/// same rule the DB schema backstops with telemetry_dc/pressure/humidity being nullable and
/// humidity's CHECK constraint (DESIGN.md §7).
/// </summary>
public static class TelemetryEndpoint
{
    private const short TemperatureSentinel = -999;
    private const short PressureSentinel = -1;
    private const short HumiditySentinel = -1;

    public static async Task<DeviceResult> HandleAsync(
        ClaimsPrincipal user, TelemetryRequest request, IngestDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var deviceId = long.Parse(user.FindFirstValue(DeviceClaimTypes.DeviceId)!);

        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null)
        {
            return DeviceResult.Error(StatusCodes.Status404NotFound, "not_found");
        }

        var now = clock.GetUtcNow();
        var inWindow = new List<TelemetrySample>(request.Samples.Count);
        foreach (var sample in request.Samples)
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(sample.Ts);
            if (!IngestWindow.IsInWindow(ts, now))
            {
                continue;
            }

            inWindow.Add(new TelemetrySample(
                ts,
                Scrub(sample.Tc, TemperatureSentinel),
                Scrub(sample.P, PressureSentinel),
                Scrub(sample.H, HumiditySentinel)));
        }

        if (inWindow.Count == 0 && request.Samples.Count > 0)
        {
            // Every sample failed the window — the one case DESIGN.md §5.4 has the batch
            // itself rejected outright.
            return DeviceResult.BadRequest();
        }

        var writer = new TelemetryBatchWriter(db);
        var acceptedCount = await writer.InsertAsync(deviceId, inWindow, ct);
        var dup = request.Samples.Count - acceptedCount;

        await UpsertDeviceStateAsync(db, deviceId, request.Seq, now, ct);

        return DeviceResult.Ok(new TelemetryResponse(true, acceptedCount, dup, PolicyResolver.Resolve(device)));
    }

    private static async Task UpsertDeviceStateAsync(IngestDbContext db, long deviceId, long seq, DateTimeOffset now, CancellationToken ct)
    {
        var state = await db.DeviceStates.SingleOrDefaultAsync(s => s.DeviceId == deviceId, ct);
        if (state is null)
        {
            db.DeviceStates.Add(new DeviceState { DeviceId = deviceId, LastIngestAt = now, LastSeq = seq });
        }
        else
        {
            state.LastIngestAt = now;
            state.LastSeq = seq;
        }

        await db.SaveChangesAsync(ct);
    }

    private static short? Scrub(short? value, short sentinel) => value == sentinel ? null : value;
}
