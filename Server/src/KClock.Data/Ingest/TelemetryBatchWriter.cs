using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KClock.Data.Ingest;

/// <summary>
/// Idempotent by construction on (device_id, ts), order-independent, and correct for a device
/// that retried after a timeout it never saw the answer to (DESIGN.md §5.4).
///
/// binding_id is resolved per sample by joining device_binding on the half-open period that
/// contains the sample's own timestamp — ts >= bound_at AND (unbound_at IS NULL OR ts <
/// unbound_at) — never taken from "whichever binding the credential currently belongs to".
/// That is what makes DESIGN.md §9's in-flight-sample rule correct: a reading taken while the
/// previous owner still held the clock is attributed to them even if it arrives after the
/// unbind. A sample matching no binding at all is silently excluded from the affected-row
/// count and never inserted — telemetry.binding_id is NOT NULL specifically so that mistake
/// can't happen even by accident (DESIGN.md §9).
///
/// Deliberately raw SQL with array parameters, not EF AddRange: a naive AddRange of a
/// 60-sample batch would generate hundreds of parameters against Npgsql's 65535 limit
/// (DESIGN.md §5.4).
/// </summary>
public class TelemetryBatchWriter(IngestDbContext db)
{
    public async Task<int> InsertAsync(long deviceId, IReadOnlyList<TelemetrySample> samples, CancellationToken ct = default)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        var ts = new DateTimeOffset[samples.Count];
        var tc = new short?[samples.Count];
        var p = new short?[samples.Count];
        var h = new short?[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            ts[i] = samples[i].Ts;
            tc[i] = samples[i].TemperatureDc;
            p[i] = samples[i].PressureMmhg;
            h[i] = samples[i].HumidityPct;
        }

        // Table names are compile-time constants (TableNames), never user input, so plain string
        // interpolation for identifiers is safe here — only sample data goes through parameters.
        // EF's ExecuteSqlInterpolatedAsync has no way to emit an unparameterized identifier, and
        // identifiers can never be SQL parameters, so ExecuteSqlRawAsync + explicit
        // NpgsqlParameters is used instead.
        var sql = $"""
                   INSERT INTO {TableNames.Telemetry} (device_id, ts, binding_id, temperature_dc, pressure_mmhg, humidity_pct)
                   SELECT @deviceId, s.ts, b.id, s.tc, s.p, s.h
                   FROM unnest(@ts, @tc, @p, @h) AS s(ts, tc, p, h)
                   JOIN {TableNames.DeviceBinding} b
                     ON b.device_id = @deviceId
                    AND s.ts >= b.bound_at
                    AND (b.unbound_at IS NULL OR s.ts < b.unbound_at)
                   ON CONFLICT (device_id, ts) DO NOTHING
                   """;

        return await db.Database.ExecuteSqlRawAsync(
            sql,
            [
                new NpgsqlParameter("deviceId", deviceId),
                new NpgsqlParameter("ts", ts),
                new NpgsqlParameter("tc", tc),
                new NpgsqlParameter("p", p),
                new NpgsqlParameter("h", h),
            ],
            ct);
    }
}
