// KClock.FakeDevice — a tool, not a test project (DESIGN.md §13). Zero project references to
// KClock.Api/KClock.Data on purpose: it hand-builds JSON independently so it stays an honest,
// independent characterization of the /d/v1 wire contract rather than one that would pass
// trivially if AppJsonContext itself had a bug. This is what lets the server be finished and
// trusted before the firmware has an HTTP client at all.
//
// Usage:
//   dotnet run --project tools/KClock.FakeDevice -- --url https://api.example.com --code XXXX-XXXX
//     [--drop-rate 0.1] [--skew-seconds 0] [--fast] [--profile esp8266-1m] [--model bme280]

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = ParseArgs(args);
using var http = new HttpClient { BaseAddress = new Uri(options.Url) };

var mac = Random.Shared.NextInt64().ToString("X12");
Console.WriteLine($"[fake-device] mac={mac} profile={options.Profile} model={options.Model}");

var (token, policy) = await EnrollAsync(http, options, mac);
Console.WriteLine($"[fake-device] enrolled, token={token[..12]}... policy={policy}");

var ring = new List<(long Ts, short Tc, short P, short H)>();
long seq = 0;
var backoffIndex = 0;
int[] backoffMinutes = [1, 2, 5, 10, 15];
var lastFlush = DateTimeOffset.UtcNow;
var lastSample = DateTimeOffset.UtcNow;
var reprobeAt = DateTimeOffset.MinValue;
var muted = false;

while (true)
{
    var now = DateTimeOffset.UtcNow;

    if (now - lastSample >= TimeSpan.FromSeconds(policy.SampleS))
    {
        lastSample = now;
        var wireTs = now.AddSeconds(options.SkewSeconds).ToUnixTimeSeconds();
        ring.Add((wireTs, (short)(210 + Random.Shared.Next(-5, 5)), (short)(745 + Random.Shared.Next(-3, 3)), (short)(40 + Random.Shared.Next(-2, 2))));
    }

    var dueForFlush = now - lastFlush >= TimeSpan.FromSeconds(policy.FlushS) && ring.Count > 0;
    if (muted && now < reprobeAt)
    {
        dueForFlush = false;
    }

    if (dueForFlush)
    {
        lastFlush = now;
        seq++;

        if (Random.Shared.NextDouble() < options.DropRate)
        {
            Console.WriteLine($"[fake-device] seq={seq} dropped on purpose (--drop-rate), samples stay in the ring");
        }
        else
        {
            var batch = ring.Take(policy.MaxBatch).ToList();
            var (status, retryAfter, newPolicy) = await SendTelemetryAsync(http, token, seq, batch);
            await HandleStatusAsync(status, retryAfter);

            switch (status)
            {
                case HttpStatusCode.OK:
                    ring.RemoveRange(0, batch.Count);
                    backoffIndex = 0;
                    muted = false;
                    if (newPolicy is { } p)
                    {
                        policy = p;
                    }

                    break;
                case HttpStatusCode.BadRequest:
                    // Never retry a 400 — a poison batch retried forever wedges the ring
                    // permanently (DESIGN.md §5.3).
                    ring.RemoveRange(0, batch.Count);
                    break;
                case HttpStatusCode.Unauthorized:
                    // Stop sending; KEEP the token; re-probe at most hourly. A 401 is a state,
                    // not an event — the token is erased only on a successful new enrollment
                    // (DESIGN.md §5.3).
                    muted = true;
                    reprobeAt = now.AddHours(1);
                    Console.WriteLine("[fake-device] 401 — не прив'язано; keeping token, re-probing in an hour");
                    break;
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.NotFound:
                    muted = true;
                    reprobeAt = now.AddHours(1);
                    break;
                case HttpStatusCode.RequestEntityTooLarge:
                    // Halve the batch, retry once, then treat as 400 if it still doesn't fit.
                    var halved = batch.Take(Math.Max(1, batch.Count / 2)).ToList();
                    var (retryStatus, _, _) = await SendTelemetryAsync(http, token, seq, halved);
                    if (retryStatus == HttpStatusCode.OK)
                    {
                        ring.RemoveRange(0, halved.Count);
                    }
                    else
                    {
                        ring.RemoveRange(0, batch.Count);
                    }

                    break;
                case HttpStatusCode.TooManyRequests:
                    reprobeAt = now.Add(retryAfter ?? TimeSpan.FromSeconds(60));
                    muted = true;
                    break;
                default:
                    // 408, 5xx, timeout: keep the samples, back off.
                    var minutes = backoffMinutes[Math.Min(backoffIndex, backoffMinutes.Length - 1)];
                    backoffIndex++;
                    reprobeAt = now.AddMinutes(minutes);
                    muted = true;
                    Console.WriteLine($"[fake-device] status={(int)status}, backing off {minutes} min");
                    break;
            }
        }
    }

    // Occasionally simulate a reboot: seq resets, the token survives (DESIGN.md §13).
    if (options.Fast && Random.Shared.NextDouble() < 0.01)
    {
        Console.WriteLine("[fake-device] simulated reboot — seq reset, token kept");
        seq = 0;
    }

    await Task.Delay(options.Fast ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(1));
}

