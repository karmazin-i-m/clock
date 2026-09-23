using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KClock.Data.Configurations;

public class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable(TableNames.Device);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).UseIdentityAlwaysColumn();
        builder.HasIndex(d => d.HardwareId).IsUnique();
        builder.Property(d => d.CreatedAt).HasDefaultValueSql("now()");

        builder.HasMany(d => d.Bindings)
            .WithOne(b => b.Device)
            .HasForeignKey(b => b.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
