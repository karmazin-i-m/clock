using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KClock.Data.Configurations;

/// <summary>
/// The partial unique index device_binding_active (device_id WHERE unbound_at IS NULL) is
/// deliberately NOT declared here — it is owned by hand-written SQL in the InitialSchema
/// migration, since EF's fluent HasIndex/HasFilter would render it, but keeping the one
/// enforcement of "at most one open binding per device" next to the rest of the isolation SQL
/// (rather than split across EF and raw SQL) keeps §15's review checklist easier to audit.
/// </summary>
public class DeviceBindingConfiguration : IEntityTypeConfiguration<DeviceBinding>
{
    public void Configure(EntityTypeBuilder<DeviceBinding> builder)
    {
        builder.ToTable(TableNames.DeviceBinding);
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).UseIdentityAlwaysColumn();
        builder.Property(b => b.BoundAt).HasDefaultValueSql("now()");

        builder.HasMany(b => b.Access)
            .WithOne(a => a.Binding)
            .HasForeignKey(a => a.BindingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(b => b.Credentials)
            .WithOne(c => c.Binding)
            .HasForeignKey(c => c.BindingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
