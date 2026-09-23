using System.Text.Json;
using KClock.Api.Devices;

namespace KClock.Api.Infrastructure;

/// <summary>
/// The runtime half of DESIGN.md §5 rules 2-4: every /d/v1 response is a JSON object, never
/// exceeds the byte budget, always carries an explicit Content-Length, and is never
/// Content-Encoded (Caddy's `encode` directive excludes /d/* separately — see the Caddyfile).
/// A device contract test is the other half; this filter is what makes a budget violation a
/// loud 500 here rather than a silently oversized response reaching a 40 KB heap in the field.
/// </summary>
public sealed class ResponseBudgetFilter(int maxBytes = 512) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        if (result is not DeviceResult deviceResult)
        {
            return result;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(deviceResult.Body, deviceResult.Body.GetType(), AppJsonContext.Default);
        if (bytes.Length > maxBytes)
        {
            throw new InvalidOperationException(
                $"/d/v1 response of {bytes.Length} bytes exceeds the {maxBytes} byte budget (DESIGN.md §5 rule 2): {context.HttpContext.Request.Path}");
        }

        // Handed back as an IResult rather than written here: whatever a filter returns, the
        // endpoint's result handler still executes it. Writing the body here and returning
        // null made that handler serialize a second response into one that had already
        // started, so every /d/v1 request ended in an unhandled exception and Kestrel dropped
        // the connection. That costs the device its keep-alive on every flush.
        return new BudgetedResult(deviceResult.StatusCode, bytes, deviceResult.RetryAfterSeconds);
    }

    private sealed class BudgetedResult(int statusCode, byte[] body, int? retryAfterSeconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentLength = body.Length;
            if (retryAfterSeconds is { } retryAfter)
            {
                response.Headers.RetryAfter = retryAfter.ToString();
            }

            return response.Body.WriteAsync(body, httpContext.RequestAborted).AsTask();
        }
    }
}
