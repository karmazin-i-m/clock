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

        var response = context.HttpContext.Response;
        response.StatusCode = deviceResult.StatusCode;
        response.ContentType = "application/json";
        response.ContentLength = bytes.Length;
        if (deviceResult.RetryAfterSeconds is { } retryAfter)
        {
            response.Headers.RetryAfter = retryAfter.ToString();
        }

        await response.Body.WriteAsync(bytes, context.HttpContext.RequestAborted);
        return null;
    }
}
