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
        var ts = Enumerable.Range(0, 6).Select(i => now.AddSeconds(-60 + (i * 10)).ToUnixTimeSeconds()).ToArray();
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
    }

    [Fact]
    public async Task Telemetry_Bmp280Board_StoresNullHumidity_NeverMinusOne()
    {
        var accountId = await SeedAccountAsync();
        var (code, mac) = await SeedEnrollmentCodeAsync(accountId);
        var token = await EnrollAsync(code, mac, model: "bmp280");

        var ts = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
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
