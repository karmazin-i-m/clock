using System.Text.Json.Serialization;

namespace KClock.Api.Devices;

// The frozen /d/v1 wire contract (DESIGN.md §5). Every property name below is what actually
// crosses the wire — changing one is a firmware-breaking change to a fleet that cannot be
// conveniently reflashed. Every key must stay unique as a substring across this whole file and
// never appear inside a string value (§5 rule 5), so a device too tight on flash to carry
// ArduinoJson can still parse with strstr + atoi.

public record EnrollRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("hw")] string Hw,
    [property: JsonPropertyName("fw")] string? Fw,
    [property: JsonPropertyName("mac")] string Mac,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("chip")] string? Chip);

public record PolicyDto(
    [property: JsonPropertyName("sample_s")] int SampleS,
    [property: JsonPropertyName("flush_s")] int FlushS,
    [property: JsonPropertyName("max_batch")] int MaxBatch,
    [property: JsonPropertyName("v")] int V);

// device_id is rendered as a JSON number, not a string — DESIGN.md §5.1's own example shows it
// quoted ("7f3ab2c1-…"), but §7's schema declares device.id as a plain bigint identity column,
// and the rest of the frozen contract (seq, ts, cfgv) renders integers unquoted. Treated here
// as a doc inconsistency rather than a deliberate string-typed id; flagged in Server/README.md.
public record EnrollResponse(
    [property: JsonPropertyName("device_id")] long DeviceId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("policy")] PolicyDto Policy,
    [property: JsonPropertyName("server_ts")] long ServerTs);

public record ErrorResponse(
    [property: JsonPropertyName("err")] string Err);

public record TelemetrySampleDto(
    [property: JsonPropertyName("ts")] long Ts,
    [property: JsonPropertyName("tc")] short? Tc,
    [property: JsonPropertyName("p")] short? P,
    [property: JsonPropertyName("h")] short? H);

public record TelemetryRequest(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("cfgv")] int Cfgv,
    [property: JsonPropertyName("samples")] List<TelemetrySampleDto> Samples);

public record TelemetryResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("dup")] int Dup,
    [property: JsonPropertyName("policy")] PolicyDto Policy);
