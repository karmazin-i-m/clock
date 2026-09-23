using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KClock.Data.Configurations;

public class DeviceStateConfiguration : IEntityTypeConfiguration<DeviceState>
{
    public void Configure(EntityTypeBuilder<DeviceState> builder)
    {
        builder.ToTable(TableNames.DeviceState);
        builder.HasKey(s => s.DeviceId);
        // DeviceId mirrors device.id — it is never an identity column, only ever assigned by
        // the app from an existing Device row.
        builder.Property(s => s.DeviceId).ValueGeneratedNever();

        builder.HasOne<Device>()
            .WithOne()
            .HasForeignKey<DeviceState>(s => s.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
