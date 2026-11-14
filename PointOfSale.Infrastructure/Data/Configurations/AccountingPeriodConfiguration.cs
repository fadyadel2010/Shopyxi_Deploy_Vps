using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;

namespace PointOfSale.Infrastructure.Data.Configurations;

public sealed class AccountingPeriodConfiguration : IEntityTypeConfiguration<AccountingPeriod>,
    IEntityTypeConfiguration<AccountingPeriodAuthority>, IEntityTypeConfiguration<AccountingPeriodEvent>
{
    public void Configure(EntityTypeBuilder<AccountingPeriod> b)
    {
        b.ToTable("AccountingPeriods", table =>
        {
            table.HasCheckConstraint("CK_AccountingPeriods_Boundaries", "\"StartDate\" <= \"EndDate\"");
            table.HasCheckConstraint("CK_AccountingPeriods_Status", "\"Status\" IN (0,1,2)");
            table.HasCheckConstraint("CK_AccountingPeriods_Retained", "NOT \"IsDeleted\"");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Code).HasMaxLength(50).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.StartDate, x.EndDate });
    }

    public void Configure(EntityTypeBuilder<AccountingPeriodAuthority> b)
    {
        b.ToTable("AccountingPeriodAuthorities");
        b.HasKey(x => x.TenantId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<AccountingPeriodEvent> b)
    {
        b.ToTable("AccountingPeriodEvents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.TotalDebits).HasPrecision(18, 4);
        b.Property(x => x.TotalCredits).HasPrecision(18, 4);
        b.HasOne<AccountingPeriod>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountingPeriodId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_PeriodEvents_Period");
        b.HasIndex(x => new { x.TenantId, x.AccountingPeriodId, x.Version }).IsUnique();
    }
}
