using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KClock.Data.Configurations;

public class EnrollmentCodeConfiguration : IEntityTypeConfiguration<EnrollmentCode>
{
    public void Configure(EntityTypeBuilder<EnrollmentCode> builder)
    {
        builder.ToTable(TableNames.EnrollmentCode);
        builder.HasKey(c => c.CodeHash);
        builder.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(c => c.Attempts).HasDefaultValue(0);

        builder.HasOne(c => c.Account)
            .WithMany()
            .HasForeignKey(c => c.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
