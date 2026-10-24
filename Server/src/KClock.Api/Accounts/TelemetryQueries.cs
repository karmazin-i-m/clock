using System.Diagnostics.CodeAnalysis;
using KClock.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace KClock.Api.Accounts;

/// <summary>
/// Every read of telemetry the browser makes, shared by the current clocks (/api/devices) and
/// the former ones (/api/history). Only ever through telemetry_by_account and
/// telemetry_1h_by_account: clock_app holds no grant on the tables themselves (DESIGN.md §8).
///
/// Every query is also narrowed to one binding, which RLS alone does not do. RLS hides other
/// people's rows, but an account that owned the same clock twice (A → B → A) holds two
/// bindings of it, and a chart of the current one must not draw the first.
///
/// Plain Npgsql on the context's own connection and transaction rather than EF, because the
/// transaction is where AccountScopeFilter set the GUC — a command on any other connection
/// would see nothing at all.
/// </summary>
public static class TelemetryQueries
{
    public record Reading(DateTimeOffset Ts, double? TempC, short? PressureMmhg, short? HumidityPct);

    /// <summary>
    /// Trend is the firmware's own rule (pressureTrendGlyph, PRESSURE_TREND_MMHG): the latest
    /// reading against the oldest one in the three hours before it, ±1 mm or more. The oldest
    /// has to be at least a quarter of an hour back, which is when the clock's own arrow
    /// appears too; before that both say nothing.
    /// </summary>
    public record Latest(Reading Reading, string? Trend, int? PressureDelta3h);

    public record Summary(double? TempMinC, double? TempMaxC, short? HumidityMinPct, short? HumidityMaxPct, long Readings);

    public record Series(string Metric, string Period, int StepS, long From, long To, List<double?[]> Points);

    private const int TrendThresholdMmhg = 1;

    private record PeriodSpec(TimeSpan Span, TimeSpan Step, bool Hourly);

    /// <summary>
    /// The day reads the raw view, since an hourly bucket would draw a day as 24 points; the
    /// rest read the hourly aggregate, which is what it exists for. A year off the raw table
    /// would be three million rows per clock.
    /// </summary>
    private static readonly Dictionary<string, PeriodSpec> Periods = new()
    {
        ["day"] = new(TimeSpan.FromDays(1), TimeSpan.FromMinutes(10), Hourly: false),
        ["week"] = new(TimeSpan.FromDays(7), TimeSpan.FromHours(1), Hourly: true),
        ["month"] = new(TimeSpan.FromDays(30), TimeSpan.FromHours(3), Hourly: true),
        ["year"] = new(TimeSpan.FromDays(365), TimeSpan.FromDays(1), Hourly: true),
    };

    // Column names are chosen from these constants, never from the query string, so they can
    // be spliced into the SQL; identifiers cannot be parameters.
    private static readonly Dictionary<string, (string Raw, string Hourly, double Scale)> Metrics = new()
    {
        ["temp"] = ("temperature_dc", "temp_avg", 0.1),
        ["pressure"] = ("pressure_mmhg", "pressure_avg", 1),
        ["humidity"] = ("humidity_pct", "humidity_avg", 1),
    };

    public static bool IsPeriod([NotNullWhen(true)] string? period) => period is not null && Periods.ContainsKey(period);

    public static bool IsMetric([NotNullWhen(true)] string? metric) => metric is not null && Metrics.ContainsKey(metric);

    public static async Task<Dictionary<long, Latest>> LatestAsync(
        ClockDbContext db, IReadOnlyList<(long DeviceId, long BindingId)> bindings, CancellationToken ct)
    {
        var result = new Dictionary<long, Latest>();
        if (bindings.Count == 0)
        {
            return result;
        }

        // device_id leads the predicate so both lookups walk the (device_id, ts) primary key
        // backwards and stop at the first row, instead of sorting a binding's whole history.
        await using var cmd = Command(db, """
            SELECT k.binding_id, l.ts, l.temperature_dc, l.pressure_mmhg, l.humidity_pct, o.pressure_mmhg
            FROM unnest(@devices, @bindings) AS k(device_id, binding_id)
            CROSS JOIN LATERAL (
                SELECT t.ts, t.temperature_dc, t.pressure_mmhg, t.humidity_pct
                FROM telemetry_by_account t
                WHERE t.device_id = k.device_id AND t.binding_id = k.binding_id
                ORDER BY t.ts DESC
                LIMIT 1) l
            LEFT JOIN LATERAL (
                SELECT t.pressure_mmhg
                FROM telemetry_by_account t
                WHERE t.device_id = k.device_id AND t.binding_id = k.binding_id
                  AND t.ts >= l.ts - INTERVAL '3 hours' AND t.ts <= l.ts - INTERVAL '15 minutes'
                  AND t.pressure_mmhg IS NOT NULL
                ORDER BY t.ts
                LIMIT 1) o ON true
            """);
        cmd.Parameters.AddWithValue("devices", bindings.Select(b => b.DeviceId).ToArray());
        cmd.Parameters.AddWithValue("bindings", bindings.Select(b => b.BindingId).ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var reading = new Reading(
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetInt16(2) / 10.0,
                reader.IsDBNull(3) ? null : reader.GetInt16(3),
                reader.IsDBNull(4) ? null : reader.GetInt16(4));

            int? delta = reading.PressureMmhg is { } now && !reader.IsDBNull(5) ? now - reader.GetInt16(5) : null;
            var trend = delta switch
            {
                null => null,
                >= TrendThresholdMmhg => "up",
                <= -TrendThresholdMmhg => "down",
                _ => "flat",
            };
            result[reader.GetInt64(0)] = new Latest(reading, trend, delta);
        }

        return result;
    }

