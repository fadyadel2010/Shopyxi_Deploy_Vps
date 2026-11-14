using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;

namespace PointOfSale.Infrastructure.Data.Configurations.Affiliate;

public sealed class AffiliateManualPayoutConfiguration : IEntityTypeConfiguration<AffiliateManualPayout>
{
    public void Configure(EntityTypeBuilder<AffiliateManualPayout> b)
    {
        b.ToTable("AffiliateManualPayouts", t => t.HasCheckConstraint("CK_AffiliateManualPayout_Amount", "\"Amount\" > 0"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.RequestId).IsUnique();
        b.HasIndex(x => new { x.StoreTenantId, x.AffiliateAccountId });
        b.Property(x => x.Amount).HasPrecision(18, 4);
        foreach (var name in new[] { nameof(AffiliateManualPayout.Currency), nameof(AffiliateManualPayout.Method), nameof(AffiliateManualPayout.DestinationType), nameof(AffiliateManualPayout.AdministratorRole) })
            b.Property<string>(name).HasMaxLength(32);
        foreach (var name in new[] { nameof(AffiliateManualPayout.DestinationFingerprint), nameof(AffiliateManualPayout.CommandFingerprint) })
            b.Property<string>(name).HasMaxLength(64);
        b.Property(x => x.DestinationNumber).HasMaxLength(64);
        b.Property(x => x.InternalReference).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.AllocationsJson).HasColumnType("jsonb");
        b.HasOne<AffiliatePayoutRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AffiliateAccount>().WithMany().HasForeignKey(x => x.AffiliateAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AffiliateUser>().WithMany().HasForeignKey(x => x.AffiliateUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.AdministratorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Cashbox>().WithMany().HasForeignKey(x => x.CashboxId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TreasuryAccount>().WithMany().HasForeignKey(x => x.TreasuryAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.SourceAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AffiliateBalanceLedger>().WithMany().HasForeignKey(x => x.BalanceLedgerId).OnDelete(DeleteBehavior.Restrict);
    }
}
