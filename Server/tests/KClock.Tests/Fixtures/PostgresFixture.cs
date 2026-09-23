using KClock.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace KClock.Tests.Fixtures;

/// <summary>
/// Empty constructor, all Docker access deferred to InitializeAsync — this is what lets
/// `dotnet test --list-tests` discover every test in this sandbox with no Docker present, and
/// what makes the "Docker not available" failure land cleanly inside a single test's setup
/// rather than crashing test discovery for the whole assembly.
///
/// Runs the real bootstrap SQL (roles, extensions) and the real EF migrations against a
/// throwaway timescaledb-ha container, exactly as a real deploy would, so this is also the
/// closest thing this repo has to re-verifying DESIGN.md §8's RLS×columnstore blocker — once
/// this runs somewhere with Docker.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string MigratorPassword = "migrator-test-pw";
    private const string AppPassword = "app-test-pw";
    private const string IngestPassword = "ingest-test-pw";

    private PostgreSqlContainer? _container;

    public string SuperuserConnectionString => _container!.GetConnectionString();
    public string AppConnectionString => BuildConnectionString("clock_app", AppPassword);
    public string IngestConnectionString => BuildConnectionString("clock_ingest", IngestPassword);
    public string MigratorConnectionString => BuildConnectionString("clock_migrator", MigratorPassword);

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
            .WithDatabase("kclock")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithEnvironment("CLOCK_MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("CLOCK_APP_PASSWORD", AppPassword)
            .WithEnvironment("CLOCK_INGEST_PASSWORD", IngestPassword)
            .Build();

        await _container.StartAsync();

        // Same script docker-entrypoint-initdb.d would run in a real deploy (Server/db/init/
        // 00_bootstrap.sql), executed the same way: through the container's own psql, so its
        // \getenv calls resolve against the container's environment, not the test host's.
        var bootstrapSql = await File.ReadAllTextAsync(FindBootstrapSqlPath());
        await _container.ExecScriptAsync(bootstrapSql);

        await MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private async Task MigrateAsync()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ClockDbContext>()
            .UseNpgsql(MigratorConnectionString)
            .UseSnakeCaseNamingConvention();

        await using var db = new ClockDbContext(optionsBuilder.Options, new NullAccountAccessor());
        await db.Database.MigrateAsync();
    }

    private string BuildConnectionString(string username, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(SuperuserConnectionString)
        {
            Username = username,
            Password = password,
        };
        return builder.ConnectionString;
    }

    private static string FindBootstrapSqlPath()
    {
        // Walk up from the test assembly's output directory to the Server/ root — avoids
        // hardcoding a path that only works from one particular working directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "db", "init", "00_bootstrap.sql")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new FileNotFoundException("Could not locate db/init/00_bootstrap.sql relative to the test output directory.")
            : Path.Combine(dir.FullName, "db", "init", "00_bootstrap.sql");
    }

    private sealed class NullAccountAccessor : ICurrentAccountAccessor
    {
        public Guid? AccountId => null;
    }
}
