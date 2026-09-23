using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KClock.Data.Configurations;

/// <summary>
/// No FK constraints to device/device_binding — matches DESIGN.md §7's raw CREATE TABLE
/// literally (hypertables and FKs into them are a known TimescaleDB rough edge, and none are
/// declared in the design). humidity_pct's CHECK is what makes the -1 sentinel unrepresentable
/// even by accident (DESIGN.md §7).
/// </summary>
public class TelemetryConfiguration : IEntityTypeConfiguration<Telemetry>
{
    public void Configure(EntityTypeBuilder<Telemetry> builder)
    {
        builder.ToTable(TableNames.Telemetry, t =>
            t.HasCheckConstraint("ck_telemetry_humidity_pct", "humidity_pct BETWEEN 0 AND 100"));
        builder.HasKey(t => new { t.DeviceId, t.Ts });
        builder.Property(t => t.ReceivedAt).HasDefaultValueSql("now()");
    }
}
