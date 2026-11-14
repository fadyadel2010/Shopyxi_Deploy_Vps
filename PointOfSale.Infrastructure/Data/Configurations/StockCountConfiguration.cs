using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PointOfSale.Core.Entities;

namespace PointOfSale.Infrastructure.Data.Configurations;

public sealed class StockCountConfiguration : IEntityTypeConfiguration<StockCountSession>, IEntityTypeConfiguration<StockCountLine>, IEntityTypeConfiguration<StockCountEvent>
{
    public void Configure(EntityTypeBuilder<StockCountSession> b)
    {
        b.ToTable("StockCountSessions", t =>
        {
            t.HasCheckConstraint("CK_StockCountSession_Status", "\"Status\" IN (0,1,2,3)");
            t.HasCheckConstraint("CK_StockCountSession_Retained", "NOT \"IsDeleted\"");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.WarehouseId, x.Status });
        b.Property(x => x.Number).HasMaxLength(60).IsRequired();
        b.Property(x => x.RequestFingerprint).HasMaxLength(64).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.PostingReason).HasMaxLength(1000);
        b.Property(x => x.ReversalReason).HasMaxLength(1000);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
    public void Configure(EntityTypeBuilder<StockCountLine> b)
    {
        b.ToTable("StockCountLines", t =>
        {
            t.HasCheckConstraint("CK_StockCountLine_Count", "\"CountedQuantity\" IS NULL OR \"CountedQuantity\" >= 0");
            t.HasCheckConstraint("CK_StockCountLine_Cost", "\"UnitCost\" >= 0");
            t.HasCheckConstraint("CK_StockCountLine_Retained", "NOT \"IsDeleted\"");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.TenantId, x.StockCountSessionId, x.VariantId }).IsUnique();
        b.Property(x => x.ProductName).HasMaxLength(500).IsRequired();
        b.Property(x => x.Sku).HasMaxLength(200).IsRequired();
        b.Property(x => x.VarianceReason).HasMaxLength(1000);
        foreach (var name in new[] { nameof(StockCountLine.SystemQuantity), nameof(StockCountLine.ReservedSnapshot), nameof(StockCountLine.DamagedSnapshot),
            nameof(StockCountLine.UnitCost), nameof(StockCountLine.CountedQuantity), nameof(StockCountLine.PostedValue) }) b.Property(name).HasPrecision(18, 4);
        b.HasOne(x => x.Session).WithMany(x => x.Lines).HasForeignKey(x => new { x.TenantId, x.StockCountSessionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockCountLine_Session");
    }
    public void Configure(EntityTypeBuilder<StockCountEvent> b)
    {
        b.ToTable("StockCountEvents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.StockCountSessionId, x.Version }).IsUnique();
        b.HasOne<StockCountSession>().WithMany().HasForeignKey(x => new { x.TenantId, x.StockCountSessionId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockCountEvent_Session");
    }
}
