using System.Text.Json;
using KClock.Api;
using KClock.Api.Accounts;
using KClock.Api.Devices;
using KClock.Api.Infrastructure;
using KClock.Data;
using KClock.Data.Ingest;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// Must run before anything else touches Request.Scheme — behind Caddy it is otherwise "http",
// and any redirect_uri built from it (including Google's) is wrong (Ops papercut, DESIGN.md
// §12). KnownIPNetworks/KnownProxies are cleared rather than left at their (loopback-only)
// default because Caddy reaches the API over the compose network, not localhost; otherwise the
// headers are silently ignored.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Persisted to a named volume with an explicit SetApplicationName — the default path inside
// the container is destroyed on every redeploy, silently signing every user out in a way that
// reads as an auth bug and is not (DESIGN.md §12, Ops papercut 1).
builder.Services.AddDataProtection()
    .SetApplicationName("kclock")
    .PersistKeysToFileSystem(new DirectoryInfo(
        builder.Configuration["DataProtectionKeysPath"] ?? "/data/dp-keys"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

// clock_app and clock_ingest are separate Postgres roles reached through separate
// connections/DbContexts (DESIGN.md §8's three-role model) — see ClockDbContext/
// IngestDbContext's own doc comments for why this can't be one context. Max Pool Size=20
// explicit on both: the default of 100 x two contexts x replicas would exceed a
// max_connections of 100 (DESIGN.md §8).
builder.Services.AddDbContext<IngestDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Ingest") ?? "Host=localhost;Database=kclock;Maximum Pool Size=20")
        .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<ICurrentAccountAccessor, HttpContextAccountAccessor>();
builder.Services.AddDbContext<ClockDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("App") ?? "Host=localhost;Database=kclock;Maximum Pool Size=20")
        .UseSnakeCaseNamingConvention());

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        // HttpOnly + Secure + SameSite=Lax, never a JWT in localStorage (DESIGN.md §12). Same
        // origin (via Caddy) is what makes this cheap — no CORS, no token storage, no XSS
        // exfiltration path, no refresh rotation to build.
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/auth/google/start";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    })
    .AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? string.Empty;
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? string.Empty;
        options.CallbackPath = "/auth/google/callback";
        options.SaveTokens = false;
        options.Events.OnCreatingTicket = GoogleAuthEndpoints.OnCreatingTicketAsync;
    })
    .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(
        DeviceTokenAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    // 10/min/IP on enrollment (DESIGN.md §5.1) — RemoteIpAddress is only correct once
    // ForwardedHeaders has already run, which UseForwardedHeaders below guarantees.
    options.AddFixedWindowLimiter("enroll", limiterOptions =>
    {
        limiterOptions.PermitLimit = 10;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });

    options.OnRejected = async (context, ct) =>
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ErrorResponse("slow_down"), AppJsonContext.Default.ErrorResponse);
        context.HttpContext.Response.Headers.RetryAfter = "60";
        context.HttpContext.Response.ContentType = "application/json";
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentLength = bytes.Length;
        await context.HttpContext.Response.Body.WriteAsync(bytes, ct);
    };
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();

// Unauthenticated, no DB round trip — liveness only.
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));

// Mapped directly on `app`, not inside any account-scoped group, so it is exempt from the
// account-scope transaction by construction rather than by an exception list (DESIGN.md §12).
app.MapGet("/health/ready", async (IngestDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.UseHttpMetrics();
app.MapMetrics();

// This path is frozen (DESIGN.md §5) — v1 is additive-only forever, and a /d/v2 would only
// appear if the encoding itself changed.
var deviceGroup = app.MapGroup("/d/v1").AddEndpointFilter<ResponseBudgetFilter>();

deviceGroup.MapPost("/enroll", EnrollEndpoint.HandleAsync)
    .RequireRateLimiting("enroll");

deviceGroup.MapPost("/telemetry", TelemetryEndpoint.HandleAsync)
    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

// No MapGet for /auth/google/callback — GoogleOptions.CallbackPath above is what makes the
// OAuth handler's own middleware answer that path before routing ever sees it.
app.MapGet("/auth/google/start", GoogleAuthEndpoints.Start);

var apiGroup = app.MapGroup("/api")
    .RequireAuthorization()
    .AddEndpointFilter<RequestedWithCsrfFilter>()
    .AddEndpointFilter<AccountScopeFilter>();

apiGroup.MapPost("/auth/signout", (Delegate)GoogleAuthEndpoints.SignOutAsync);
apiGroup.MapGet("/me", MeEndpoint.HandleAsync);
apiGroup.MapGet("/devices", DevicesEndpoints.ListAsync);
apiGroup.MapGet("/devices/{id:long}", DevicesEndpoints.GetAsync);
apiGroup.MapPatch("/devices/{id:long}", DevicesEndpoints.UpdateAsync);
apiGroup.MapDelete("/devices/{id:long}/binding", DevicesEndpoints.UnbindAsync);
apiGroup.MapPost("/enrollment-codes", EnrollmentCodesEndpoints.CreateAsync);
apiGroup.MapGet("/enrollment-codes", EnrollmentCodesEndpoints.ListAsync);
apiGroup.MapDelete("/enrollment-codes", EnrollmentCodesEndpoints.DeleteUnconsumedAsync);

app.Run();

// WebApplicationFactory<Program> needs this to see the top-level statements above as a type.
public partial class Program;
