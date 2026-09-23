namespace KClock.Api.Infrastructure;

/// <summary>
/// SameSite=Lax on the session cookie already blocks cross-site POSTs; this adds the same
/// guarantee antiforgery tokens give, in three lines: X-Requested-With cannot be set
/// cross-origin without a preflight that fails (DESIGN.md §12).
/// </summary>
public sealed class RequestedWithCsrfFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
            && !request.Headers.ContainsKey("X-Requested-With"))
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            return ValueTask.FromResult<object?>(Results.Empty);
        }

        return next(context);
    }
}
