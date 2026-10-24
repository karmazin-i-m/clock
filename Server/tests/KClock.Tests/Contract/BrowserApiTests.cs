using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using KClock.Api.Accounts;
using KClock.Data;
using KClock.Data.Entities;
using KClock.Data.Ingest;
using KClock.Tests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace KClock.Tests.Contract;

/// <summary>
/// The cabinet's reads, end to end through Program.cs, RLS and the security_barrier views. The
/// session cookie is minted with the cookie handler's own ticket format — the same thing the
/// Google callback ends in — so everything after sign-in runs unmodified. Requires Docker
/// (via PostgresFixture).
/// </summary>
[Collection(PostgresCollection.Name)]
public class BrowserApiTests(PostgresFixture fixture) : IDisposable
{
    private readonly AppFactory _factory = new(fixture);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Api_WithoutSession_Answers401_NotARedirectToGoogle()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/devices");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Devices_CarryLastReadingAndTrend_AndNoHumidityForBmp280()
    {
        var account = await SeedAccountAsync();
        var now = DateTimeOffset.UtcNow;
        var (device, _) = await SeedClockAsync(account, "bmp280", now.AddHours(-4));
        await InsertAsync(device,
            new TelemetrySample(now.AddMinutes(-170), 214, 745, null),
            new TelemetrySample(now.AddMinutes(-90), 216, 746, null),
            new TelemetrySample(now.AddMinutes(-1), 218, 748, null));

        var clock = (await GetJsonAsync(account, "/api/devices")).EnumerateArray()
            .Single(d => d.GetProperty("id").GetInt64() == device);

        var last = clock.GetProperty("last");
        Assert.Equal(21.8, last.GetProperty("temp_c").GetDouble());
        Assert.Equal(748, last.GetProperty("pressure_mmhg").GetInt32());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("humidity_pct").ValueKind);
        Assert.Equal("up", clock.GetProperty("pressure_trend").GetString());
        Assert.Equal(3, clock.GetProperty("pressure_delta_3h").GetInt32());
        Assert.Equal("Europe/Kyiv", clock.GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task Series_MarksAGapWithNull_AndNeverDrawsAcrossIt()
    {
        var account = await SeedAccountAsync();
        var now = DateTimeOffset.UtcNow;
        var (device, _) = await SeedClockAsync(account, "bme280", now.AddHours(-12));
        // Both early samples inside one ten minute bucket, whatever minute the test runs in.
        var early = DateTimeOffset.FromUnixTimeSeconds(now.AddHours(-6).ToUnixTimeSeconds() / 600 * 600).AddMinutes(1);
        await InsertAsync(device,
            new TelemetrySample(early, 200, 750, 40),
            new TelemetrySample(early.AddMinutes(1), 210, 750, 40),
            new TelemetrySample(now.AddHours(-1), 230, 751, 41));

        var series = await GetJsonAsync(account, $"/api/devices/{device}/series?metric=temp&period=day");

        Assert.Equal(600, series.GetProperty("step_s").GetInt32());
        var points = series.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal(20.5, points[0][1].GetDouble());
        Assert.Equal(JsonValueKind.Null, points[1][1].ValueKind);
        Assert.Equal(23.0, points[2][1].GetDouble());

        var week = await GetJsonAsync(account, $"/api/devices/{device}/series?metric=pressure&period=week");
        Assert.Equal(3600, week.GetProperty("step_s").GetInt32());
        Assert.Contains(week.GetProperty("points").EnumerateArray(), p => p[1].ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Series_RejectsAnUnknownMetricOrPeriod()
    {
        var account = await SeedAccountAsync();
        var (device, _) = await SeedClockAsync(account, "bme280", DateTimeOffset.UtcNow.AddHours(-1));

        var client = CreateClient(account);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/devices/{device}/series?metric=temperature_dc&period=day")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/devices/{device}/series?metric=temp&period=decade")).StatusCode);
    }

    /// <summary>
    /// A owns the clock, gives it to B, and later takes it back. A's current chart must hold
    /// only the second period, the first must be in A's history, and B must see neither of
    /// A's periods — not through /api/devices, and not by guessing a binding id.
    /// </summary>
    [Fact]
    public async Task OwnershipPeriods_StaySeparate_AcrossABA()
    {
        var accountA = await SeedAccountAsync();
        var accountB = await SeedAccountAsync();
        var now = DateTimeOffset.UtcNow;

        var (device, firstA) = await SeedClockAsync(accountA, "bme280", now.AddHours(-10));
        await InsertAsync(device, new TelemetrySample(now.AddHours(-9), 100, 740, 30));
        await CloseAsync(firstA, now.AddHours(-8), "re-enrolled");

        var bindingB = await BindAsync(device, accountB, now.AddHours(-8));
        await InsertAsync(device, new TelemetrySample(now.AddHours(-7), 200, 750, 50));
        await CloseAsync(bindingB, now.AddHours(-6), "re-enrolled");

        await BindAsync(device, accountA, now.AddHours(-6));
        await InsertAsync(device, new TelemetrySample(now.AddHours(-5), 300, 760, 60));

        var current = await GetJsonAsync(accountA, $"/api/devices/{device}/series?metric=temp&period=day");
        Assert.Equal([30.0], current.GetProperty("points").EnumerateArray().Select(p => p[1].GetDouble()));

        var history = (await GetJsonAsync(accountA, "/api/history")).EnumerateArray().ToList();
        var former = Assert.Single(history, h => h.GetProperty("device_id").GetInt64() == device);
        Assert.Equal(firstA, former.GetProperty("binding_id").GetInt64());
        Assert.Equal("re-enrolled", former.GetProperty("unbound_reason").GetString());

        var formerSeries = await GetJsonAsync(accountA, $"/api/history/{firstA}/series?metric=temp&period=day");
        Assert.Equal([10.0], formerSeries.GetProperty("points").EnumerateArray().Select(p => p[1].GetDouble()));

        var clientB = CreateClient(accountB);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/devices/{device}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/history/{firstA}")).StatusCode);
        var historyB = (await GetJsonAsync(accountB, "/api/history")).EnumerateArray();
        Assert.Equal([bindingB], historyB.Select(h => h.GetProperty("binding_id").GetInt64()));
        var seriesB = await GetJsonAsync(accountB, $"/api/history/{bindingB}/series?metric=temp&period=day");
        Assert.Equal([20.0], seriesB.GetProperty("points").EnumerateArray().Select(p => p[1].GetDouble()));
    }

