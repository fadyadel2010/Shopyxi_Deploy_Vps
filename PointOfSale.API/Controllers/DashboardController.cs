using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Inventory;
using PointOfSale.API.Security;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Accounting;

namespace PointOfSale.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private static readonly TimeSpan AggregateCacheTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RecentSalesCacheTtl = TimeSpan.FromSeconds(15);
    private const int MaxDashboardItems = 50;

    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        ApplicationDbContext context,
        ITenantContext tenantContext,
        IMemoryCache cache,
        ILogger<DashboardController> logger)
    {
        _context = context;
        _tenantContext = tenantContext;
        _cache = cache;
        _logger = logger;
    }

    private Guid TenantId => _tenantContext.RequiredTenantId;

    /// <summary>
    /// Get dashboard statistics and metrics.
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<DashboardStatsResponse>> GetDashboardStats(
        [FromQuery] Guid? warehouseId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = TenantId;
            var cacheKey = BuildCacheKey(
                tenantId,
                "stats",
                warehouseId?.ToString("N") ?? "all-warehouses");

            var response = await GetOrCreateAsync(
                cacheKey,
                AggregateCacheTtl,
                async ct =>
                {
                    var todayStart = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

                    var canViewFinance = PosPermissionAuthorizationHandler.HasPermission(User, PosPermissions.FinanceView);
                    var currencyCode = canViewFinance
                        ? await PointOfSale.Infrastructure.Accounting.FinancialReportingCurrency.RequireTenantAsync(_context, tenantId, ct) : null;
                    var salesSnapshot = canViewFinance
                        ? await PosSalesReportingReader.ReadAsync(_context, tenantId, todayStart, ct) : new PosSalesSnapshot();

                    var totalProducts = await _context.Products
                        .AsNoTracking()
                        .Where(product => product.TenantId == tenantId && !product.IsDeleted)
                        .CountAsync(ct);

                    var lowStockQuery = InventoryReportingReader.LowStockPositions(_context, tenantId);

                    if (warehouseId.HasValue)
                    {
                        lowStockQuery = lowStockQuery.Where(stock => stock.WarehouseId == warehouseId.Value);
                    }

                    var lowStockCount = await lowStockQuery.CountAsync(ct);

                    var totalCustomers = await _context.Customers
                        .AsNoTracking()
                        .Where(customer => customer.TenantId == tenantId && !customer.IsDeleted)
                        .CountAsync(ct);

                    var outstandingReceivables = canViewFinance ? await _context.AccountLedgers
                        .AsNoTracking()
                        .Where(entry =>
                            entry.TenantId == tenantId &&
                            entry.PartyType == PartyType.Customer)
                        .SumAsync(
                            entry => (decimal?)(entry.DebitAmount - entry.CreditAmount),
                            ct) ?? 0m : 0m;

                    var outstandingPayables = canViewFinance ? await _context.AccountLedgers
                        .AsNoTracking()
                        .Where(entry =>
                            entry.TenantId == tenantId &&
                            entry.PartyType == PartyType.Supplier)
                        .SumAsync(
                            entry => (decimal?)(entry.CreditAmount - entry.DebitAmount),
                            ct) ?? 0m : 0m;

                    var valuation = canViewFinance
                        ? await InventoryReportingReader.ReadValuationAsync(_context, tenantId, ct) : null;

                    return new DashboardStatsResponse
                    {
                        FinancialSummaryAvailable = canViewFinance,
                        CurrencyCode = currencyCode,
                        TodaySales = canViewFinance ? salesSnapshot.TodaySales : null,
                        SalesChangePercentage = canViewFinance ? salesSnapshot.SalesChangePercentage : null,
                        TotalProducts = totalProducts,
                        LowStockCount = lowStockCount,
                        TotalCustomers = totalCustomers,
                        WeekSales = canViewFinance ? salesSnapshot.WeekSales : null,
                        MonthSales = canViewFinance ? salesSnapshot.MonthSales : null,
                        YearSales = canViewFinance ? salesSnapshot.YearSales : null,
                        TotalRevenue = canViewFinance ? salesSnapshot.TotalRevenue : null,
                        OutstandingReceivables = canViewFinance ? outstandingReceivables : null,
                        OutstandingPayables = canViewFinance ? outstandingPayables : null,
                        TodayTransactionCount = canViewFinance ? salesSnapshot.TodayTransactionCount : null,
                        InventoryValue = valuation?.TotalOwnedValue,
                        WarehouseInventoryValue = valuation?.WarehouseValue,
                        InTransitInventoryValue = valuation?.InTransitValue,
                        InventoryValuationComplete = valuation?.IsComplete
                    };
                },
                cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching dashboard stats");
            return StatusCode(500, new { message = "An unexpected error occurred" });
        }
    }

    /// <summary>
    /// Get sales trend data for charts (last 7 days, 30 days, or 12 months).
    /// </summary>
    [HttpGet("sales-trend")]
    public async Task<ActionResult<SalesTrendResponse>> GetSalesTrend(
        [FromQuery] string period = "week",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = TenantId;
            var normalizedPeriod = NormalizeTrendPeriod(period);
            await PointOfSale.Infrastructure.Accounting.FinancialReportingCurrency.RequireTenantAsync(_context, tenantId, cancellationToken);
            var cacheKey = BuildCacheKey(tenantId, "sales-trend", normalizedPeriod);

            var response = await GetOrCreateAsync(
                cacheKey,
                AggregateCacheTtl,
                async ct =>
                {
                    var todayStart = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

                    if (normalizedPeriod == "year")
                    {
                        var currentMonthStart = new DateTime(todayStart.Year, todayStart.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                        var firstMonthStart = currentMonthStart.AddMonths(-11);
                        var nextMonthStart = currentMonthStart.AddMonths(1);

                        var monthlyRows = await BuildPostedRevenueQuery(tenantId)
                            .AsNoTracking()
                            .Where(entry => entry.JournalEntry.JournalDate >= firstMonthStart &&
                                            entry.JournalEntry.JournalDate < todayStart.AddDays(1))
                            .GroupBy(entry => new
                            {
                                entry.JournalEntry.JournalDate.Year,
                                entry.JournalEntry.JournalDate.Month
                            })
                            .Select(group => new MonthlySalesAmountRow
                            {
                                Year = group.Key.Year,
                                Month = group.Key.Month,
                                Amount = group.Sum(entry => entry.CreditAmount - entry.DebitAmount)
                            })
                            .ToListAsync(ct);

                        var monthlyLookup = monthlyRows.ToDictionary(
                            row => new YearMonthKey(row.Year, row.Month),
                            row => row.Amount);

                        var monthlyData = Enumerable.Range(0, 12)
                            .Select(offset =>
                            {
                                var month = firstMonthStart.AddMonths(offset);
                                return new SalesTrendDataPoint
                                {
                                    Label = month.ToString("MMM yyyy"),
                                    Date = month,
                                    Amount = monthlyLookup.GetValueOrDefault(new YearMonthKey(month.Year, month.Month))
                                };
                            })
                            .ToList();

                        return new SalesTrendResponse { Data = monthlyData };
                    }

                    var numberOfDays = normalizedPeriod == "month" ? 30 : 7;
                    var startDate = todayStart.AddDays(-(numberOfDays - 1));
                    var endDate = todayStart.AddDays(1);

                    var dailyRows = await BuildPostedRevenueQuery(tenantId)
                        .AsNoTracking()
                        .Where(entry => entry.JournalEntry.JournalDate >= startDate &&
                                        entry.JournalEntry.JournalDate < endDate)
                        .GroupBy(entry => entry.JournalEntry.JournalDate.Date)
                        .Select(group => new DailySalesAmountRow
                        {
                            Date = group.Key,
                            Amount = group.Sum(entry => entry.CreditAmount - entry.DebitAmount)
                        })
                        .ToListAsync(ct);

                    var dailyLookup = dailyRows.ToDictionary(
                        row => DateTime.SpecifyKind(row.Date.Date, DateTimeKind.Utc),
                        row => row.Amount);

                    var data = Enumerable.Range(0, numberOfDays)
                        .Select(offset =>
                        {
                            var date = startDate.AddDays(offset);
                            return new SalesTrendDataPoint
                            {
                                Label = normalizedPeriod == "month"
                                    ? date.ToString("MMM dd")
                                    : date.ToString("ddd, MMM dd"),
                                Date = date,
                                Amount = dailyLookup.GetValueOrDefault(date)
                            };
                        })
                        .ToList();

                    return new SalesTrendResponse { Data = data };
                },
                cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales trend");
            return StatusCode(500, new { message = "Error fetching sales trend" });
        }
    }

    /// <summary>
    /// Get top selling products.
    /// </summary>
    [HttpGet("top-products")]
    public async Task<ActionResult<List<TopProductResponse>>> GetTopProducts(
        [FromQuery] int limit = 5,
        [FromQuery] string period = "month",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = TenantId;
            var normalizedPeriod = NormalizeAggregatePeriod(period);
            await PointOfSale.Infrastructure.Accounting.FinancialReportingCurrency.RequireTenantAsync(_context, tenantId, cancellationToken);
            var cappedLimit = ClampLimit(limit);
            var cacheKey = BuildCacheKey(tenantId, "top-products", normalizedPeriod, cappedLimit.ToString());

            var response = await GetOrCreateAsync(
                cacheKey,
                AggregateCacheTtl,
                async ct =>
                {
                    var startDate = ResolveAggregatePeriodStart(normalizedPeriod);
                    var endDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(1), DateTimeKind.Utc);

                    return await _context.SalesInvoiceItems
                        .AsNoTracking()
                        .Where(item => !item.IsDeleted && item.SalesInvoice.TenantId == tenantId && !item.SalesInvoice.IsDeleted)
                        .Where(item => item.Product != null && item.Product.TenantId == tenantId && !item.Product.IsDeleted)
                        .Where(item => item.SalesInvoice.Status != InvoiceStatus.Cancelled)
                        .Where(item => item.SalesInvoice.InvoiceDate >= startDate && item.SalesInvoice.InvoiceDate < endDate)
                        .GroupBy(item => new { item.ProductId, ProductName = item.Product.Name, item.Product.SKU })
                        .Select(group => new TopProductResponse
                        {
                            ProductId = group.Key.ProductId,
                            ProductName = group.Key.ProductName,
                            SKU = group.Key.SKU,
                            QuantitySold = group.Sum(item => item.Quantity),
                            TotalRevenue = group.Sum(item => item.TotalAmount)
                        })
                        .OrderByDescending(product => product.TotalRevenue)
                        .ThenBy(product => product.ProductName)
                        .ThenBy(product => product.ProductId)
                        .Take(cappedLimit)
                        .ToListAsync(ct);
                },
                cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching top products");
            return StatusCode(500, new { message = "Error fetching top products" });
        }
    }

    /// <summary>
    /// Get recent sales transactions.
    /// </summary>
    [HttpGet("recent-sales")]
    public async Task<ActionResult<List<RecentSaleResponse>>> GetRecentSales(
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = TenantId;
            var cappedLimit = ClampLimit(limit);
            var cacheKey = BuildCacheKey(tenantId, "recent-sales", cappedLimit.ToString());

            var response = await GetOrCreateAsync(
                cacheKey,
                RecentSalesCacheTtl,
                async ct =>
                {
                    return await BuildRecentSalesQuery(_context.SalesInvoices, tenantId, cappedLimit)
                        .ToListAsync(ct);
                },
                cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching recent sales");
            return StatusCode(500, new { message = "Error fetching recent sales" });
        }
    }

    // Keep the endpoint's real query available for provider translation tests.
    // It does not connect to a database until the caller enumerates it.
    internal static IQueryable<RecentSaleResponse> BuildRecentSalesQuery(
        IQueryable<SalesInvoice> invoices, Guid tenantId, int limit) =>
        invoices.AsNoTracking()
            .Where(invoice => invoice.TenantId == tenantId && !invoice.IsDeleted)
            .Where(invoice => invoice.Status != InvoiceStatus.Cancelled)
            .OrderByDescending(invoice => invoice.InvoiceDate)
            .ThenByDescending(invoice => invoice.Id)
            .Take(ClampLimit(limit))
            .Select(invoice => new RecentSaleResponse
            {
                Id = invoice.Id,
                InvoiceNumber = invoice.InvoiceNumber,
                CustomerName = invoice.Customer != null && invoice.Customer.TenantId == tenantId && !invoice.Customer.IsDeleted
                    ? invoice.Customer.Name : null,
                IsWalkInCustomer = invoice.CustomerId == null,
                TotalAmount = invoice.TotalAmount,
                InvoiceDate = invoice.InvoiceDate,
                Status = invoice.Status.ToString(),
                PaidAmount = invoice.PaidAmount
            });

    /// <summary>
    /// Get low stock items.
    /// </summary>
    [HttpGet("low-stock")]
    public async Task<ActionResult<List<LowStockItemResponse>>> GetLowStockItems(
        [FromQuery] Guid? warehouseId = null,
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = TenantId;
            var cappedLimit = ClampLimit(limit);
            var cacheKey = BuildCacheKey(
                tenantId,
                "low-stock",
                warehouseId?.ToString("N") ?? "all-warehouses",
                cappedLimit.ToString());

            var response = await GetOrCreateAsync(
                cacheKey,
                AggregateCacheTtl,
                async ct =>
                {
                    var query = InventoryReportingReader.LowStockPositions(_context, tenantId);

                    if (warehouseId.HasValue)
                    {
                        query = query.Where(stock => stock.WarehouseId == warehouseId.Value);
                    }

                    return await query
                        .OrderBy(stock => stock.Quantity - stock.ReservedQuantity)
                        .ThenBy(stock => stock.Variant.Product.Name)
                        .ThenBy(stock => stock.Variant.ProductId)
                        .ThenBy(stock => stock.VariantId)
                        .Take(cappedLimit)
                        .Select(stock => new LowStockItemResponse
                        {
                            ProductId = stock.Variant.ProductId,
                            ProductName = stock.Variant.Product.Name,
                            SKU = string.IsNullOrWhiteSpace(stock.Variant.SKU) ? stock.Variant.Product.SKU : stock.Variant.SKU,
                            WarehouseName = stock.Warehouse.Name,
                            CurrentStock = stock.Quantity - stock.ReservedQuantity,
                            ReorderPoint = stock.Variant.Product.LowStockThreshold,
                            Status = stock.Quantity - stock.ReservedQuantity <= 0m ? "Out of Stock" : "Low Stock"
                        })
                        .ToListAsync(ct);
                },
                cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching low stock items");
            return StatusCode(500, new { message = "Error fetching low stock items" });
        }
    }

    /// <summary>
    /// Get category-wise sales breakdown.
    /// </summary>
    [HttpGet("category-sales")]
    public async Task<ActionResult<List<CategorySalesResponse>>> GetCategorySales(
        [FromQuery] string period = "month",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = TenantId;
            var normalizedPeriod = NormalizeAggregatePeriod(period);
            await PointOfSale.Infrastructure.Accounting.FinancialReportingCurrency.RequireTenantAsync(_context, tenantId, cancellationToken);
            var cacheKey = BuildCacheKey(tenantId, "category-sales", normalizedPeriod);

            var response = await GetOrCreateAsync(
                cacheKey,
                AggregateCacheTtl,
                async ct =>
                {
                    var startDate = ResolveAggregatePeriodStart(normalizedPeriod);
                    var endDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(1), DateTimeKind.Utc);

                    return await _context.SalesInvoiceItems
                        .AsNoTracking()
                        .Where(item => !item.IsDeleted && item.SalesInvoice.TenantId == tenantId && !item.SalesInvoice.IsDeleted)
                        .Where(item => item.Product != null && item.Product.TenantId == tenantId && !item.Product.IsDeleted)
                        .Where(item => item.SalesInvoice.Status != InvoiceStatus.Cancelled)
                        .Where(item => item.SalesInvoice.InvoiceDate >= startDate && item.SalesInvoice.InvoiceDate < endDate)
                        .GroupBy(item => new
                        {
                            CategoryId = item.Product.Category != null && item.Product.Category.TenantId == tenantId && !item.Product.Category.IsDeleted
                                ? item.Product.CategoryId : Guid.Empty,
                            CategoryName = item.Product.Category != null && item.Product.Category.TenantId == tenantId && !item.Product.Category.IsDeleted
                                ? item.Product.Category.Name : string.Empty,
                            IsUncategorized = item.Product.Category == null || item.Product.Category.TenantId != tenantId || item.Product.Category.IsDeleted
                        })
                        .Select(group => new CategorySalesResponse
                        {
                            CategoryId = group.Key.CategoryId,
                            CategoryName = group.Key.CategoryName,
                            IsUncategorized = group.Key.IsUncategorized,
                            TotalRevenue = group.Sum(item => item.TotalAmount),
                            ItemsSold = group.Sum(item => item.Quantity)
                        })
                        .OrderByDescending(category => category.TotalRevenue)
                        .ThenBy(category => category.CategoryName)
                        .ToListAsync(ct);
                },
                cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching category sales");
            return StatusCode(500, new { message = "Error fetching category sales" });
        }
    }

    /// <summary>
    /// Get payment method breakdown.
    /// </summary>
    [HttpGet("payment-methods")]
    public async Task<ActionResult<List<PaymentMethodBreakdownResponse>>> GetPaymentMethodBreakdown(
        [FromQuery] string period = "month",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = TenantId;
            var normalizedPeriod = NormalizeAggregatePeriod(period);
            await PointOfSale.Infrastructure.Accounting.FinancialReportingCurrency.RequireTenantAsync(_context, tenantId, cancellationToken);
            var cacheKey = BuildCacheKey(tenantId, "payment-methods", normalizedPeriod);

            var response = await GetOrCreateAsync(
                cacheKey,
                AggregateCacheTtl,
                async ct =>
                {
                    var startDate = ResolveAggregatePeriodStart(normalizedPeriod);
                    var endDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(1), DateTimeKind.Utc);

                    return await _context.SalesInvoicePayments
                        .AsNoTracking()
                        .Where(payment => !payment.IsDeleted && payment.SalesInvoice.TenantId == tenantId && !payment.SalesInvoice.IsDeleted)
                        .Where(payment => payment.SalesInvoice.Status != InvoiceStatus.Cancelled)
                        .Where(payment => payment.PaymentDate >= startDate && payment.PaymentDate < endDate)
                        .GroupBy(payment => payment.PaymentMethod)
                        .Select(group => new PaymentMethodBreakdownResponse
                        {
                            PaymentMethod = group.Key.ToString(),
                            TotalAmount = group.Sum(payment => payment.Amount),
                            TransactionCount = group.Count()
                        })
                        .OrderByDescending(payment => payment.TotalAmount)
                        .ThenBy(payment => payment.PaymentMethod)
                        .ToListAsync(ct);
                },
                cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching payment method breakdown");
            return StatusCode(500, new { message = "Error fetching payment method breakdown" });
        }
    }

    private Task<T> GetOrCreateAsync<T>(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        // Financial and stock dashboard values are recomputed from SQL authority on every request.
        return factory(cancellationToken);
    }

    private IQueryable<LedgerEntry> BuildPostedRevenueQuery(Guid tenantId) =>
        PosSalesReportingReader.RevenueEntries(_context, tenantId);

    private static string BuildCacheKey(Guid tenantId, params string[] parts)
    {
        return $"dashboard:{tenantId:N}:{string.Join(":", parts)}";
    }

    private static int ClampLimit(int limit)
    {
        return Math.Clamp(limit, 1, MaxDashboardItems);
    }

    private static string NormalizeTrendPeriod(string? period)
    {
        return period?.Trim().ToLowerInvariant() switch
        {
            "month" => "month",
            "year" => "year",
            _ => "week"
        };
    }

    private static string NormalizeAggregatePeriod(string? period)
    {
        return period?.Trim().ToLowerInvariant() switch
        {
            "week" => "week",
            "year" => "year",
            "all" => "all",
            _ => "month"
        };
    }

    private static DateTime ResolveAggregatePeriodStart(string period)
    {
        var todayStart = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

        return period switch
        {
            "week" => todayStart.AddDays(-6),
            "month" => todayStart.AddMonths(-1),
            "year" => todayStart.AddYears(-1),
            _ => DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)
        };
    }

    private sealed class DailySalesAmountRow
    {
        public DateTime Date { get; init; }
        public decimal Amount { get; init; }
    }

    private sealed class MonthlySalesAmountRow
    {
        public int Year { get; init; }
        public int Month { get; init; }
        public decimal Amount { get; init; }
    }

    private readonly record struct YearMonthKey(int Year, int Month);
}