    /// <summary>
    /// Minimum and maximum over the 24 hours before <paramref name="to"/>, and every reading
    /// the binding ever stored, counted off the hourly aggregate rather than the raw table.
    /// </summary>
    public static async Task<Summary> SummaryAsync(
        ClockDbContext db, long deviceId, long bindingId, DateTimeOffset to, CancellationToken ct)
    {
        await using var cmd = Command(db, """
            SELECT min(t.temperature_dc), max(t.temperature_dc), min(t.humidity_pct), max(t.humidity_pct),
                   (SELECT coalesce(sum(h.samples), 0)::bigint
                    FROM telemetry_1h_by_account h
                    WHERE h.device_id = @device AND h.binding_id = @binding)
            FROM telemetry_by_account t
            WHERE t.device_id = @device AND t.binding_id = @binding
              AND t.ts >= @from AND t.ts < @to
            """);
        cmd.Parameters.AddWithValue("device", deviceId);
        cmd.Parameters.AddWithValue("binding", bindingId);
        cmd.Parameters.AddWithValue("from", to - TimeSpan.FromDays(1));
        cmd.Parameters.AddWithValue("to", to);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new Summary(
            reader.IsDBNull(0) ? null : reader.GetInt16(0) / 10.0,
            reader.IsDBNull(1) ? null : reader.GetInt16(1) / 10.0,
            reader.IsDBNull(2) ? null : reader.GetInt16(2),
            reader.IsDBNull(3) ? null : reader.GetInt16(3),
            reader.GetInt64(4));
    }

    /// <summary>
    /// Buckets that hold data, in time order, with a null point wherever more than a bucket
    /// and a half separates two of them. The null is the gap: the chart breaks its line there
    /// and never draws across it (HANDOFF §4.4). Gaps are found here rather than by filling a
    /// grid in SQL because day buckets are aligned to the clock's own time zone, and a grid
    /// generated in UTC would misplace every one of them across a daylight saving change.
    /// A gap at either end needs no marker: the response carries the window it covers.
    /// </summary>
    public static async Task<Series> SeriesAsync(
        ClockDbContext db, long deviceId, long bindingId, string metric, string period, string timezone,
        DateTimeOffset to, CancellationToken ct)
    {
        var spec = Periods[period];
        var (raw, hourly, scale) = Metrics[metric];
        var from = to - spec.Span;

        // Hourly averages are recombined weighted by the samples behind each, and only over
        // hours that had a value: a BME280 that stopped answering for part of an hour leaves
        // a humidity average over fewer samples than the hour's count.
        var sql = spec.Hourly
            ? $"""
               SELECT time_bucket(@step, h.bucket, @tz) AS b,
                      sum(h.{hourly}::float8 * h.samples) / sum(h.samples)
               FROM telemetry_1h_by_account h
               WHERE h.device_id = @device AND h.binding_id = @binding
                 AND h.bucket >= @from AND h.bucket < @to AND h.{hourly} IS NOT NULL
               GROUP BY b
               ORDER BY b
               """
            : $"""
               SELECT time_bucket(@step, t.ts, @tz) AS b, avg(t.{raw})::float8
               FROM telemetry_by_account t
               WHERE t.device_id = @device AND t.binding_id = @binding
                 AND t.ts >= @from AND t.ts < @to AND t.{raw} IS NOT NULL
               GROUP BY b
               ORDER BY b
               """;

        await using var cmd = Command(db, sql);
        cmd.Parameters.AddWithValue("step", spec.Step);
        cmd.Parameters.AddWithValue("tz", timezone);
        cmd.Parameters.AddWithValue("device", deviceId);
        cmd.Parameters.AddWithValue("binding", bindingId);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        var points = new List<double?[]>();
        var gap = spec.Step.TotalSeconds * 1.5;
        long? previous = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var bucket = reader.GetFieldValue<DateTimeOffset>(0).ToUnixTimeSeconds();
                if (previous is { } last && bucket - last > gap)
                {
                    points.Add([last + (long)spec.Step.TotalSeconds, null]);
                }

                points.Add([bucket, Math.Round(reader.GetDouble(1) * scale, 1)]);
                previous = bucket;
            }
        }

        return new Series(metric, period, (int)spec.Step.TotalSeconds, from.ToUnixTimeSeconds(), to.ToUnixTimeSeconds(), points);
    }

    private static NpgsqlCommand Command(ClockDbContext db, string sql)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException(
                "Telemetry is read inside AccountScopeFilter's transaction; outside it the GUC is unset and every view is empty.");
        return new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction);
    }
}
