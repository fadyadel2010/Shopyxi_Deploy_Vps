namespace PointOfSale.API.DTOs;

public class InventoryValuationDto
{
    public decimal? TotalInventoryValue { get; set; }
    public decimal WarehouseInventoryValue { get; set; }
    public decimal? InTransitValue { get; set; }
    public bool ValuationComplete { get; set; }
    public string ValuationBasis => "current-cost-on-hand-plus-dispatched-transit";
    public decimal InTransitQuantity { get; set; }
    public int InTransitTransferCount { get; set; }
    public int UnvaluedInTransitLines { get; set; }
    public int TotalProductsInStock { get; set; }
    public int LowStockItemsCount { get; set; }
    
    public List<WarehouseValuationDto> WarehouseValuations { get; set; } = new();
    public List<LowStockItemDto> LowStockItems { get; set; } = new();
}

public class WarehouseValuationDto
{
    public Guid WarehouseId { get; set; }
    public string WarehouseName { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public decimal TotalValue { get; set; }
}

public class LowStockItemDto
{
    public Guid WarehouseId { get; set; }
    public string WarehouseName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public decimal CurrentStock { get; set; }
    public decimal MinimumStock { get; set; }
    public decimal ReorderQuantity => MinimumStock - CurrentStock > 0 ? MinimumStock - CurrentStock : 0;
}
