using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;

namespace PointOfSale.Infrastructure.Data.Configurations;

public sealed class FinancialVoucherConfiguration : IEntityTypeConfiguration<FinancialVoucher>, IEntityTypeConfiguration<FinancialVoucherEvent>
{
    public void Configure(EntityTypeBuilder<FinancialVoucher> b)
    {
        b.ToTable("FinancialVouchers", t =>
        {
            t.HasCheckConstraint("CK_FinancialVoucher_Amount", "\"Amount\" > 0");
            t.HasCheckConstraint("CK_FinancialVoucher_Kind", "\"Kind\" IN (0,1)");
            t.HasCheckConstraint("CK_FinancialVoucher_Status", "\"Status\" IN (0,1)");
            t.HasCheckConstraint("CK_FinancialVoucher_Retained", "NOT \"IsDeleted\"");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Number).HasMaxLength(60).IsRequired();
        b.Property(x => x.RequestFingerprint).HasMaxLength(64).IsRequired();
        b.Property(x => x.PartyName).HasMaxLength(500).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Amount).HasPrecision(18, 4);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ReversalReason).HasMaxLength(1000);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Kind, x.SourcePaymentId }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.ReversalJournalId).OnDelete(DeleteBehavior.Restrict);
    }
    public void Configure(EntityTypeBuilder<FinancialVoucherEvent> b)
    {
        b.ToTable("FinancialVoucherEvents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.FinancialVoucherId, x.Version }).IsUnique();
        b.HasOne<FinancialVoucher>().WithMany().HasForeignKey(x => new { x.TenantId, x.FinancialVoucherId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_FinancialVoucherEvent_Voucher");
    }
}
