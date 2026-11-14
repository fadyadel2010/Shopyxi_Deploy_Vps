using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;

namespace PointOfSale.Core.Entities;

/// <summary>Retained workflow confirmation and links to the canonical per-item movements.</summary>
public sealed class StockTransferEvent : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid StockTransferId { get; set; }
    public long Version { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public TransferStatus Status { get; set; }
    public string DetailsJson { get; set; } = "[]";
}
