using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;

namespace PointOfSale.Infrastructure.Data.Configurations;

public sealed class CommerceHubSellerApplicationConfiguration : IEntityTypeConfiguration<CommerceHubSellerApplication>
{
    public void Configure(EntityTypeBuilder<CommerceHubSellerApplication> builder)
    {
        builder.ToTable("CommerceHubSellerApplications", table =>
        {
            table.HasCheckConstraint("CK_CommerceHubSellerApplications_Revision", "\"Revision\" > 0");
            table.HasCheckConstraint(
                "CK_CommerceHubSellerApplications_ReviewState",
                "(\"Status\" = 1 AND \"ReviewedAtUtc\" IS NULL AND \"ReviewedBySuperAdminId\" IS NULL) OR " +
                "(\"Status\" IN (2, 3) AND \"ReviewedAtUtc\" IS NOT NULL AND \"ReviewedBySuperAdminId\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CommerceHubSellerApplications_RejectionReason",
                "\"Status\" <> 3 OR (\"RejectionReason\" IS NOT NULL AND length(trim(\"RejectionReason\")) >= 10)");
        });

        builder.HasKey(application => application.Id);
        builder.HasIndex(application => application.ReferenceNumber).IsUnique();
        builder.HasIndex(application => new { application.TenantId, application.Revision }).IsUnique();
        builder.HasIndex(application => application.TenantId)
            .HasFilter($"\"Status\" = {(int)CommerceHubSellerApplicationStatus.Submitted}")
            .IsUnique()
            .HasDatabaseName("UX_CommerceHubSellerApplications_OnePendingPerTenant");
        builder.HasIndex(application => new { application.Status, application.SubmittedAtUtc });

        builder.Property(application => application.ReferenceNumber).HasMaxLength(40).IsRequired();
        builder.Property(application => application.StoreDisplayName).HasMaxLength(160).IsRequired();
        builder.Property(application => application.BusinessCategory).HasMaxLength(120).IsRequired();
        builder.Property(application => application.BusinessDescription).HasMaxLength(1000).IsRequired();
        builder.Property(application => application.ContactName).HasMaxLength(160).IsRequired();
        builder.Property(application => application.ContactPhone).HasMaxLength(40).IsRequired();
        builder.Property(application => application.ContactEmail).HasMaxLength(320).IsRequired();
        builder.Property(application => application.ProductCategoriesJson).HasColumnType("jsonb").IsRequired();
        builder.Property(application => application.StorefrontUrl).HasMaxLength(500);
        builder.Property(application => application.ReviewerIdentity).HasMaxLength(320);
        builder.Property(application => application.RejectionReason).HasMaxLength(1000);
        builder.Property(application => application.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(application => application.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
