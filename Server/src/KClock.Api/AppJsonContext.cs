using System.Text.Json.Serialization;
using KClock.Api.Devices;

namespace KClock.Api;

/// <summary>
/// Source-generated, reflection-free JSON for the /d/v1 device contract only — never mixed
/// with /api/* browser DTOs, which are ordinary reflection-based System.Text.Json since they
/// carry no heap-constrained-device budget. This is deliberately the one file where the
/// frozen wire contract is visible end to end (DESIGN.md §5).
/// </summary>
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(TelemetryRequest))]
[JsonSerializable(typeof(TelemetryResponse))]
public partial class AppJsonContext : JsonSerializerContext;
