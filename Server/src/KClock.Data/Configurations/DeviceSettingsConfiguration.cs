using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KClock.Data.Configurations;

/// <summary>
/// The CHECKs are the firmware's own ranges (linkSetConfig on the Nano): both dimming bounds
/// 0–24, the marquee 0–60 minutes. A value the clock would refuse must not be storable.
/// </summary>
public class DeviceSettingsConfiguration : IEntityTypeConfiguration<DeviceSettings>
{
    public void Configure(EntityTypeBuilder<DeviceSettings> builder)
    {
        builder.ToTable(TableNames.DeviceSettings, t =>
        {
            t.HasCheckConstraint("ck_device_settings_dim_from", "dim_from BETWEEN 0 AND 24");
            t.HasCheckConstraint("ck_device_settings_dim_until", "dim_until BETWEEN 0 AND 24");
            t.HasCheckConstraint("ck_device_settings_marquee_minutes", "marquee_minutes BETWEEN 0 AND 60");
            t.HasCheckConstraint("ck_device_settings_version", "version > 0");
        });
        builder.HasKey(s => s.BindingId);
        builder.Property(s => s.BindingId).ValueGeneratedNever();
        builder.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasOne<DeviceBinding>()
            .WithOne()
            .HasForeignKey<DeviceSettings>(s => s.BindingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
