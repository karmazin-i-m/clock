using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KClock.Data;

/// <summary>
/// The migration-owning context. Used at runtime by the browser /api/* path, connecting as
/// clock_app — RLS on the ordinary tables plus these EF global query filters are belt and
/// braces (DESIGN.md §8): the filters make the generated SQL readable in logs, make
/// Include() across a device you don't own come back empty instead of truncated, and catch a
/// missing set_config call at the application layer with a useful stack trace, with the
/// database as the backstop.
///
/// clock_ingest never uses this context — see IngestDbContext.
/// </summary>
public class ClockDbContext(DbContextOptions<ClockDbContext> options, ICurrentAccountAccessor accountAccessor)
    : DbContext(options)
{
    private readonly ICurrentAccountAccessor _accountAccessor = accountAccessor;

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceBinding> DeviceBindings => Set<DeviceBinding>();
    public DbSet<DeviceAccess> DeviceAccesses => Set<DeviceAccess>();
    public DbSet<EnrollmentCode> EnrollmentCodes => Set<EnrollmentCode>();
    public DbSet<DeviceCredential> DeviceCredentials => Set<DeviceCredential>();
    public DbSet<Telemetry> Telemetry => Set<Telemetry>();
    public DbSet<DeviceState> DeviceStates => Set<DeviceState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ClockDbContext).Assembly);
        ApplyAccountScopeFilters(modelBuilder);
    }

    private void ApplyAccountScopeFilters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>().HasQueryFilter(a =>
            _accountAccessor.AccountId != null && a.Id == _accountAccessor.AccountId);

        modelBuilder.Entity<DeviceAccess>().HasQueryFilter(da =>
            _accountAccessor.AccountId != null && da.AccountId == _accountAccessor.AccountId);

        modelBuilder.Entity<EnrollmentCode>().HasQueryFilter(c =>
            _accountAccessor.AccountId != null && c.AccountId == _accountAccessor.AccountId);

        modelBuilder.Entity<DeviceBinding>().HasQueryFilter(b =>
            _accountAccessor.AccountId != null &&
            Set<DeviceAccess>().Any(da => da.BindingId == b.Id
                && da.AccountId == _accountAccessor.AccountId && da.RevokedAt == null));

        modelBuilder.Entity<DeviceCredential>().HasQueryFilter(dc =>
            _accountAccessor.AccountId != null &&
            Set<DeviceAccess>().Any(da => da.BindingId == dc.BindingId
                && da.AccountId == _accountAccessor.AccountId && da.RevokedAt == null));

        modelBuilder.Entity<Device>().HasQueryFilter(d =>
            _accountAccessor.AccountId != null &&
            Set<DeviceAccess>().Any(da => da.RevokedAt == null
                && da.AccountId == _accountAccessor.AccountId
                && da.Binding!.DeviceId == d.Id));
    }
}
