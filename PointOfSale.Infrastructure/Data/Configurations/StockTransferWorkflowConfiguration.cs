using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PointOfSale.Core.Entities;

namespace PointOfSale.Infrastructure.Data.Configurations;

public sealed class StockTransferWorkflowConfiguration : IEntityTypeConfiguration<StockTransfer>, IEntityTypeConfiguration<StockTransferEvent>, IEntityTypeConfiguration<StockTransferItem>
{
    public void Configure(EntityTypeBuilder<StockTransferItem> b)
    {
        b.Property(x => x.DispatchedUnitCost).HasPrecision(18, 4);
        b.ToTable("StockTransferItems", t => t.HasCheckConstraint("CK_StockTransferItem_DispatchedCost", "\"DispatchedUnitCost\" IS NULL OR \"DispatchedUnitCost\" >= 0"));
    }
    public void Configure(EntityTypeBuilder<StockTransfer> b)
    {
        b.Property(x => x.WorkflowVersion).HasDefaultValue(1L).IsConcurrencyToken();
        b.Property(x => x.ReversalReason).HasMaxLength(1000);
        b.Property(x => x.CancellationReason).HasMaxLength(1000);
    }
    public void Configure(EntityTypeBuilder<StockTransferEvent> b)
    {
        b.ToTable("StockTransferEvents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.DetailsJson).HasColumnType("jsonb").IsRequired();
        b.HasIndex(x => new { x.TenantId, x.StockTransferId, x.Version }).IsUnique();
        b.HasOne<StockTransfer>().WithMany().HasForeignKey(x => x.StockTransferId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
