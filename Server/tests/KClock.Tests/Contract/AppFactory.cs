using KClock.Data;
using KClock.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KClock.Tests.Contract;

/// <summary>
/// Points the app's IngestDbContext at the shared PostgresFixture container instead of
/// whatever appsettings/environment configuration a real deploy would use — everything else
/// about Program.cs runs unmodified, which is the point of a characterization test
/// (DESIGN.md §13).
/// </summary>
public sealed class AppFactory(PostgresFixture fixture) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<IngestDbContext>>();
            services.AddDbContext<IngestDbContext>(options =>
                options.UseNpgsql(fixture.IngestConnectionString).UseSnakeCaseNamingConvention());
        });
    }
}
