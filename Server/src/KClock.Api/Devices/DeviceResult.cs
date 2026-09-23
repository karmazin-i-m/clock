namespace KClock.Api.Devices;

/// <summary>
/// What a /d/v1 handler returns: ResponseBudgetFilter is the only thing that turns this into
/// bytes on the wire, so every /d/v1 handler goes through the same size-budget and
/// Content-Length enforcement (DESIGN.md §5 rule 2, rule 4) with no per-endpoint bypass.
/// </summary>
public sealed record DeviceResult(int StatusCode, object Body, int? RetryAfterSeconds = null)
{
    public static DeviceResult Ok(object body) => new(StatusCodes.Status200OK, body);

    public static DeviceResult Error(int statusCode, string err) => new(statusCode, new ErrorResponse(err));

    public static DeviceResult BadRequest() => Error(StatusCodes.Status400BadRequest, "bad_request");

    public static DeviceResult SlowDown(int retryAfterSeconds) =>
        new(StatusCodes.Status429TooManyRequests, new ErrorResponse("slow_down"), retryAfterSeconds);
}
