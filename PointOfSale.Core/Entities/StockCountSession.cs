using System.ComponentModel.DataAnnotations.Schema;
using PointOfSale.Core.Interfaces;

namespace PointOfSale.Core.Entities;

public enum StockCountStatus { Draft, Posted, Cancelled, Reversed }

/// <summary>Counts the sellable bucket, including reserved units, excluding damaged stock.</summary>
public sealed class StockCountSession : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Number { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid WarehouseId { get; set; }
    public DateOnly CountDate { get; set; }
    public DateTime SnapshotAtUtc { get; set; }
    public StockCountStatus Status { get; set; }
    public long Version { get; set; } = 1;
    public string Reason { get; set; } = string.Empty;
    public Guid? CountedBy { get; set; }
    public DateTime? CountedAtUtc { get; set; }
    public Guid? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public string? PostingReason { get; set; }
    public Guid? VarianceAccountId { get; set; }
    public Guid? JournalId { get; set; }
    public Guid? ReversalJournalId { get; set; }
    public Guid? ReversedBy { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public string? ReversalReason { get; set; }
    public ICollection<StockCountLine> Lines { get; set; } = new List<StockCountLine>();
}

public sealed class StockCountLine : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid StockCountSessionId { get; set; }
    public Guid ProductId { get; set; }
    public Guid VariantId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public decimal SystemQuantity { get; set; }
    public decimal ReservedSnapshot { get; set; }
    public decimal DamagedSnapshot { get; set; }
    public long StockVersion { get; set; }
    public decimal UnitCost { get; set; }
    public decimal? CountedQuantity { get; set; }
    [NotMapped] public decimal? Variance => CountedQuantity - SystemQuantity;
    public string? VarianceReason { get; set; }
    public Guid? CountedBy { get; set; }
    public DateTime? CountedAtUtc { get; set; }
    public decimal PostedValue { get; set; }
    public Guid? MovementId { get; set; }
    public Guid? ReversalMovementId { get; set; }
    public StockCountSession Session { get; set; } = null!;
}

public sealed class StockCountEvent : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid StockCountSessionId { get; set; }
    public long Version { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public StockCountStatus Status { get; set; }
}
