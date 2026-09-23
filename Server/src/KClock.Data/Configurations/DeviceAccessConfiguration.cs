using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KClock.Data.Configurations;

public class DeviceAccessConfiguration : IEntityTypeConfiguration<DeviceAccess>
{
    public void Configure(EntityTypeBuilder<DeviceAccess> builder)
    {
        builder.ToTable(TableNames.DeviceAccess);
        builder.HasKey(a => new { a.BindingId, a.AccountId });

        builder.HasOne(a => a.Account)
            .WithMany()
            .HasForeignKey(a => a.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
