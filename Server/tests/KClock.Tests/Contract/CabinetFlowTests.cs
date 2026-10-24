using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KClock.Api;
using KClock.Api.Accounts;
using KClock.Api.Devices;
using KClock.Data;
using KClock.Data.Entities;
using KClock.Data.Ingest;
using KClock.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace KClock.Tests.Contract;

/// <summary>
/// The cabinet's writes, followed all the way to the clock: settings saved in the browser come
/// back in the telemetry reply exactly while the clock does not hold them. Requires Docker (via
/// PostgresFixture).
/// </summary>
[Collection(PostgresCollection.Name)]
public class CabinetFlowTests(PostgresFixture fixture) : CabinetTestBase(fixture)
{

    [Fact]
    public async Task Settings_ReachTheClock_OnlyWhileItDoesNotHoldThem()
    {
        var account = await CreateGoogleAccountAsync();
        var browser = _factory.CreateSessionClient(account);
        var (deviceId, clock) = await EnrollAsync(browser);

        // Never saved in the cloud: nothing is sent, and the clock keeps what it runs.
        Assert.False((await PostBatchAsync(clock, cfgv: 0)).TryGetProperty("config", out _));
        var initial = await browser.GetFromJsonAsync<JsonElement>($"/api/devices/{deviceId}/settings");
        Assert.Equal(22, initial.GetProperty("dim_from").GetInt32());
        Assert.Equal(0, initial.GetProperty("version").GetInt32());
        Assert.Equal(0, initial.GetProperty("applied_version").GetInt32());

        var saved = await browser.PutAsJsonAsync($"/api/devices/{deviceId}/settings", new { dim_from = 23, dim_until = 6, marquee_minutes = 10 });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var reply = await PostBatchAsync(clock, cfgv: 0);
        var config = reply.GetProperty("config");
        var version = config.GetProperty("cv").GetInt32();
        Assert.True(version > 0);
        Assert.Equal(23, config.GetProperty("dim_from").GetInt32());
        Assert.Equal(6, config.GetProperty("dim_until").GetInt32());
        Assert.Equal(10, config.GetProperty("marquee_m").GetInt32());
        Assert.Equal("EET-2EEST,M3.5.0/3,M10.5.0/4", config.GetProperty("tz").GetString());

        // Once the clock reports that version, the reply is back to the bare envelope, and
        // the page can see the clock has it.
        Assert.False((await PostBatchAsync(clock, cfgv: version)).TryGetProperty("config", out _));
        var applied = await browser.GetFromJsonAsync<JsonElement>($"/api/devices/{deviceId}/settings");
        Assert.Equal(version, applied.GetProperty("applied_version").GetInt32());

        // A new zone travels in the same downlink, so it is a new version too.
        var moved = await browser.PatchAsJsonAsync($"/api/devices/{deviceId}", new { timezone = "Europe/London" });
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        var london = (await PostBatchAsync(clock, cfgv: version)).GetProperty("config");
        Assert.True(london.GetProperty("cv").GetInt32() > version);
        Assert.Equal("GMT0BST,M3.5.0/1,M10.5.0", london.GetProperty("tz").GetString());
        Assert.Equal(23, london.GetProperty("dim_from").GetInt32());

        var refused = await browser.PutAsJsonAsync($"/api/devices/{deviceId}/settings", new { dim_from = 25, dim_until = 6, marquee_minutes = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    /// <summary>
    /// The clock remembers the last version it applied across owners. A per-binding counter
    /// would hand the new owner's first save the same number the old owner's first save left
    /// on the clock, and it would never be sent.
    /// </summary>
    [Fact]
    public async Task Settings_NewOwnersFirstSave_IsNeverMistakenForTheOldOnes()
    {
        var first = _factory.CreateSessionClient(await CreateGoogleAccountAsync());
        var mac = NewMac();
        var (deviceId, clock) = await EnrollAsync(first, mac);
        await first.PutAsJsonAsync($"/api/devices/{deviceId}/settings", new { dim_from = 21, dim_until = 8, marquee_minutes = 2 });
        var held = (await PostBatchAsync(clock, cfgv: 0)).GetProperty("config").GetProperty("cv").GetInt32();

        var second = _factory.CreateSessionClient(await CreateGoogleAccountAsync());
        var (_, clockAgain) = await EnrollAsync(second, mac);
        await second.PutAsJsonAsync($"/api/devices/{deviceId}/settings", new { dim_from = 0, dim_until = 0, marquee_minutes = 0 });

        var config = (await PostBatchAsync(clockAgain, cfgv: held)).GetProperty("config");
        Assert.NotEqual(held, config.GetProperty("cv").GetInt32());
        Assert.Equal(0, config.GetProperty("dim_until").GetInt32());

        // And the former owner can no longer change anything on it.
        var former = await first.PutAsJsonAsync($"/api/devices/{deviceId}/settings", new { dim_from = 1, dim_until = 2, marquee_minutes = 0 });
        Assert.Equal(HttpStatusCode.NotFound, former.StatusCode);
    }

    [Fact]
    public async Task Settings_Rls_RefusesAWriteToSomeoneElsesBinding()
    {
        var owner = _factory.CreateSessionClient(await CreateGoogleAccountAsync());
        var (deviceId, _) = await EnrollAsync(owner);
        var intruder = await CreateGoogleAccountAsync();

        await using var conn = new NpgsqlConnection(fixture.AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.account_id', @id, true)", conn, tx))
        {
            set.Parameters.AddWithValue("id", intruder.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO device_settings (binding_id, dim_from, dim_until, marquee_minutes, version)
            SELECT id, 0, 24, 0, 1 FROM device_binding WHERE device_id = @device AND unbound_at IS NULL
            """, conn, tx);
        insert.Parameters.AddWithValue("device", deviceId);
        // The intruder cannot even see the binding to name it, so nothing is inserted; the
        // policy is what would refuse it if they could.
        Assert.Equal(0, await insert.ExecuteNonQueryAsync());
    }

    /// <summary>The largest config the server can send must still fit DESIGN.md §5's 512 bytes.</summary>
    [Fact]
    public void TelemetryReply_WithTheLongestConfig_FitsTheBudget()
    {
        var reply = new TelemetryResponse(
            true, 60, 60, new PolicyDto(3600, 3600, 16, int.MaxValue),
            new ConfigDto(int.MaxValue, 24, 24, 60, new string('M', 47)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, AppJsonContext.Default.TelemetryResponse);
        Assert.True(bytes.Length <= 512, $"{bytes.Length} bytes");
    }

    [Fact]
    public async Task UnknownApiPath_Is404_NeverTheCabinetsHtml()
    {
        var browser = _factory.CreateSessionClient(await CreateGoogleAccountAsync());
        var response = await browser.GetAsync("/api/no-such-thing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}

/// <summary>A signed-in browser, a clock enrolled through it, and direct database access.</summary>
public abstract class CabinetTestBase(PostgresFixture fixture) : IDisposable
{
    protected readonly AppFactory _factory = new(fixture);
    protected readonly PostgresFixture fixture = fixture;

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    protected async Task<Guid> CreateGoogleAccountAsync()
    {
        await using var db = new ClockDbContext(
            new DbContextOptionsBuilder<ClockDbContext>().UseNpgsql(fixture.AppConnectionString).UseSnakeCaseNamingConvention().Options,
            new NoAccount());
        return await GoogleAuthEndpoints.FindOrCreateAccountAsync(db, "google", $"flow-{Guid.NewGuid():N}", "someone@example.test", "Someone");
    }

    protected static string NewMac() => Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    /// <summary>The whole binding ceremony: a code from the cabinet, typed into the clock.</summary>
    protected async Task<(long DeviceId, HttpClient Clock)> EnrollAsync(HttpClient browser, string? mac = null)
    {
        var created = await browser.PostAsync("/api/enrollment-codes", null);
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RawFixtures", "enroll_request.json");
        var body = (await File.ReadAllTextAsync(path)).Replace("__CODE__", code).Replace("__MAC__", mac ?? NewMac());
        var clock = _factory.CreateClient();
        var enrolled = await clock.PostAsync("/d/v1/enroll", new StringContent(body, Encoding.UTF8, "application/json"));
        var reply = await enrolled.Content.ReadFromJsonAsync<JsonElement>();
        clock.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reply.GetProperty("token").GetString());
        return (reply.GetProperty("device_id").GetInt64(), clock);
    }

    protected static async Task<JsonElement> PostBatchAsync(HttpClient clock, int cfgv)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = $$"""{"seq":1,"cfgv":{{cfgv}},"samples":[{"ts":{{ts}},"tc":214,"p":746,"h":41}]}""";
        var response = await clock.PostAsync("/d/v1/telemetry", new StringContent(body, Encoding.UTF8, "application/json"));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(bytes.Length <= 512, $"telemetry reply is {bytes.Length} bytes");
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    protected async Task<(long Device, long Binding)> SeedClockAsync(Guid accountId, DateTimeOffset boundAt)
    {
        await using var ingest = CreateIngestDbContext();
        var device = new Device { HardwareId = $"purge-{Guid.NewGuid():N}", Profile = "esp8266-1m", Model = "bme280" };
        ingest.Devices.Add(device);
        await ingest.SaveChangesAsync();
        var binding = new DeviceBinding { DeviceId = device.Id, AccountId = accountId, BoundAt = boundAt };
        ingest.DeviceBindings.Add(binding);
        await ingest.SaveChangesAsync();
        ingest.DeviceAccesses.Add(new DeviceAccess { BindingId = binding.Id, AccountId = accountId, Role = DeviceAccessRole.Owner });
        await ingest.SaveChangesAsync();
        return (device.Id, binding.Id);
    }

    protected async Task ExecuteAsMigratorAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    protected async Task<long> CountAsync(string sql, object key)
    {
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("b", key);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    protected IngestDbContext CreateIngestDbContext() =>
        new(new DbContextOptionsBuilder<IngestDbContext>().UseNpgsql(fixture.IngestConnectionString).UseSnakeCaseNamingConvention().Options);

    private sealed class NoAccount : ICurrentAccountAccessor
    {
        public Guid? AccountId => null;
    }
}
