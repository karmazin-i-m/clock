using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KClock.Data.Ingest;
using KClock.Tests.Fixtures;
using Xunit;

namespace KClock.Tests.Contract;

/// <summary>
/// DESIGN.md §10 end to end: the request leaves no live path to the data, and the purge job
/// then leaves no data — raw rows and aggregate buckets both — without touching anyone else's.
/// In its own database, because it materialises the aggregate (MaterializingPostgresCollection).
/// </summary>
[Collection(MaterializingPostgresCollection.Name)]
public class AccountDeletionTests(PostgresFixture fixture) : CabinetTestBase(fixture)
{
    [Fact]
    public async Task DeleteAccount_CutsEveryPath_ThenThePurgeJobErasesTheData()
    {
        var doomed = await CreateGoogleAccountAsync();
        var bystander = await CreateGoogleAccountAsync();
        var now = DateTimeOffset.UtcNow;
        var (device, binding) = await SeedClockAsync(doomed, now.AddHours(-12));
        var (otherDevice, otherBinding) = await SeedClockAsync(bystander, now.AddHours(-12));
        foreach (var d in new[] { device, otherDevice })
        {
            await using var ingest = CreateIngestDbContext();
            await new TelemetryBatchWriter(ingest).InsertAsync(d,
            [
                new TelemetrySample(now.AddHours(-10), 210, 750, 40),
                new TelemetrySample(now.AddHours(-8), 220, 751, 41),
            ]);
        }

        // Materialise the buckets first: the purge must remove them, not merely the raw rows
        // they were computed from (DESIGN.md §10). Only up to the refresh policy's end_offset,
        // an hour back, where both the policy and the purge stop.
        await ExecuteAsMigratorAsync("CALL refresh_continuous_aggregate('telemetry_1h', NULL, date_trunc('hour', now() - INTERVAL '1 hour'))");
        Assert.True(await CountAsync("SELECT count(*) FROM telemetry_1h WHERE binding_id = @b", binding) > 0);

        var browser = _factory.CreateSessionClient(doomed);
        var deleted = await browser.DeleteAsync("/api/me");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Phase 1: nothing live reaches the data, and the identity is gone.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/me")).StatusCode);
        Assert.Empty(await browser.GetFromJsonAsync<JsonElement[]>("/api/history") ?? []);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM external_login WHERE account_id = @b", doomed));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM device_binding WHERE id = @b AND unbound_reason IS DISTINCT FROM 'account deleted'", binding));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM purge_queue WHERE binding_id = @b AND done_at IS NULL", binding));

        // Phase 2, as the scheduler would run it.
        await ExecuteAsMigratorAsync("CALL purge_telemetry(0, NULL)");

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM telemetry WHERE binding_id = @b", binding));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM telemetry_1h WHERE binding_id = @b", binding));
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM telemetry WHERE binding_id = @b", otherBinding));
        Assert.True(await CountAsync("SELECT count(*) FROM telemetry_1h WHERE binding_id = @b", otherBinding) > 0);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM purge_queue WHERE binding_id = @b AND done_at IS NOT NULL", binding));
    }
}
