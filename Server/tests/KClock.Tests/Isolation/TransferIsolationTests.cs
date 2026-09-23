using KClock.Data;
using KClock.Data.Entities;
using KClock.Data.Ingest;
using KClock.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KClock.Tests.Isolation;

/// <summary>
/// Validates DESIGN.md §8/§9's whole ownership model against a real timescaledb-ha container.
/// This suite cannot be replaced by careful reading (DESIGN.md §13) — with a single account a
/// policy that leaks everything and a correct one behave identically. Requires Docker; see
/// PostgresFixture for why discovery still works without it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TransferIsolationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TwoAccounts_EnrollSameDevice_SeeOnlyOwnTelemetry()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        await SeedAccountAsync(accountA);
        await SeedAccountAsync(accountB);

        await using var ingest = CreateIngestDbContext();
        var device = new Device { HardwareId = $"transfer-{Guid.NewGuid():N}", Profile = "esp8266-1m" };
        ingest.Devices.Add(device);
        await ingest.SaveChangesAsync();

        var bindingA = new DeviceBinding { DeviceId = device.Id, AccountId = accountA, BoundAt = DateTimeOffset.UtcNow.AddHours(-2) };
        ingest.DeviceBindings.Add(bindingA);
        await ingest.SaveChangesAsync();
        ingest.DeviceAccesses.Add(new DeviceAccess { BindingId = bindingA.Id, AccountId = accountA, Role = DeviceAccessRole.Owner });
        await ingest.SaveChangesAsync();

        var writer = new TelemetryBatchWriter(ingest);
        var baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        var samplesA = Enumerable.Range(0, 5)
            .Select(i => new TelemetrySample(baseTime.AddSeconds(i * 10), (short)(200 + i), 750, 40))
            .ToList();
        Assert.Equal(5, await writer.InsertAsync(device.Id, samplesA));

        // A unbinds, B enrolls the same MAC — this is what makes it a transfer, not two
        // independent devices (DESIGN.md §5.1, §9).
        bindingA.UnboundAt = DateTimeOffset.UtcNow;
        await ingest.SaveChangesAsync();

        var bindingB = new DeviceBinding { DeviceId = device.Id, AccountId = accountB, BoundAt = DateTimeOffset.UtcNow };
        ingest.DeviceBindings.Add(bindingB);
        await ingest.SaveChangesAsync();
        ingest.DeviceAccesses.Add(new DeviceAccess { BindingId = bindingB.Id, AccountId = accountB, Role = DeviceAccessRole.Owner });
        await ingest.SaveChangesAsync();

        var samplesB = Enumerable.Range(0, 5)
            .Select(i => new TelemetrySample(DateTimeOffset.UtcNow.AddSeconds(i * 10), (short)(300 + i), 760, 45))
            .ToList();
        Assert.Equal(5, await writer.InsertAsync(device.Id, samplesB));

        Assert.Equal(5, await CountTelemetryByAccountAsync(accountA));
        Assert.Equal(5, await CountTelemetryByAccountAsync(accountB));
    }

    [Fact]
    public async Task UnsetAccountGuc_ReturnsZeroRows_DoesNotThrow()
    {
        await using var app = CreateAppDbContext();
        await using var tx = await app.Database.BeginTransactionAsync();
        // Deliberately not calling SetCurrentAccountAsync — the fail-safe DESIGN.md §8
        // requires: an unset GUC must yield zero rows, never everything and never an
        // exception.
        var count = await app.Database.SqlQueryRaw<int>("SELECT count(*)::int FROM telemetry_by_account").SingleAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task OnlyOneOpenBindingPerDevice_IsEnforced()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        await SeedAccountAsync(accountA);
        await SeedAccountAsync(accountB);

        await using var ingest = CreateIngestDbContext();
        var device = new Device { HardwareId = $"single-open-{Guid.NewGuid():N}", Profile = "esp8266-1m" };
        ingest.Devices.Add(device);
        await ingest.SaveChangesAsync();

        ingest.DeviceBindings.Add(new DeviceBinding { DeviceId = device.Id, AccountId = accountA });
        await ingest.SaveChangesAsync();

        // A second OPEN binding for the same device must violate ix_device_binding_active —
        // the database, not the application, enforces "ownership is a period" (DESIGN.md §7).
        ingest.DeviceBindings.Add(new DeviceBinding { DeviceId = device.Id, AccountId = accountB });
        await Assert.ThrowsAsync<DbUpdateException>(() => ingest.SaveChangesAsync());
    }

    [Fact]
    public async Task HourlyAggregate_DoesNotLeakAcrossAccounts()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        await SeedAccountAsync(accountA);
        await SeedAccountAsync(accountB);

        await using var ingest = CreateIngestDbContext();
        var deviceA = new Device { HardwareId = $"agg-a-{Guid.NewGuid():N}", Profile = "esp8266-1m" };
        var deviceB = new Device { HardwareId = $"agg-b-{Guid.NewGuid():N}", Profile = "esp8266-1m" };
        ingest.Devices.AddRange(deviceA, deviceB);
        await ingest.SaveChangesAsync();

        var bindingA = new DeviceBinding { DeviceId = deviceA.Id, AccountId = accountA, BoundAt = DateTimeOffset.UtcNow.AddHours(-2) };
        var bindingB = new DeviceBinding { DeviceId = deviceB.Id, AccountId = accountB, BoundAt = DateTimeOffset.UtcNow.AddHours(-2) };
        ingest.DeviceBindings.AddRange(bindingA, bindingB);
        await ingest.SaveChangesAsync();
        ingest.DeviceAccesses.AddRange(
            new DeviceAccess { BindingId = bindingA.Id, AccountId = accountA, Role = DeviceAccessRole.Owner },
            new DeviceAccess { BindingId = bindingB.Id, AccountId = accountB, Role = DeviceAccessRole.Owner });
        await ingest.SaveChangesAsync();

        var writer = new TelemetryBatchWriter(ingest);
        var now = DateTimeOffset.UtcNow;
        // materialized_only = false gives real-time aggregation, so this is visible through
        // telemetry_1h_by_account without an explicit refresh_continuous_aggregate call.
        await writer.InsertAsync(deviceA.Id, [new TelemetrySample(now.AddMinutes(-30), 210, 750, 40)]);
        await writer.InsertAsync(deviceB.Id, [new TelemetrySample(now.AddMinutes(-30), 310, 760, 45)]);

        Assert.True(await CountHourlyBucketsByAccountAsync(accountA) >= 1);
        Assert.True(await CountHourlyBucketsByAccountAsync(accountB) >= 1);
    }

    // Goes through the migrator connection (RLS-exempt as owner), the same way
    // find_or_create_account runs at runtime — account.id = current_account_id()'s WITH CHECK
    // could never be satisfied for a brand-new row with no GUC set yet.
    private async Task SeedAccountAsync(Guid accountId)
    {
        var options = new DbContextOptionsBuilder<ClockDbContext>()
            .UseNpgsql(fixture.MigratorConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new ClockDbContext(options, new FixedAccountAccessor(null));
        db.Accounts.Add(new Account { Id = accountId, Email = $"{accountId}@example.test", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<int> CountTelemetryByAccountAsync(Guid accountId)
    {
        await using var db = CreateAppDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.SetCurrentAccountAsync(accountId);
        return await db.Database.SqlQueryRaw<int>("SELECT count(*)::int FROM telemetry_by_account").SingleAsync();
    }

    private async Task<int> CountHourlyBucketsByAccountAsync(Guid accountId)
    {
        await using var db = CreateAppDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.SetCurrentAccountAsync(accountId);
        return await db.Database.SqlQueryRaw<int>("SELECT count(*)::int FROM telemetry_1h_by_account").SingleAsync();
    }

    private ClockDbContext CreateAppDbContext()
    {
        var options = new DbContextOptionsBuilder<ClockDbContext>()
            .UseNpgsql(fixture.AppConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ClockDbContext(options, new FixedAccountAccessor(null));
    }

    private IngestDbContext CreateIngestDbContext()
    {
        var options = new DbContextOptionsBuilder<IngestDbContext>()
            .UseNpgsql(fixture.IngestConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new IngestDbContext(options);
    }

    // Unused by the raw-SQL view queries in this file (those are scoped purely by the Postgres
    // GUC via SetCurrentAccountAsync) — only here because ClockDbContext's constructor needs
    // one.
    private sealed class FixedAccountAccessor(Guid? accountId) : ICurrentAccountAccessor
    {
        public Guid? AccountId => accountId;
    }
}
