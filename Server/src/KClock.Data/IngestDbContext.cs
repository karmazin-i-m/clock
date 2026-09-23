using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KClock.Data;

/// <summary>
/// Runtime-only context for the device /d/v1/* path, connecting as clock_ingest. Maps the
/// same physical tables ClockDbContext owns, but every entity here is marked
/// ExcludeFromMigrations — this context must never generate schema, and there are no EF
/// query filters: the ingest role sees across accounts by design (a batch resolves its own
/// binding by timestamp per DESIGN.md §9), gated by permissive RLS policies on the
/// database side instead.
/// </summary>
public class IngestDbContext(DbContextOptions<IngestDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceBinding> DeviceBindings => Set<DeviceBinding>();
    public DbSet<DeviceAccess> DeviceAccesses => Set<DeviceAccess>();
    public DbSet<EnrollmentCode> EnrollmentCodes => Set<EnrollmentCode>();
    public DbSet<DeviceCredential> DeviceCredentials => Set<DeviceCredential>();
    public DbSet<Telemetry> Telemetry => Set<Telemetry>();
    public DbSet<DeviceState> DeviceStates => Set<DeviceState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ClockDbContext).Assembly);

        modelBuilder.Entity<Device>().ToTable(t => t.ExcludeFromMigrations());
        modelBuilder.Entity<DeviceBinding>().ToTable(t => t.ExcludeFromMigrations());
        modelBuilder.Entity<DeviceAccess>().ToTable(t => t.ExcludeFromMigrations());
        modelBuilder.Entity<EnrollmentCode>().ToTable(t => t.ExcludeFromMigrations());
        modelBuilder.Entity<DeviceCredential>().ToTable(t => t.ExcludeFromMigrations());
        modelBuilder.Entity<Telemetry>().ToTable(t => t.ExcludeFromMigrations());
        modelBuilder.Entity<DeviceState>().ToTable(t => t.ExcludeFromMigrations());

        // Not reachable through the device path — exclude entirely so this context never
        // touches account/external_login even by accident.
        modelBuilder.Ignore<Account>();
        modelBuilder.Ignore<ExternalLogin>();
    }
}