static Task HandleStatusAsync(HttpStatusCode status, TimeSpan? retryAfter)
{
    Console.WriteLine($"[fake-device] telemetry -> {(int)status}{(retryAfter is { } r ? $" retry-after={r.TotalSeconds}s" : string.Empty)}");
    return Task.CompletedTask;
}

static async Task<(string Token, Policy Policy)> EnrollAsync(HttpClient http, Options options, string mac)
{
    var body = new JsonObject
    {
        ["code"] = options.Code,
        ["hw"] = options.Profile,
        ["fw"] = "fake-device",
        ["mac"] = mac,
        ["model"] = options.Model,
        ["chip"] = "deadbeef",
    };

    var response = await http.PostAsync("/d/v1/enroll", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    response.EnsureSuccessStatusCode();
    var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    var token = json["token"]!.GetValue<string>();
    var policy = ParsePolicy(json["policy"]!.AsObject());
    return (token, policy);
}

static async Task<(HttpStatusCode Status, TimeSpan? RetryAfter, Policy? Policy)> SendTelemetryAsync(
    HttpClient http, string token, long seq, List<(long Ts, short Tc, short P, short H)> batch)
{
    var samples = new JsonArray();
    foreach (var s in batch)
    {
        samples.Add(new JsonObject { ["ts"] = s.Ts, ["tc"] = s.Tc, ["p"] = s.P, ["h"] = s.H });
    }

    var body = new JsonObject { ["seq"] = seq, ["cfgv"] = 0, ["samples"] = samples };

    using var request = new HttpRequestMessage(HttpMethod.Post, "/d/v1/telemetry")
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    var response = await http.SendAsync(request);
    Policy? policy = null;
    if (response.IsSuccessStatusCode)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        if (json["policy"] is JsonObject p)
        {
            policy = ParsePolicy(p);
        }
    }

    return (response.StatusCode, response.Headers.RetryAfter?.Delta, policy);
}

static Policy ParsePolicy(JsonObject p) => new(
    p["sample_s"]!.GetValue<int>(),
    p["flush_s"]!.GetValue<int>(),
    p["max_batch"]!.GetValue<int>());

static Options ParseArgs(string[] args)
{
    string? url = null;
    string? code = null;
    var dropRate = 0.0;
    var skewSeconds = 0;
    var fast = false;
    var profile = "esp8266-1m";
    var model = "bme280";

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--url": url = args[++i]; break;
            case "--code": code = args[++i]; break;
            case "--drop-rate": dropRate = double.Parse(args[++i]); break;
            case "--skew-seconds": skewSeconds = int.Parse(args[++i]); break;
            case "--fast": fast = true; break;
            case "--profile": profile = args[++i]; break;
            case "--model": model = args[++i]; break;
        }
    }

    if (url is null || code is null)
    {
        Console.Error.WriteLine("Usage: KClock.FakeDevice --url <base-url> --code <enrollment-code> [--drop-rate 0.1] [--skew-seconds 0] [--fast] [--profile esp8266-1m] [--model bme280]");
        Environment.Exit(1);
    }

    return new Options(url!, code!, dropRate, skewSeconds, fast, profile, model);
}

record Options(string Url, string Code, double DropRate, int SkewSeconds, bool Fast, string Profile, string Model);

record Policy(int SampleS, int FlushS, int MaxBatch)
{
    public override string ToString() => $"sample_s={SampleS} flush_s={FlushS} max_batch={MaxBatch}";
}
