using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.SuperAdmin.DTOs;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.SuperAdmin.Services;

public interface ISuperAdminOrdersService
{
    Task<SuperAdminPagedResult<SuperAdminOrderListItemDto>> GetOrdersAsync(
        SuperAdminOrdersListQuery query,
        CancellationToken cancellationToken = default);

    Task<SuperAdminOrdersStatsDto> GetOrderStatsAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);

    Task<SuperAdminOrderDetailsDto?> GetOrderDetailsAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);

    Task<SuperAdminOrdersCsvExport> ExportOrdersCsvAsync(
        SuperAdminOrdersListQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class SuperAdminOrdersService : ISuperAdminOrdersService
{
    private static readonly PaymentStatus[] CapturedPaymentStatuses =
    [
        PaymentStatus.Collected,
        PaymentStatus.Settled,
        PaymentStatus.Refunded,
        PaymentStatus.PartiallyRefunded
    ];
    private static readonly PaymentStatus[] FailedPaymentStatuses =
    [
        PaymentStatus.Failed,
        PaymentStatus.Rejected,
        PaymentStatus.Expired,
        PaymentStatus.AmountMismatch
    ];
    internal const int MaxExportRows = 10_000;

    private readonly ApplicationDbContext _dbContext;
    private readonly IRefundEvidenceService _refundEvidenceService;
    private readonly ILogger<SuperAdminOrdersService> _logger;

    public SuperAdminOrdersService(
        ApplicationDbContext dbContext,
        IRefundEvidenceService refundEvidenceService,
        ILogger<SuperAdminOrdersService> logger)
    {
        _dbContext = dbContext;
        _refundEvidenceService = refundEvidenceService;
        _logger = logger;
    }

    public async Task<SuperAdminPagedResult<SuperAdminOrderListItemDto>> GetOrdersAsync(
        SuperAdminOrdersListQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var snapshot = await BeginReadSnapshotAsync(cancellationToken);
        var filteredQuery = BuildFilteredQuery(query);
        var totalCount = await filteredQuery.CountAsync(cancellationToken);

        var pageRows = await ApplySorting(ProjectOrderListQuery(filteredQuery), query)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var orderIds = pageRows.Select(row => row.OrderId).ToArray();
        var subOrderLookup = await LoadSubOrderSummariesAsync(orderIds, cancellationToken);

        var result = new SuperAdminPagedResult<SuperAdminOrderListItemDto>
        {
            Items = pageRows
                .Select(row => new SuperAdminOrderListItemDto
                {
                    OrderId = row.OrderId,
                    OrderNumber = row.OrderNumber,
                    CustomerName = row.CustomerName,
                    TotalAmount = row.TotalAmount,
                    Currency = row.Currency,
                    Status = row.Status,
                    PaymentStatus = row.PaymentStatus,
                    CreatedAt = row.CreatedAt,
                    VendorCount = row.VendorCount,
                    TenantName = BuildTenantSummary(row.PrimaryTenantName, row.VendorCount),
                    SubOrders = subOrderLookup.TryGetValue(row.OrderId, out var subOrders)
                        ? subOrders
                        : Array.Empty<SuperAdminOrderSubOrderSummaryDto>()
                })
                .ToList(),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount,
            TotalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)query.PageSize),
            AsOfUtc = DateTime.UtcNow
        };

        return result;
    }

    public async Task<SuperAdminOrdersStatsDto> GetOrderStatsAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        await using var snapshot = await BeginReadSnapshotAsync(cancellationToken);
        var todayStartUtc = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var todayEndUtc = todayStartUtc.AddDays(1);
        var baseQuery = BuildBaseOrdersQuery(tenantId);

        var todaySummary = await baseQuery
            .Where(order => order.CreatedAt >= todayStartUtc && order.CreatedAt < todayEndUtc)
            .GroupBy(_ => 1)
            .Select(group => new OrdersTodayProjection
            {
                TotalOrdersToday = group.LongCount()
            })
            .SingleOrDefaultAsync(cancellationToken);

        var pendingOrders = await baseQuery.LongCountAsync(
            order => order.Status == OrderStatus.Pending, cancellationToken);
        var failedPayments = await baseQuery.LongCountAsync(
            order => FailedPaymentStatuses.Contains(order.PaymentStatus), cancellationToken);
        var capturedSalesToday = await GetCapturedSalesTodayAsync(
            tenantId,
            todayStartUtc,
            todayEndUtc,
            cancellationToken);

        var result = new SuperAdminOrdersStatsDto
        {
            WindowStartUtc = todayStartUtc,
            WindowEndUtc = todayEndUtc,
            AsOfUtc = DateTime.UtcNow,
            Currency = "EGP",
            TotalOrdersToday = todaySummary?.TotalOrdersToday ?? 0,
            PendingOrders = pendingOrders,
            FailedPayments = failedPayments,
            CapturedSalesToday = capturedSalesToday
        };

        return result;
    }

    public async Task<SuperAdminOrderDetailsDto?> GetOrderDetailsAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        await using var snapshot = await BeginReadSnapshotAsync(cancellationToken);
        var order = await _dbContext.CommerceHubMarketplaceOrders
            .AsNoTracking()
            .Where(item => item.Id == orderId && !item.IsDeleted)
            .Select(item => new OrderDetailsProjection
            {
                OrderId = item.Id,
                OrderNumber = item.OrderNumber,
                CustomerName = item.FullName,
                CustomerPhone = item.PhoneNumber,
                AddressLine = item.AddressLine,
                City = item.City,
                Area = item.Area,
                Notes = item.Notes,
                Currency = item.Currency,
                Subtotal = item.Subtotal,
                ShippingTotal = item.ShippingTotal,
                TotalAmount = item.TotalAmount,
                Status = item.Status,
                PaymentStatus = item.PaymentStatus,
                PaymentMethodCode = item.PaymentMethodCode,
                PaymentMethodName = item.PaymentMethodName,
                PaymentReference = item.PaymentReference,
                PaymentReferenceId = item.PaymentReferenceId,
                PaymentGateway = item.PaymentGateway,
                CreatedAt = item.CreatedAt,
                PaidAt = item.PaidAt,
                VendorCount = item.SubOrders.Count(subOrder => !subOrder.IsDeleted),
                PrimaryTenantName = item.SubOrders
                    .Where(subOrder => !subOrder.IsDeleted)
                    .OrderBy(subOrder => subOrder.StoreName)
                    .Select(subOrder => subOrder.StoreName)
                    .FirstOrDefault() ?? string.Empty
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (order == null)
        {
            return null;
        }

        var payments = await _dbContext.CommerceHubPayments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId && !payment.IsDeleted)
            .OrderByDescending(payment => payment.CreatedAt)
            .Select(payment => new SuperAdminOrderPaymentDetailsDto
            {
                PaymentId = payment.Id,
                Amount = payment.Amount,
                Currency = payment.Currency,
                Status = payment.Status,
                Provider = payment.Provider,
                ProviderReference = payment.ProviderReference,
                ProviderIntentId = payment.ProviderIntentId,
                ProviderTransactionId = payment.ProviderTransactionId,
                FailureReason = payment.FailureReason,
                IsProcessed = payment.IsProcessed,
                CreatedAt = payment.CreatedAt,
                ProcessedAtUtc = payment.ProcessedAtUtc,
                PaidAtUtc = payment.PaidAtUtc,
                Attempts = payment.Attempts
                    .Where(attempt => !attempt.IsDeleted)
                    .OrderByDescending(attempt => attempt.AttemptNumber)
                    .Select(attempt => new SuperAdminOrderPaymentAttemptDto
                    {
                        AttemptId = attempt.Id,
                        AttemptNumber = attempt.AttemptNumber,
                        Provider = attempt.Provider,
                        Status = attempt.Status,
                        ProviderReference = attempt.ProviderReference,
                        ProviderIntentId = attempt.ProviderIntentId,
                        ProviderTransactionId = attempt.ProviderTransactionId,
                        FailureReason = attempt.FailureReason,
                        CreatedAt = attempt.CreatedAt,
                        CompletedAtUtc = attempt.CompletedAtUtc
                    })
                    .ToArray()
            })
            .ToListAsync(cancellationToken);

        var subOrders = await _dbContext.CommerceHubSubOrders
            .AsNoTracking()
            .Where(subOrder => subOrder.MarketplaceOrderId == orderId && !subOrder.IsDeleted)
            .OrderBy(subOrder => subOrder.StoreName)
            .Select(subOrder => new SuperAdminOrderSubOrderDetailsDto
            {
                SubOrderId = subOrder.Id,
                OrderNumber = subOrder.OrderNumber,
                VendorId = subOrder.StoreId,
                VendorName = subOrder.StoreName,
                Amount = subOrder.Total,
                Currency = subOrder.Currency,
                Status = subOrder.Status,
                PaymentStatus = subOrder.PaymentStatus,
                PlatformCommissionAmount = subOrder.PlatformCommissionAmount,
                PlatformCommissionRate = subOrder.PlatformCommissionRate,
                VendorEarningAmount = subOrder.VendorEarningAmount,
                ShippingMethodName = subOrder.ShippingMethodName,
                PaymentMethodName = subOrder.PaymentMethodName,
                PaymentReferenceId = subOrder.PaymentReferenceId,
                PaidAt = subOrder.PaidAt,
                ItemCount = subOrder.Items.Count(item => !item.IsDeleted),
                CommissionSnapshotJson = subOrder.CommissionSnapshotJson
            })
            .ToListAsync(cancellationToken);

        var orderItems = await _dbContext.CommerceHubSubOrderItems
            .AsNoTracking()
            .Where(item =>
                !item.IsDeleted &&
                !item.SubOrder.IsDeleted &&
                item.SubOrder.MarketplaceOrderId == orderId)
            .OrderBy(item => item.SubOrder.StoreName)
            .ThenBy(item => item.ProductName)
            .ThenBy(item => item.VariantName)
            .Select(item => new SuperAdminOrderItemDto
            {
                ItemId = item.Id,
                SubOrderId = item.SubOrderId,
                VendorName = item.SubOrder.StoreName,
                ProductName = item.ProductName,
                VariantName = item.VariantName,
                ImageUrl = item.ImageUrl,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                TotalPrice = item.Total
            })
            .ToListAsync(cancellationToken);

        var refundRequests = await _dbContext.CommerceHubRefundRequests
            .AsNoTracking()
            .Where(refundRequest =>
                refundRequest.OrderId == orderId &&
                !refundRequest.IsDeleted)
            .OrderByDescending(refundRequest => refundRequest.CreatedAt)
            .ThenByDescending(refundRequest => refundRequest.Id)
            .ToListAsync(cancellationToken);
        var evidenceLookup = await _refundEvidenceService.GetEvidenceLookupAsync(
            refundRequests.Select(refundRequest => refundRequest.Id),
            cancellationToken);
        var refundLookup = refundRequests
            .GroupBy(refundRequest => refundRequest.OrderItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PointOfSale.Core.Entities.CommerceHubRefundRequest>)group.ToList());

        var mappedOrderItems = orderItems
            .Select(item => new SuperAdminOrderItemDto
            {
                ItemId = item.ItemId,
                SubOrderId = item.SubOrderId,
                VendorName = item.VendorName,
                ProductName = item.ProductName,
                VariantName = item.VariantName,
                ImageUrl = item.ImageUrl,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                TotalPrice = item.TotalPrice,
                Refunds = refundLookup.TryGetValue(item.ItemId, out var matchedRefunds)
                    ? matchedRefunds.Select(refundRequest => new SuperAdminOrderRefundEvidenceDto
                    {
                        RefundId = refundRequest.Id,
                        Quantity = refundRequest.Quantity,
                        Amount = refundRequest.TotalAmount,
                        Status = refundRequest.Status,
                        Reason = CustomerRefundService.BuildReasonSummary(refundRequest.ReasonCode, refundRequest.ReasonText),
                        ReasonCode = refundRequest.ReasonCode,
                        ReasonText = refundRequest.ReasonText,
                        MediaUrls = evidenceLookup.TryGetValue(refundRequest.Id, out var media)
                            ? media.Select(entry => entry.Url).ToArray()
                            : Array.Empty<string>(),
                        Media = evidenceLookup.TryGetValue(refundRequest.Id, out var refundMedia)
                            ? refundMedia
                            : Array.Empty<RefundEvidenceMediaDto>(),
                        CreatedAt = refundRequest.CreatedAt,
                        UpdatedAt = refundRequest.UpdatedAt
                    }).ToArray()
                    : Array.Empty<SuperAdminOrderRefundEvidenceDto>()
            })
            .ToList();

        var statusHistory = await _dbContext.CommerceHubSubOrderStatusHistory
            .AsNoTracking()
            .Where(history =>
                !history.IsDeleted &&
                !history.SubOrder.IsDeleted &&
                history.SubOrder.MarketplaceOrderId == orderId)
            .OrderByDescending(history => history.ChangedAtUtc)
            .ThenByDescending(history => history.Id)
            .Select(history => new SuperAdminOrderStatusHistoryDto
            {
                HistoryId = history.Id,
                SubOrderId = history.SubOrderId,
                PreviousStatus = history.PreviousStatus,
                NewStatus = history.NewStatus,
                Reason = history.Reason,
                CorrelationId = history.CorrelationId,
                ChangedAtUtc = history.ChangedAtUtc
            })
            .ToListAsync(cancellationToken);

        var refunds = await _dbContext.CommerceHubRefunds
            .AsNoTracking()
            .Where(refund =>
                !refund.IsDeleted &&
                !refund.SubOrder.IsDeleted &&
                refund.SubOrder.MarketplaceOrderId == orderId)
            .OrderByDescending(refund => refund.ProcessedAtUtc ?? refund.CreatedAt)
            .ThenByDescending(refund => refund.Id)
            .Select(refund => new SuperAdminOrderRefundDetailsDto
            {
                RefundId = refund.Id,
                SubOrderId = refund.SubOrderId,
                RefundRequestId = refund.RefundRequestId,
                OrderItemId = refund.OrderItemId,
                Quantity = refund.Quantity,
                Amount = refund.Amount,
                CommissionReversalAmount = refund.CommissionReversalAmount,
                PromoDiscountReversalAmount = refund.PromoDiscountReversalAmount,
                VendorEarningReversalAmount = refund.VendorEarningReversalAmount,
                Currency = refund.Currency,
                Status = refund.Status,
                Reason = refund.Reason,
                FailureReason = refund.FailureReason,
                IsFullRefund = refund.IsFullRefund,
                IsPostPayoutAdjustment = refund.IsPostPayoutAdjustment,
                CreatedAt = refund.CreatedAt,
                ProcessingStartedAtUtc = refund.ProcessingStartedAtUtc,
                ProcessedAtUtc = refund.ProcessedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new SuperAdminOrderDetailsDto
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            CustomerName = order.CustomerName,
            CustomerPhone = order.CustomerPhone,
            AddressLine = order.AddressLine,
            City = order.City,
            Area = order.Area,
            Notes = order.Notes,
            Currency = order.Currency,
            Subtotal = order.Subtotal,
            ShippingTotal = order.ShippingTotal,
            TotalAmount = order.TotalAmount,
            Status = order.Status,
            PaymentStatus = order.PaymentStatus,
            PaymentMethodCode = order.PaymentMethodCode,
            PaymentMethodName = order.PaymentMethodName,
            PaymentReference = order.PaymentReference,
            PaymentReferenceId = order.PaymentReferenceId,
            PaymentGateway = order.PaymentGateway,
            CreatedAt = order.CreatedAt,
            PaidAt = order.PaidAt,
            VendorCount = order.VendorCount,
            TenantName = BuildTenantSummary(order.PrimaryTenantName, order.VendorCount),
            PlatformCommissionTotal = subOrders.Sum(subOrder => subOrder.PlatformCommissionAmount),
            VendorEarningsTotal = subOrders.Sum(subOrder => subOrder.VendorEarningAmount),
            EventStatus = BuildEventProcessingStatus(payments),
            Payments = payments,
            SubOrders = subOrders,
            OrderItems = mappedOrderItems,
            StatusHistory = statusHistory,
            Refunds = refunds
        };
    }

    public async Task<SuperAdminOrdersCsvExport> ExportOrdersCsvAsync(
        SuperAdminOrdersListQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var snapshot = await BeginReadSnapshotAsync(cancellationToken);
        var filteredQuery = BuildFilteredQuery(query);
        var rowCount = await filteredQuery.CountAsync(cancellationToken);
        if (rowCount > MaxExportRows)
        {
            throw new SuperAdminOrdersExportLimitExceededException(rowCount, MaxExportRows);
        }

        var rows = await ApplySorting(ProjectOrderListQuery(filteredQuery), query)
            .Take(MaxExportRows + 1)
            .ToListAsync(cancellationToken);
        if (rows.Count > MaxExportRows)
        {
            throw new SuperAdminOrdersExportLimitExceededException(rows.Count, MaxExportRows);
        }

        var builder = new StringBuilder();
        builder.AppendLine("Order ID,Order Number,Customer,Tenant,Vendor Count,Total Amount,Currency,Status,Payment Status,Created At UTC");

        foreach (var row in rows)
        {
            var line = string.Join(",", new[]
            {
                EscapeCsv(row.OrderId.ToString()),
                EscapeCsv(row.OrderNumber),
                EscapeCsv(row.CustomerName),
                EscapeCsv(BuildTenantSummary(row.PrimaryTenantName, row.VendorCount)),
                EscapeCsv(row.VendorCount.ToString(CultureInfo.InvariantCulture)),
                EscapeCsv(row.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture)),
                EscapeCsv(row.Currency),
                EscapeCsv(row.Status.ToString()),
                EscapeCsv(row.PaymentStatus.ToString()),
                EscapeCsv(row.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            });

            builder.AppendLine(line);
        }

        return new SuperAdminOrdersCsvExport
        {
            Content = builder.ToString(),
            RowCount = rows.Count
        };
    }

    private IQueryable<PointOfSale.Core.Entities.CommerceHubMarketplaceOrder> BuildBaseOrdersQuery(
        Guid? tenantId = null)
    {
        var ordersQuery = _dbContext.CommerceHubMarketplaceOrders
            .AsNoTracking()
            .Where(order => !order.IsDeleted);

        if (tenantId.HasValue)
        {
            ordersQuery = ordersQuery.Where(order =>
                order.SubOrders.Any(subOrder =>
                    !subOrder.IsDeleted &&
                    subOrder.StoreId == tenantId.Value));
        }

        return ordersQuery;
    }

    private async Task<IDbContextTransaction?> BeginReadSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational() || _dbContext.Database.CurrentTransaction != null)
            return null;
        var transaction = await _dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        if (_dbContext.Database.IsNpgsql())
            await _dbContext.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        return transaction;
    }

    private IQueryable<PointOfSale.Core.Entities.CommerceHubMarketplaceOrder> BuildFilteredQuery(
        SuperAdminOrdersListQuery query)
    {
        var ordersQuery = BuildBaseOrdersQuery(query.TenantId);

        if (query.Status.HasValue)
        {
            ordersQuery = ordersQuery.Where(order => order.Status == query.Status.Value);
        }

        if (query.PaymentStatus.HasValue)
        {
            ordersQuery = ApplyPaymentStatusFilter(ordersQuery, query.PaymentStatus.Value);
        }

        if (query.FromDate.HasValue)
        {
            var fromDateUtc = DateTime.SpecifyKind(query.FromDate.Value.Date, DateTimeKind.Utc);
            ordersQuery = ordersQuery.Where(order => order.CreatedAt >= fromDateUtc);
        }

        if (query.ToDate.HasValue)
        {
            var toDateUtc = DateTime.SpecifyKind(query.ToDate.Value.Date.AddDays(1), DateTimeKind.Utc);
            ordersQuery = ordersQuery.Where(order => order.CreatedAt < toDateUtc);
        }

        if (!string.IsNullOrWhiteSpace(query.NormalizedSearch))
        {
            var searchTerm = query.NormalizedSearch!;
            // Search is literal substring matching, not user-controlled SQL LIKE syntax.
            var escapedSearch = searchTerm.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            var likePattern = $"%{escapedSearch}%";

            if (Guid.TryParse(searchTerm, out var orderId))
            {
                ordersQuery = ordersQuery.Where(order =>
                    order.Id == orderId ||
                    EF.Functions.ILike(order.OrderNumber, likePattern, "\\") ||
                    EF.Functions.ILike(order.FullName, likePattern, "\\"));
            }
            else
            {
                ordersQuery = ordersQuery.Where(order =>
                    EF.Functions.ILike(order.OrderNumber, likePattern, "\\") ||
                    EF.Functions.ILike(order.FullName, likePattern, "\\"));
            }
        }

        return ordersQuery;
    }

    private static IQueryable<OrderListProjection> ProjectOrderListQuery(
        IQueryable<PointOfSale.Core.Entities.CommerceHubMarketplaceOrder> query)
    {
        return query.Select(order => new OrderListProjection
        {
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            CustomerName = order.FullName,
            TotalAmount = order.TotalAmount,
            Currency = order.Currency,
            Status = order.Status,
            PaymentStatus = order.PaymentStatus,
            CreatedAt = order.CreatedAt,
            VendorCount = order.SubOrders.Count(subOrder => !subOrder.IsDeleted),
            PrimaryTenantName = order.SubOrders
                .Where(subOrder => !subOrder.IsDeleted)
                .OrderBy(subOrder => subOrder.StoreName)
                .Select(subOrder => subOrder.StoreName)
                .FirstOrDefault() ?? string.Empty
        });
    }

    private static IQueryable<OrderListProjection> ApplySorting(
        IQueryable<OrderListProjection> query,
        SuperAdminOrdersListQuery filters)
    {
        var ascending = filters.SortDirection == SuperAdminSortDirection.Asc;

        return filters.SortBy switch
        {
            SuperAdminOrdersSortField.OrderNumber => ascending
                ? query.OrderBy(order => order.OrderNumber).ThenByDescending(order => order.CreatedAt).ThenBy(order => order.OrderId)
                : query.OrderByDescending(order => order.OrderNumber).ThenByDescending(order => order.CreatedAt).ThenByDescending(order => order.OrderId),

            SuperAdminOrdersSortField.Customer => ascending
                ? query.OrderBy(order => order.CustomerName).ThenByDescending(order => order.CreatedAt).ThenBy(order => order.OrderId)
                : query.OrderByDescending(order => order.CustomerName).ThenByDescending(order => order.CreatedAt).ThenByDescending(order => order.OrderId),

            SuperAdminOrdersSortField.Tenant => ascending
                ? query.OrderBy(order => order.PrimaryTenantName).ThenByDescending(order => order.CreatedAt).ThenBy(order => order.OrderId)
                : query.OrderByDescending(order => order.PrimaryTenantName).ThenByDescending(order => order.CreatedAt).ThenByDescending(order => order.OrderId),

            SuperAdminOrdersSortField.TotalAmount => ascending
                ? query.OrderBy(order => order.TotalAmount).ThenByDescending(order => order.CreatedAt).ThenBy(order => order.OrderId)
                : query.OrderByDescending(order => order.TotalAmount).ThenByDescending(order => order.CreatedAt).ThenByDescending(order => order.OrderId),

            SuperAdminOrdersSortField.Status => ascending
                ? query.OrderBy(order => order.Status).ThenByDescending(order => order.CreatedAt).ThenBy(order => order.OrderId)
                : query.OrderByDescending(order => order.Status).ThenByDescending(order => order.CreatedAt).ThenByDescending(order => order.OrderId),

            SuperAdminOrdersSortField.PaymentStatus => ascending
                ? query.OrderBy(order => order.PaymentStatus).ThenByDescending(order => order.CreatedAt).ThenBy(order => order.OrderId)
                : query.OrderByDescending(order => order.PaymentStatus).ThenByDescending(order => order.CreatedAt).ThenByDescending(order => order.OrderId),

            SuperAdminOrdersSortField.VendorCount => ascending
                ? query.OrderBy(order => order.VendorCount).ThenByDescending(order => order.CreatedAt).ThenBy(order => order.OrderId)
                : query.OrderByDescending(order => order.VendorCount).ThenByDescending(order => order.CreatedAt).ThenByDescending(order => order.OrderId),

            _ => ascending
                ? query.OrderBy(order => order.CreatedAt).ThenBy(order => order.OrderNumber).ThenBy(order => order.OrderId)
                : query.OrderByDescending(order => order.CreatedAt).ThenByDescending(order => order.OrderNumber).ThenByDescending(order => order.OrderId)
        };
    }

    private async Task<Dictionary<Guid, IReadOnlyList<SuperAdminOrderSubOrderSummaryDto>>> LoadSubOrderSummariesAsync(
        IReadOnlyCollection<Guid> orderIds,
        CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<SuperAdminOrderSubOrderSummaryDto>>();
        }

        var subOrders = await _dbContext.CommerceHubSubOrders
            .AsNoTracking()
            .Where(subOrder =>
                !subOrder.IsDeleted &&
                orderIds.Contains(subOrder.MarketplaceOrderId))
            .OrderBy(subOrder => subOrder.StoreName)
            .Select(subOrder => new SubOrderSummaryProjection
            {
                MarketplaceOrderId = subOrder.MarketplaceOrderId,
                Summary = new SuperAdminOrderSubOrderSummaryDto
                {
                    SubOrderId = subOrder.Id,
                    VendorId = subOrder.StoreId,
                    VendorName = subOrder.StoreName,
                    Amount = subOrder.Total,
                    Currency = subOrder.Currency,
                    Status = subOrder.Status,
                    PaymentStatus = subOrder.PaymentStatus
                }
            })
            .ToListAsync(cancellationToken);

        return subOrders
            .GroupBy(item => item.MarketplaceOrderId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SuperAdminOrderSubOrderSummaryDto>)group
                    .Select(item => item.Summary)
                    .ToList());
    }

    private static IQueryable<PointOfSale.Core.Entities.CommerceHubMarketplaceOrder> ApplyPaymentStatusFilter(
        IQueryable<PointOfSale.Core.Entities.CommerceHubMarketplaceOrder> query,
        SuperAdminOrderPaymentFilter filter)
    {
        var legacyPartiallyPaidStatus = (PaymentStatus)3;

        return filter switch
        {
            SuperAdminOrderPaymentFilter.Paid => query.Where(order =>
                order.PaymentStatus == PaymentStatus.Collected ||
                order.PaymentStatus == PaymentStatus.Settled ||
                order.PaymentStatus == PaymentStatus.Refunded ||
                order.PaymentStatus == PaymentStatus.PartiallyRefunded),

            SuperAdminOrderPaymentFilter.Failed => query.Where(order =>
                FailedPaymentStatuses.Contains(order.PaymentStatus)),

            SuperAdminOrderPaymentFilter.Unpaid => query.Where(order =>
                order.PaymentStatus == PaymentStatus.Pending ||
                order.PaymentStatus == PaymentStatus.Authorized ||
                order.PaymentStatus == legacyPartiallyPaidStatus ||
                order.PaymentStatus == PaymentStatus.AwaitingVerification ||
                order.PaymentStatus == PaymentStatus.CashOnDelivery),
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unsupported payment filter.")
        };
    }

    private async Task<decimal> GetCapturedSalesTodayAsync(
        Guid? tenantId,
        DateTime todayStartUtc,
        DateTime todayEndUtc,
        CancellationToken cancellationToken)
    {
        return await _dbContext.CommerceHubSubOrders
            .AsNoTracking()
            .Where(subOrder =>
                !subOrder.IsDeleted &&
                !subOrder.MarketplaceOrder.IsDeleted &&
                (!tenantId.HasValue || subOrder.StoreId == tenantId.Value) &&
                subOrder.PaidAt >= todayStartUtc &&
                subOrder.PaidAt < todayEndUtc &&
                CapturedPaymentStatuses.Contains(subOrder.PaymentStatus))
            .SumAsync(subOrder => (decimal?)subOrder.Total, cancellationToken) ?? 0m;
    }

    private static string BuildTenantSummary(string? primaryTenantName, int vendorCount)
    {
        var normalizedTenantName = string.IsNullOrWhiteSpace(primaryTenantName)
            ? "Unknown vendor"
            : primaryTenantName.Trim();

        if (vendorCount <= 0)
        {
            return "No vendors";
        }

        if (vendorCount == 1)
        {
            return normalizedTenantName;
        }

        return $"{normalizedTenantName} +{vendorCount - 1} more";
    }

    private static string? BuildEventProcessingStatus(
        IReadOnlyCollection<SuperAdminOrderPaymentDetailsDto> payments)
    {
        if (payments.Count == 0)
        {
            return null;
        }

        return payments.Any(payment => !payment.IsProcessed)
            ? "Pending downstream processing"
            : "Processed";
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var safeValue = IsSpreadsheetFormula(value) ? $"'{value}" : value;
        return safeValue.Contains(',') || safeValue.Contains('"') || safeValue.Contains('\n') || safeValue.Contains('\r')
            ? $"\"{safeValue.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : safeValue;
    }

    private static bool IsSpreadsheetFormula(string value) =>
        value.Length > 0 && (value[0] is '\t' or '\r' or '\n' ||
            value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@');

    private sealed class OrderListProjection
    {
        public Guid OrderId { get; init; }
        public string OrderNumber { get; init; } = string.Empty;
        public string CustomerName { get; init; } = string.Empty;
        public decimal TotalAmount { get; init; }
        public string Currency { get; init; } = "EGP";
        public OrderStatus Status { get; init; }
        public PaymentStatus PaymentStatus { get; init; }
        public DateTime CreatedAt { get; init; }
        public int VendorCount { get; init; }
        public string PrimaryTenantName { get; init; } = string.Empty;
    }

    private sealed class OrderDetailsProjection
    {
        public Guid OrderId { get; init; }
        public string OrderNumber { get; init; } = string.Empty;
        public string CustomerName { get; init; } = string.Empty;
        public string? CustomerPhone { get; init; }
        public string AddressLine { get; init; } = string.Empty;
        public string? City { get; init; }
        public string? Area { get; init; }
        public string? Notes { get; init; }
        public string Currency { get; init; } = "EGP";
        public decimal Subtotal { get; init; }
        public decimal ShippingTotal { get; init; }
        public decimal TotalAmount { get; init; }
        public OrderStatus Status { get; init; }
        public PaymentStatus PaymentStatus { get; init; }
        public string PaymentMethodCode { get; init; } = string.Empty;
        public string PaymentMethodName { get; init; } = string.Empty;
        public string? PaymentReference { get; init; }
        public string? PaymentReferenceId { get; init; }
        public string? PaymentGateway { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? PaidAt { get; init; }
        public int VendorCount { get; init; }
        public string PrimaryTenantName { get; init; } = string.Empty;
    }

    private sealed class OrdersTodayProjection
    {
        public long TotalOrdersToday { get; init; }
    }

    private sealed class SubOrderSummaryProjection
    {
        public Guid MarketplaceOrderId { get; init; }
        public SuperAdminOrderSubOrderSummaryDto Summary { get; init; } = new();
    }
}

public sealed class SuperAdminOrdersExportLimitExceededException : Exception
{
    public SuperAdminOrdersExportLimitExceededException(int rowCount, int maximumRows)
        : base($"The filtered export contains {rowCount} rows; the maximum permitted export is {maximumRows} rows.")
    {
        RowCount = rowCount;
        MaximumRows = maximumRows;
    }

    public int RowCount { get; }
    public int MaximumRows { get; }
}
