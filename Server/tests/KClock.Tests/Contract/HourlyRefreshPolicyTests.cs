using KClock.Data.Ingest;
using KClock.Tests.Fixtures;
using Npgsql;
using Xunit;

namespace KClock.Tests.Contract;

/// <summary>
/// telemetry_1h's refresh policy (TelemetryHourlyRefreshPolicy): its first run keeps history
/// stored before it, and a later run picks up a late sample in a bucket already materialised.
/// In the materialising database, because running the policy moves the watermark.
/// </summary>
[Collection(MaterializingPostgresCollection.Name)]
public class HourlyRefreshPolicyTests(PostgresFixture fixture) : CabinetTestBase(fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public async Task RefreshPolicy_KeepsOldHistory_AndPicksUpLateSamples()
    {
        var owner = await CreateGoogleAccountAsync();
        var bucket = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(-3).UtcDateTime.Date.AddHours(12), TimeSpan.Zero);
        var (device, binding) = await SeedClockAsync(owner, bucket.AddDays(-1));
        await InsertAsync(device, bucket.AddMinutes(10));
        // Something recent too, as production always has: it is what moves the watermark.
        await InsertAsync(device, DateTimeOffset.UtcNow.AddHours(-2));
        var samples = $"SELECT coalesce(sum(samples), 0)::bigint FROM telemetry_1h WHERE binding_id = @b AND bucket = '{bucket:O}'";

        // start_offset is NULL: a finite one would move the watermark past this bucket
        // without materialising it, and below the watermark nothing is computed live.
        await RunRefreshPolicyAsync();
        Assert.Equal(1, await CountAsync(samples, binding));

        // Below the watermark now, so the second sample shows only once a refresh has acted
        // on the invalidation it recorded.
        await InsertAsync(device, bucket.AddMinutes(20));
        Assert.Equal(1, await CountAsync(samples, binding));
        await RunRefreshPolicyAsync();
        Assert.Equal(2, await CountAsync(samples, binding));
    }

    private async Task InsertAsync(long device, DateTimeOffset ts)
    {
        await using var ingest = CreateIngestDbContext();
        await new TelemetryBatchWriter(ingest).InsertAsync(device, [new TelemetrySample(ts, 210, 750, 40)]);
    }

    // run_job, because the fixture runs no scheduler; a refresh cannot be called from inside
    // a DO block or a subquery, hence the two round trips.
    private async Task RunRefreshPolicyAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await conn.OpenAsync();
        await using var find = new NpgsqlCommand(
            """
            SELECT job_id FROM timescaledb_information.jobs
            WHERE proc_name = 'policy_refresh_continuous_aggregate'
            """, conn);
        var jobId = (int)(await find.ExecuteScalarAsync())!;
        await using var run = new NpgsqlCommand($"CALL run_job({jobId})", conn);
        await run.ExecuteNonQueryAsync();
    }
}