    [Fact]
    public async Task Patch_BindsSnakeCase_AndRefusesAZonePostgresWouldNotKnow()
    {
        var account = await SeedAccountAsync();
        var (device, _) = await SeedClockAsync(account, "bme280", DateTimeOffset.UtcNow.AddHours(-1));
        var client = CreateClient(account);

        var renamed = await client.PatchAsJsonAsync($"/api/devices/{device}", new { display_name = "  Спальня ", timezone = "Europe/Warsaw" });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        var clock = await GetJsonAsync(account, $"/api/devices/{device}");
        Assert.Equal("Спальня", clock.GetProperty("display_name").GetString());
        Assert.Equal("Europe/Warsaw", clock.GetProperty("timezone").GetString());

        foreach (var zone in new[] { "FLE Standard Time", "Mars/Olympus", "" })
        {
            var refused = await client.PatchAsJsonAsync($"/api/devices/{device}", new { timezone = zone });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
    }

    [Fact]
    public async Task EnrollmentCode_IsListedUnderTheIdItWasCreatedWith()
    {
        var account = await SeedAccountAsync();
        var client = CreateClient(account);

        var created = await client.PostAsync("/api/enrollment-codes", null);
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString();
        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}$", body.GetProperty("code").GetString());

        var listed = Assert.Single((await GetJsonAsync(account, "/api/enrollment-codes")).EnumerateArray());
        Assert.Equal(id, listed.GetProperty("id").GetString());
        Assert.Equal("active", listed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("consumed_device_id").ValueKind);
    }

    private HttpClient CreateClient(Guid accountId)
    {
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity([new Claim(AccountClaimTypes.AccountId, accountId.ToString())], "Google");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
        client.DefaultRequestHeaders.Add("X-Requested-With", "test");
        return client;
    }

    private async Task<JsonElement> GetJsonAsync(Guid accountId, string path)
    {
        var response = await CreateClient(accountId).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Guid> SeedAccountAsync()
    {
        var accountId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ClockDbContext>()
            .UseNpgsql(fixture.MigratorConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new ClockDbContext(options, new NoAccount());
        db.Accounts.Add(new Account { Id = accountId, Email = $"{accountId}@example.test", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return accountId;
    }

    private async Task<(long Device, long Binding)> SeedClockAsync(Guid accountId, string model, DateTimeOffset boundAt)
    {
        await using var ingest = CreateIngestDbContext();
        var device = new Device { HardwareId = $"cabinet-{Guid.NewGuid():N}", Profile = "esp8266-1m", Model = model };
        ingest.Devices.Add(device);
        await ingest.SaveChangesAsync();
        ingest.DeviceStates.Add(new DeviceState { DeviceId = device.Id, LastIngestAt = DateTimeOffset.UtcNow, FwVersion = "2026.10.24" });
        await ingest.SaveChangesAsync();
        return (device.Id, await BindAsync(device.Id, accountId, boundAt));
    }

    private async Task<long> BindAsync(long deviceId, Guid accountId, DateTimeOffset boundAt)
    {
        await using var ingest = CreateIngestDbContext();
        var binding = new DeviceBinding { DeviceId = deviceId, AccountId = accountId, BoundAt = boundAt };
        ingest.DeviceBindings.Add(binding);
        await ingest.SaveChangesAsync();
        ingest.DeviceAccesses.Add(new DeviceAccess { BindingId = binding.Id, AccountId = accountId, Role = DeviceAccessRole.Owner });
        await ingest.SaveChangesAsync();
        return binding.Id;
    }

    private async Task CloseAsync(long bindingId, DateTimeOffset at, string reason)
    {
        await using var ingest = CreateIngestDbContext();
        var binding = await ingest.DeviceBindings.SingleAsync(b => b.Id == bindingId);
        binding.UnboundAt = at;
        binding.UnboundReason = reason;
        await ingest.SaveChangesAsync();
    }

    private async Task InsertAsync(long deviceId, params TelemetrySample[] samples)
    {
        await using var ingest = CreateIngestDbContext();
        Assert.Equal(samples.Length, await new TelemetryBatchWriter(ingest).InsertAsync(deviceId, samples));
    }

    private IngestDbContext CreateIngestDbContext() =>
        new(new DbContextOptionsBuilder<IngestDbContext>()
            .UseNpgsql(fixture.IngestConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    private sealed class NoAccount : ICurrentAccountAccessor
    {
        public Guid? AccountId => null;
    }
}