public class DashboardStatsResponse
{
    public bool FinancialSummaryAvailable { get; set; }
    public string? CurrencyCode { get; set; }
    public string FinancialBasis => "journal-date-utc-period-movement";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public decimal? TodaySales { get; set; }
    public decimal? SalesChangePercentage { get; set; }
    public int TotalProducts { get; set; }
    public int LowStockCount { get; set; }
    public int TotalCustomers { get; set; }
    public decimal? WeekSales { get; set; }
    public decimal? MonthSales { get; set; }
    public decimal? YearSales { get; set; }
    public decimal? TotalRevenue { get; set; }
    public decimal? OutstandingReceivables { get; set; }
    public decimal? OutstandingPayables { get; set; }
    public int? TodayTransactionCount { get; set; }
    public decimal? InventoryValue { get; set; }
    public decimal? WarehouseInventoryValue { get; set; }
    public decimal? InTransitInventoryValue { get; set; }
    public bool? InventoryValuationComplete { get; set; }
}

public class SalesTrendResponse
{
    public List<SalesTrendDataPoint> Data { get; set; } = new();
}

public class SalesTrendDataPoint
{
    public string Label { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
}

public class TopProductResponse
{
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? SKU { get; set; }
    public decimal QuantitySold { get; set; }
    public decimal TotalRevenue { get; set; }
}

public class RecentSaleResponse
{
    public Guid Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>
    /// Associated customer display name. Null when no customer name is available; the
    /// client decides how to localize or represent the absence.
    /// </summary>
    public string? CustomerName { get; set; }

    /// <summary>
    /// True only when the invoice has no customer association (nullable customer foreign key).
    /// The client localizes the walk-in label; no display language is chosen server-side.
    /// </summary>
    public bool IsWalkInCustomer { get; set; }

    public decimal TotalAmount { get; set; }
    public DateTime InvoiceDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal PaidAmount { get; set; }
}

public class LowStockItemResponse
{
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? SKU { get; set; }
    public string WarehouseName { get; set; } = string.Empty;
    public decimal CurrentStock { get; set; }
    public decimal ReorderPoint { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class CategorySalesResponse
{
    public Guid CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public bool IsUncategorized { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal ItemsSold { get; set; }
}

public class PaymentMethodBreakdownResponse
{
    public string PaymentMethod { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public int TransactionCount { get; set; }
}
