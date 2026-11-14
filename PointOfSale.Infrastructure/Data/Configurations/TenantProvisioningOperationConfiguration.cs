using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PointOfSale.Core.Entities;

namespace PointOfSale.Infrastructure.Data.Configurations;

public sealed class TenantProvisioningOperationConfiguration : IEntityTypeConfiguration<TenantProvisioningOperation>
{
    public void Configure(EntityTypeBuilder<TenantProvisioningOperation> builder)
    {
        builder.ToTable("TenantProvisioningOperations", table =>
        {
            table.HasCheckConstraint("CK_TenantProvisioningOperations_AttemptCount", "\"AttemptCount\" >= 0");
            table.HasCheckConstraint(
                "CK_TenantProvisioningOperations_Hostname",
                "\"CanonicalHostname\" = LOWER(\"CanonicalHostname\") AND \"CanonicalHostname\" ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?\\.shopyxi\\.com$'");
        });

        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.IdempotencyKeyHash).HasMaxLength(64).IsRequired();
        builder.Property(operation => operation.RequestFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(operation => operation.CanonicalHostname).HasMaxLength(75).IsRequired();
        builder.Property(operation => operation.LastErrorCategory).HasMaxLength(64);
        builder.Property(operation => operation.LastErrorCode).HasMaxLength(128);

        // A lease is a fencing token, not merely a claim-time admission check.
        // Every tracked checkpoint must fail if another worker/admin has taken
        // ownership. Attempt/state also prevent the null-lease ABA race after
        // a competing worker has already completed and released its lease.
        // These are EF concurrency predicates on existing columns, not DDL.
        builder.Property(operation => operation.LeaseId).IsConcurrencyToken();
        builder.Property(operation => operation.AttemptCount).IsConcurrencyToken();
        builder.Property(operation => operation.State).IsConcurrencyToken();

        builder.HasIndex(operation => operation.IdempotencyKeyHash)
            .IsUnique()
            .HasDatabaseName("UQ_TenantProvisioningOperations_IdempotencyKeyHash");
        builder.HasIndex(operation => operation.TenantId)
            .IsUnique()
            .HasDatabaseName("UQ_TenantProvisioningOperations_TenantId");
        builder.HasIndex(operation => new { operation.State, operation.NextAttemptAtUtc })
            .HasDatabaseName("IX_TenantProvisioningOperations_Recovery");
        builder.HasIndex(operation => operation.LeaseExpiresAtUtc)
            .HasDatabaseName("IX_TenantProvisioningOperations_LeaseExpiry");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(operation => operation.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(operation => operation.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoreConfig>()
            .WithMany()
            .HasForeignKey(operation => operation.StoreConfigId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
