using System.Security.Claims;
using KClock.Api.Accounts;
using KClock.Data;
using KClock.Tests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace KClock.Tests.Contract;

/// <summary>
/// Points the app's IngestDbContext at the shared PostgresFixture container instead of
/// whatever appsettings/environment configuration a real deploy would use — everything else
/// about Program.cs runs unmodified, which is the point of a characterization test
/// (DESIGN.md §13).
/// </summary>
public sealed class AppFactory(PostgresFixture fixture) : WebApplicationFactory<Program>
{
    private readonly string _dataProtectionKeys = Path.Combine(Path.GetTempPath(), $"kclock-dp-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The Google handler validates its options on every request, /d/v1 included, so an
        // empty ClientId turns the whole host into 500s. The default key path (/data) is a
        // deploy-time volume that does not exist on a test machine.
        builder.UseSetting("Authentication:Google:ClientId", "test-client-id");
        builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");
        builder.UseSetting("DataProtectionKeysPath", _dataProtectionKeys);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<IngestDbContext>>();
            services.AddDbContext<IngestDbContext>(options =>
                options.UseNpgsql(fixture.IngestConnectionString).UseSnakeCaseNamingConvention());

            services.RemoveAll<DbContextOptions<ClockDbContext>>();
            services.AddDbContext<ClockDbContext>(options =>
                options.UseNpgsql(fixture.AppConnectionString).UseSnakeCaseNamingConvention());
        });
    }

    /// <summary>
    /// A browser signed in as <paramref name="accountId"/>. The cookie is minted with the
    /// cookie handler's own ticket format — what the Google callback ends in — so everything
    /// after sign-in runs unmodified. Carries the CSRF header every non-GET /api call needs.
    /// </summary>
    public HttpClient CreateSessionClient(Guid accountId)
    {
        var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity([new Claim(AccountClaimTypes.AccountId, accountId.ToString())], "Google");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);

        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
        client.DefaultRequestHeaders.Add("X-Requested-With", "test");
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(_dataProtectionKeys, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
