using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Entities.Affiliate;

namespace PointOfSale.Infrastructure.Data.Configurations.Affiliate;

public sealed class AffiliateManualPayoutReturnConfiguration : IEntityTypeConfiguration<AffiliateManualPayoutReturn>,
    IEntityTypeConfiguration<AffiliateManualPayoutReturnAllocation>
{
    public void Configure(EntityTypeBuilder<AffiliateManualPayoutReturn> b)
    {
        b.ToTable("AffiliateManualPayoutReturns", t => t.HasCheckConstraint("CK_AffiliatePayoutReturn_Amount", "\"Amount\" > 0"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.RequestId).IsUnique();
        b.HasIndex(x => x.ManualPayoutId).IsUnique();
        b.HasIndex(x => x.BalanceLedgerId).IsUnique();
        b.HasIndex(x => x.OriginalJournalId).IsUnique();
        b.HasIndex(x => new { x.StoreTenantId, x.AffiliateAccountId });
        b.Property(x => x.Amount).HasPrecision(18, 4);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Attestation).HasMaxLength(200).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ReceiptReference).HasMaxLength(200).IsRequired();
        b.Property(x => x.CommandFingerprint).HasMaxLength(64).IsRequired();
        b.HasOne<AffiliateManualPayout>().WithMany().HasForeignKey(x => x.ManualPayoutId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AffiliatePayoutRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AffiliateAccount>().WithMany().HasForeignKey(x => x.AffiliateAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AffiliateBalanceLedger>().WithMany().HasForeignKey(x => x.BalanceLedgerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.OriginalJournalId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.AdministratorId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<AffiliateManualPayoutReturnAllocation> b)
    {
        b.ToTable("AffiliateManualPayoutReturnAllocations", t => t.HasCheckConstraint("CK_AffiliatePayoutReturnAllocation_Amount", "\"Amount\" > 0"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.PaidEntryId).IsUnique();
        b.Property(x => x.Amount).HasPrecision(18, 4);
        b.HasOne<AffiliateManualPayoutReturn>().WithMany().HasForeignKey(x => x.ReturnId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AffiliateCommissionLedger>().WithMany().HasForeignKey(x => x.PaidEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}
