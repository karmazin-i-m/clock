using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KClock.Data.Migrations;

/// <summary>
/// Used only by `dotnet ef` tooling (migrations add/script/database update). The design-time
/// model build is pure reflection over ClockDbContext and never opens the connection this
/// dummy string names — that is what lets `dotnet ef migrations add` run in a sandbox with no
/// live Postgres and no dependency on KClock.Api's configuration or DI container.
/// </summary>
public class ClockDbContextFactory : IDesignTimeDbContextFactory<ClockDbContext>
{
    public ClockDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ClockDbContext>()
            .UseNpgsql("Host=localhost;Database=designtime")
            .UseSnakeCaseNamingConvention();

        return new ClockDbContext(optionsBuilder.Options, new DesignTimeAccountAccessor());
    }

    private sealed class DesignTimeAccountAccessor : ICurrentAccountAccessor
    {
        public Guid? AccountId => null;
    }
}
