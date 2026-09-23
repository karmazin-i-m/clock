using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KClock.Data.Configurations;

public class DeviceCredentialConfiguration : IEntityTypeConfiguration<DeviceCredential>
{
    public void Configure(EntityTypeBuilder<DeviceCredential> builder)
    {
        builder.ToTable(TableNames.DeviceCredential);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).UseIdentityAlwaysColumn();
        builder.HasIndex(c => c.TokenHash).IsUnique();
        builder.Property(c => c.IssuedAt).HasDefaultValueSql("now()");

        builder.HasOne(c => c.Device)
            .WithMany()
            .HasForeignKey(c => c.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
