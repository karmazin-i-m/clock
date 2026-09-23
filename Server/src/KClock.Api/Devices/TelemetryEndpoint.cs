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

    public static async Task<DeviceResult> HandleAsync(
        ClaimsPrincipal user, TelemetryRequest request, IngestDbContext db, TimeProvider clock, CancellationToken ct)
    {
        // System.Text.Json does not enforce non-nullable annotations, so a body without
        // "samples" arrives as null. Left alone it becomes a NullReferenceException and a 500,
        // which the device must keep and retry — a poison batch wedging the ring (§5.3).
        if (request.Samples is null)
        {
            return DeviceResult.BadRequest();
        }

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
                ScrubHumidity(sample.H)));
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

    // Anything outside 0-100 would trip the humidity CHECK and turn the whole batch into a
    // 500 the device retries for ever (§5.3). The -1 sentinel is one case of this; a sensor
    // glitch reading 101 is another. Both mean "no valid reading", so both become NULL.
    private static short? ScrubHumidity(short? value) => value is >= 0 and <= 100 ? value : null;
}
