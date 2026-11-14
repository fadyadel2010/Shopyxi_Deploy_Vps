using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;

namespace PointOfSale.Infrastructure.Data.Configurations.Affiliate;

public sealed class AffiliateLandingPageDesignConfiguration : IEntityTypeConfiguration<AffiliateLandingPageDesign>
{
    public void Configure(EntityTypeBuilder<AffiliateLandingPageDesign> builder)
    {
        builder.ToTable("AffiliateLandingPageDesigns", table => table.HasCheckConstraint(
            "CK_AffiliateLandingPageDesigns_Template", "\"TemplateType\" IN ('Classic:v1', 'Storytelling:v1', 'MinimalFastCheckout:v1')"));
        builder.HasKey(d => new { d.AffiliateAccountId, d.TenantId, d.ProductId });
        builder.Property(d => d.TemplateType).HasMaxLength(64).IsRequired();
        builder.HasOne<AffiliateAccount>().WithMany().HasForeignKey(d => d.AffiliateAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany().HasForeignKey(d => d.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
