using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KClock.Tests.Fixtures;
using Npgsql;
using Xunit;

namespace KClock.Tests.Contract;

/// <summary>
/// Posts the literal bytes the firmware will send (DESIGN.md §13) — a representative subset of
/// the full case list, not the exhaustive one: happy-path enroll and telemetry, duplicate
/// batch, revoked token, and the BMP280 -> NULL humidity rule the §15 review checklist calls
/// out by name. Requires Docker (via PostgresFixture); see that fixture for why discovery
/// still works without it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DeviceContractTests(PostgresFixture fixture) : IDisposable
{
    private readonly AppFactory _factory = new(fixture);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Enroll_HappyPath_RespectsBudgetAndReturnsPolicy()
    {
        var accountId = await SeedAccountAsync();
        var (code, mac) = await SeedEnrollmentCodeAsync(accountId);

        var requestJson = await LoadFixtureAsync("enroll_request.json", ("__CODE__", code), ("__MAC__", mac));
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/d/v1/enroll", new StringContent(requestJson, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.Length <= 512, $"enroll response is {body.Length} bytes, over the DESIGN.md §5 budget");
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);

        using var doc = JsonDocument.Parse(body);
        Assert.StartsWith("kcd1_", doc.RootElement.GetProperty("token").GetString());
        var policy = doc.RootElement.GetProperty("policy");
        Assert.Equal(10, policy.GetProperty("sample_s").GetInt32());
        Assert.Equal(60, policy.GetProperty("flush_s").GetInt32());
        Assert.Equal(12, policy.GetProperty("max_batch").GetInt32());
    }

    [Fact]
    public async Task Telemetry_DuplicateBatch_CountsAsDupNotAccepted()
    {
        var accountId = await SeedAccountAsync();
        var (code, mac) = await SeedEnrollmentCodeAsync(accountId);
        var token = await EnrollAsync(code, mac);

        var now = DateTimeOffset.UtcNow;
        // After enrolment, not before: a sample older than the binding belongs to nobody and is
        // discarded by design (DESIGN.md §9), so pre-enrolment timestamps would test nothing.
        var ts = Enumerable.Range(0, 6).Select(i => now.AddSeconds(1 + (i * 10)).ToUnixTimeSeconds()).ToArray();
        var requestJson = await LoadFixtureAsync(
            "telemetry_request.json",
            ("__TS0__", ts[0].ToString()), ("__TS1__", ts[1].ToString()), ("__TS2__", ts[2].ToString()),
            ("__TS3__", ts[3].ToString()), ("__TS4__", ts[4].ToString()), ("__TS5__", ts[5].ToString()));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await PostTelemetryAsync(client, requestJson);
        Assert.Equal(6, first.GetProperty("accepted").GetInt32());
        Assert.Equal(0, first.GetProperty("dup").GetInt32());

        // A device that retried after a timeout it never saw the answer to must be safe to
        // replay — idempotent by construction on (device_id, ts) (DESIGN.md §5.4).
        var second = await PostTelemetryAsync(client, requestJson);
        Assert.Equal(0, second.GetProperty("accepted").GetInt32());
        Assert.Equal(6, second.GetProperty("dup").GetInt32());
    }

    [Fact]
    public async Task Telemetry_RevokedToken_Returns401()
    {
        var accountId = await SeedAccountAsync();
        var (code, mac) = await SeedEnrollmentCodeAsync(accountId);
        var firstToken = await EnrollAsync(code, mac);

        // Re-enrolling the same MAC revokes the first credential — this is the transfer
        // mechanism itself, not a side effect (DESIGN.md §5.1).
        var (secondCode, _) = await SeedEnrollmentCodeAsync(accountId);
        await EnrollAsync(secondCode, mac);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstToken);
        var response = await client.PostAsync(
            "/d/v1/telemetry",
            new StringContent("""{"seq":1,"cfgv":0,"samples":[]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // And the first binding is closed, not merely orphaned: exactly one open binding,
        // the old one stamped with why it ended (DESIGN.md §5.1).
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FILTER (WHERE b.unbound_at IS NULL),
                   count(*) FILTER (WHERE b.unbound_reason = 're-enrolled')
            FROM device_binding b JOIN device d ON d.id = b.device_id
            WHERE d.hardware_id = @mac
            """,
            conn);
        cmd.Parameters.AddWithValue("mac", mac);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Telemetry_OutOfOrderBatch_IsAcceptedWhole()
    {
        var client = await EnrolledClientAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1;

        var result = await PostTelemetryAsync(
            client,
            $$"""{"seq":1,"cfgv":0,"samples":[{"ts":{{now + 20}},"tc":1},{"ts":{{now}},"tc":2},{"ts":{{now + 10}},"tc":3}]}""");

        Assert.Equal(3, result.GetProperty("accepted").GetInt32());
        Assert.Equal(0, result.GetProperty("dup").GetInt32());
    }

    [Fact]
    public async Task Telemetry_AllSamplesOutOfWindow_Returns400WithTinyBody()
    {
        var client = await EnrolledClientAsync();
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var (status, body) = await PostRawAsync(
            client, "/d/v1/telemetry", $$"""{"seq":1,"cfgv":0,"samples":[{"ts":{{future}},"tc":1}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("""{"err":"bad_request"}""", body);
    }

    [Fact]
    public async Task Telemetry_FutureSampleInsideGoodBatch_IsDroppedNotRejected()
    {
        var client = await EnrolledClientAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1;

        var result = await PostTelemetryAsync(
            client,
            $$"""{"seq":1,"cfgv":0,"samples":[{"ts":{{now}},"tc":1},{"ts":{{now + 3600}},"tc":2}]}""");

        Assert.Equal(1, result.GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task Telemetry_HumidityOutOfRange_StoresNull_InsteadOfFailingTheBatch()
    {
        var client = await EnrolledClientAsync();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1;

        // 101 would trip the CHECK; as a 500 the device would retry the batch for ever (§5.3).
        var result = await PostTelemetryAsync(
            client, $$"""{"seq":1,"cfgv":0,"samples":[{"ts":{{ts}},"tc":214,"p":746,"h":101}]}""");
        Assert.Equal(1, result.GetProperty("accepted").GetInt32());

        await using var conn = new NpgsqlConnection(fixture.IngestConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT humidity_pct FROM telemetry WHERE ts = @ts", conn);
        cmd.Parameters.AddWithValue("ts", DateTimeOffset.FromUnixTimeSeconds(ts));
        Assert.True(await cmd.ExecuteScalarAsync() is null or DBNull);
    }

    [Fact]
    public async Task Telemetry_MissingSamples_Returns400_Not500()
    {
        var client = await EnrolledClientAsync();

        var (status, body) = await PostRawAsync(client, "/d/v1/telemetry", """{"seq":1,"cfgv":0}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("""{"err":"bad_request"}""", body);
    }

    [Fact]
    public async Task Enroll_EleventhAttemptInAMinute_IsRateLimited()
    {
        var client = _factory.CreateClient();
        const string request = """{"code":"ZZZZ-ZZZZ","hw":"esp8266-1m","mac":"000000000000"}""";

        for (var i = 0; i < 10; i++)
        {
            var (status, _) = await PostRawAsync(client, "/d/v1/enroll", request);
            Assert.Equal(HttpStatusCode.BadRequest, status);
        }

        var response = await client.PostAsync("/d/v1/enroll", new StringContent(request, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("""{"err":"slow_down"}""", await response.Content.ReadAsStringAsync());
        Assert.NotNull(response.Headers.RetryAfter);
    }

    private async Task<HttpClient> EnrolledClientAsync()
    {
        var accountId = await SeedAccountAsync();
        var (code, mac) = await SeedEnrollmentCodeAsync(accountId);
        var token = await EnrollAsync(code, mac);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostRawAsync(HttpClient client, string path, string json)
    {
        var response = await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Telemetry_Bmp280Board_StoresNullHumidity_NeverMinusOne()
    {
        var accountId = await SeedAccountAsync();
        var (code, mac) = await SeedEnrollmentCodeAsync(accountId);
        var token = await EnrollAsync(code, mac, model: "bmp280");

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1;
        var requestJson = await LoadFixtureAsync("telemetry_request_bmp280.json", ("__TS0__", ts.ToString()));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var result = await PostTelemetryAsync(client, requestJson);
        Assert.Equal(1, result.GetProperty("accepted").GetInt32());

        // Storing -1 instead of NULL is on the §15 review blocklist by name — assert the
        // database column directly, not just the response shape.
        await using var conn = new NpgsqlConnection(fixture.IngestConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT humidity_pct FROM telemetry WHERE ts = @ts", conn);
        cmd.Parameters.AddWithValue("ts", DateTimeOffset.FromUnixTimeSeconds(ts));
        var humidity = await cmd.ExecuteScalarAsync();
        Assert.True(humidity is null or DBNull);
    }

    private static async Task<JsonElement> PostTelemetryAsync(HttpClient client, string requestJson)
    {
        var response = await client.PostAsync("/d/v1/telemetry", new StringContent(requestJson, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return doc.RootElement.Clone();
    }

    private async Task<string> EnrollAsync(string code, string mac, string model = "bme280")
    {
        var requestJson = await LoadFixtureAsync("enroll_request.json", ("__CODE__", code), ("__MAC__", mac));
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/d/v1/enroll", new StringContent(requestJson, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<string> LoadFixtureAsync(string name, params (string Placeholder, string Value)[] substitutions)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RawFixtures", name);
        var text = await File.ReadAllTextAsync(path);
        foreach (var (placeholder, value) in substitutions)
        {
            text = text.Replace(placeholder, value);
        }

        return text;
    }

    private async Task<Guid> SeedAccountAsync()
    {
        var accountId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO account (id, email, created_at) VALUES (@id, @email, now())", conn);
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("email", $"{accountId}@example.test");
        await cmd.ExecuteNonQueryAsync();
        return accountId;
    }

    private async Task<(string Code, string Mac)> SeedEnrollmentCodeAsync(Guid accountId)
    {
        var code = $"T{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        var mac = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO enrollment_code (code_hash, account_id, expires_at) VALUES (@hash, @account, now() + interval '15 minutes')", conn);
        cmd.Parameters.AddWithValue("hash", codeHash);
        cmd.Parameters.AddWithValue("account", accountId);
        await cmd.ExecuteNonQueryAsync();

        return (code, mac);
    }
}
