using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.SuperAdmin.DTOs;
using PointOfSale.API.SuperAdmin.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.SuperAdmin;

// Real Npgsql/EF model and PostgreSQL constraints, never production connections.
public sealed class SuperAdminOrdersForensicTests : IDisposable
{
    private readonly FinancePostgresTestContext _db = FinancePostgresTestContext.Create();
    private readonly SuperAdminOrdersService _service;
    private readonly Guid _storeA = Guid.NewGuid(), _storeB = Guid.NewGuid();
    private static readonly DateTime Day = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    public SuperAdminOrdersForensicTests()
    {
        var evidence = new Mock<IRefundEvidenceService>();
        evidence.Setup(e => e.GetEvidenceLookupAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<RefundEvidenceMediaDto>>());
        _service = new(_db, evidence.Object, NullLogger<SuperAdminOrdersService>.Instance);
    }

    private CommerceHubMarketplaceOrder Add(string number, DateTime? created = null,
        OrderStatus status = OrderStatus.Pending, PaymentStatus payment = PaymentStatus.Pending,
        bool split = false, string customerName = "Isolated Buyer")
    {
        var customer = CommerceHubCustomer.Create($"{Guid.NewGuid():N}@example.test", customerName, "+201000000001", "fixture-hash");
        var order = new CommerceHubMarketplaceOrder
        {
            Id = Guid.NewGuid(), Customer = customer, CustomerId = customer.Id, OrderNumber = number,
            FullName = customerName, PhoneNumber = "+201000000001", AddressLine = "Fixture street", City = "Fixture city",
            CreatedAt = created ?? Day, Currency = "EGP", Status = status, PaymentStatus = payment,
            Subtotal = split ? 200m : 100m, ShippingTotal = split ? 20m : 10m, TotalAmount = split ? 220m : 110m,
            PaymentMethodCode = "PAYMOB", PaymentMethodName = "Online"
        };
        foreach (var store in split ? new[] { _storeA, _storeB } : new[] { _storeA })
        {
            var sub = new CommerceHubSubOrder
            {
                Id = Guid.NewGuid(), MarketplaceOrder = order, StoreId = store,
                StoreName = store == _storeA ? "Fixture A" : "Fixture B",
                OrderNumber = number + "-" + store.ToString("N")[..6], Currency = "EGP",
                Subtotal = 100m, Shipping = 10m, Total = 110m, Status = status, PaymentStatus = payment,
                PlatformCommissionRate = .1m, PlatformCommissionAmount = 10m, FinalPlatformCommissionAmount = 10m,
                VendorEarningAmount = 100m, CommissionSnapshotJson = "{\"rate\":0.1,\"amount\":10,\"source\":\"fixture-frozen\"}",
                PaymentMethodCode = "PAYMOB", PaymentMethodName = "Online"
            };
            sub.Items.Add(new CommerceHubSubOrderItem
            {
                Id = Guid.NewGuid(), ProductName = "Frozen fixture item", ListingId = Guid.NewGuid(),
                ProductId = Guid.NewGuid(), VariantId = Guid.NewGuid(), CategoryId = Guid.NewGuid(),
                Quantity = 1, UnitPrice = 100m, Total = 100m, PlatformCommissionRate = .1m,
                PlatformCommissionAmount = 10m, FinalPlatformCommissionAmount = 10m
            });
            order.SubOrders.Add(sub);
        }
        _db.CommerceHubMarketplaceOrders.Add(order);
        return order;
    }

    private async Task Save()
    {
        var timestamps = _db.ChangeTracker.Entries<CommerceHubMarketplaceOrder>()
            .Where(e => e.State == EntityState.Added).ToDictionary(e => e.Entity.Id, e => e.Entity.CreatedAt);
        using var lifecycle = CommerceHubBoundaryMutationScope.BeginOrderLifecycle();
        using var payment = CommerceHubBoundaryMutationScope.BeginPayment();
        using var payments = CommerceHubBoundaryMutationScope.BeginPaymentLifecycle();
        using var financial = CommerceHubBoundaryMutationScope.BeginFinancialWorkflow();
        using var refund = CommerceHubBoundaryMutationScope.BeginRefund();
        await _db.SaveChangesAsync();
        // Owned isolated fixture timestamps only: normal SaveChanges stamps creation
        // at now; arrange historical/midnight examples without changing application guards.
        foreach (var (id, time) in timestamps)
            await _db.CommerceHubMarketplaceOrders.IgnoreQueryFilters().Where(o => o.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.CreatedAt, time));
    }

    [Fact]
    public async Task EmptyPopulation_IsNotOnePageOrAFabricatedRow()
    {
        var rows = await _service.GetOrdersAsync(new());
        Assert.Empty(rows.Items); Assert.Equal(0, rows.TotalCount); Assert.Equal(0, rows.TotalPages);
        Assert.Equal(DateTimeKind.Utc, rows.AsOfUtc.Kind);
        var stats = await _service.GetOrderStatsAsync();
        Assert.Equal(0, stats.TotalOrdersToday); Assert.Equal(0, stats.CapturedSalesToday);
        Assert.Null(await _service.GetOrderDetailsAsync(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("ch-test", 1)]
    [InlineData("isolated", 2)]
    [InlineData("%", 1)]
    [InlineData("_", 1)]
    [InlineData("' OR 1=1 --", 0)]
    public async Task Search_IsLiteralCaseInsensitiveAndParameterized(string search, int count)
    {
        Add("CH-TEST"); Add("CH-%_LITERAL"); await Save();
        Assert.Equal(count, (await _service.GetOrdersAsync(new() { Search = search })).TotalCount);
    }

    [Fact]
    public async Task IdentifierSearchAndSplitStoreFilter_CountEachParentOnce()
    {
        var order = Add("CH-SPLIT", split: true);
        var deleted = Add("CH-DELETED");
        deleted.IsDeleted = true;
        await Save();
        Assert.Single((await _service.GetOrdersAsync(new() { Search = order.Id.ToString() })).Items);
        var scoped = await _service.GetOrdersAsync(new() { TenantId = _storeB });
        var row = Assert.Single(scoped.Items);
        Assert.Equal(2, row.VendorCount); Assert.Equal(220m, row.TotalAmount); Assert.Equal(2, row.SubOrders.Count);
        Assert.Empty((await _service.GetOrdersAsync(new() { TenantId = Guid.NewGuid() })).Items);
        Assert.Null(await _service.GetOrderDetailsAsync(deleted.Id));
    }

    [Theory]
    [InlineData(SuperAdminOrderPaymentFilter.Paid, 4)]
    [InlineData(SuperAdminOrderPaymentFilter.Failed, 4)]
    [InlineData(SuperAdminOrderPaymentFilter.Unpaid, 5)]
    public async Task PaymentFilter_PartitionsActualStoredStatesWithoutCountingAttempts(SuperAdminOrderPaymentFilter filter, int count)
    {
        foreach (var value in Enum.GetValues<PaymentStatus>().Distinct().Where(p => p != PaymentStatus.SyntheticQaCollected))
            Add("CH-" + (int)value, payment: value);
        await Save();
        Assert.Equal(count, (await _service.GetOrdersAsync(new() { PaymentStatus = filter })).TotalCount);
    }

    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 1)]
    public async Task DateBoundaries_AreInclusiveUtcCalendarDays(bool from, bool to, int count)
    {
        Add("CH-BEFORE", Day.AddTicks(-10)); Add("CH-START", Day);
        Add("CH-NEXT", Day.AddDays(1)); await Save();
        var result = await _service.GetOrdersAsync(new() { FromDate = from ? Day : null, ToDate = to ? Day : null });
        Assert.Equal(count, result.TotalCount);
    }

    [Theory]
    [InlineData(SuperAdminOrdersSortField.CreatedAt)]
    [InlineData(SuperAdminOrdersSortField.OrderNumber)]
    [InlineData(SuperAdminOrdersSortField.Customer)]
    [InlineData(SuperAdminOrdersSortField.Tenant)]
    [InlineData(SuperAdminOrdersSortField.TotalAmount)]
    [InlineData(SuperAdminOrdersSortField.Status)]
    [InlineData(SuperAdminOrdersSortField.PaymentStatus)]
    [InlineData(SuperAdminOrdersSortField.VendorCount)]
    public async Task EverySort_HasStableNonOverlappingPageBoundaries(SuperAdminOrdersSortField sort)
    {
        for (var i = 0; i < 5; i++) Add($"CH-TIE-{i}");
        await Save();
        foreach (var direction in Enum.GetValues<SuperAdminSortDirection>())
        {
            var ids = new List<Guid>();
            for (var page = 1; page <= 3; page++)
            {
                var r = await _service.GetOrdersAsync(new() { Page = page, PageSize = 2, SortBy = sort, SortDirection = direction });
                Assert.Equal(5, r.TotalCount); Assert.Equal(3, r.TotalPages);
                ids.AddRange(r.Items.Select(o => o.OrderId));
            }
            Assert.Equal(5, ids.Distinct().Count());
        }
    }

    [Fact]
    public async Task Stats_UseUtcCreationFinalPaymentStateAndCollectedSubOrderTime()
    {
        var now = DateTime.UtcNow;
        var good = Add("CH-CAPTURED", now.AddDays(-2), OrderStatus.Confirmed, PaymentStatus.Collected, split: true);
        foreach (var sub in good.SubOrders) sub.PaidAt = now;
        good.PaidAt = now;
        Add("CH-TODAY-PENDING", now);
        Add("CH-TODAY-FAILED", now, OrderStatus.Cancelled, PaymentStatus.Expired);
        var mismatch = Add("CH-MISMATCH", now, OrderStatus.Cancelled, PaymentStatus.AmountMismatch);
        var payment = new CommerceHubPayment
        {
            Id = Guid.NewGuid(), Order = good, Amount = 220m, Currency = "EGP", Status = CommerceHubPaymentRecordStatus.Paid,
            ProviderReference = Guid.NewGuid().ToString(), ProviderTransactionId = Guid.NewGuid().ToString(),
            IsProcessed = true, PaidAtUtc = now
        };
        payment.Attempts.Add(new() { Id = Guid.NewGuid(), AttemptNumber = 1, ProviderReference = "failed-fixture",
            Status = CommerceHubPaymentRecordStatus.Failed, FailureReason = "fixture decline" });
        payment.Attempts.Add(new() { Id = Guid.NewGuid(), AttemptNumber = 2, ProviderReference = "paid-fixture",
            Status = CommerceHubPaymentRecordStatus.Paid });
        good.Payments.Add(payment);
        await Save();
        var stats = await _service.GetOrderStatsAsync();
        Assert.Equal(3, stats.TotalOrdersToday); Assert.Equal(1, stats.PendingOrders);
        Assert.Equal(2, stats.FailedPayments); Assert.Equal(220m, stats.CapturedSalesToday);
        Assert.Equal(110m, (await _service.GetOrderStatsAsync(_storeB)).CapturedSalesToday);
        var detail = await _service.GetOrderDetailsAsync(good.Id);
        Assert.NotNull(detail); Assert.Equal(PaymentStatus.Collected, detail.PaymentStatus);
        Assert.Equal(2, Assert.Single(detail.Payments).Attempts.Count);
        Assert.Equal(20m, detail.PlatformCommissionTotal);
        Assert.All(detail.SubOrders, s => Assert.Contains("fixture-frozen", s.CommissionSnapshotJson));
    }

    [Fact]
    public async Task Csv_ExportsAllFilteredRowsNotPage_AndNeutralizesFormulaCells()
    {
        Add("CH-CSV-1", customerName: "=HYPERLINK(\"bad\")");
        Add("CH-CSV-2", customerName: " +SUM(1,2)");
        Add("CH-OTHER", status: OrderStatus.Cancelled); await Save();
        var export = await _service.ExportOrdersCsvAsync(new() { Page = 2, PageSize = 1, Status = OrderStatus.Pending, Search = "CH-CSV" });
        Assert.Equal(2, export.RowCount);
        Assert.Contains("'=HYPERLINK", export.Content); Assert.Contains("' +SUM", export.Content);
        Assert.DoesNotContain("CH-OTHER", export.Content);
        Assert.DoesNotContain("Phone", export.Content); Assert.DoesNotContain("PaymentToken", export.Content);
    }

    [Fact]
    public async Task FreshRead_DoesNotReuseOldRedisSnapshot()
    {
        Add("CH-FIRST"); await Save();
        Assert.Equal(1, (await _service.GetOrdersAsync(new())).TotalCount);
        Add("CH-SECOND"); await Save();
        Assert.Equal(2, (await _service.GetOrdersAsync(new())).TotalCount);
    }

    [Fact]
    public async Task PersistedRefundAndTransitionHistory_AreBoundToTheirOrderAndUseFrozenAmounts()
    {
        var order = Add("CH-REFUND", status: OrderStatus.Refunded, payment: PaymentStatus.Refunded);
        var other = Add("CH-OTHER");
        await Save();
        var sub = order.SubOrders.Single();
        _db.CommerceHubSubOrderStatusHistory.Add(new()
        {
            Id = Guid.NewGuid(), SubOrderId = sub.Id, PreviousStatus = OrderStatus.Processing,
            NewStatus = OrderStatus.Shipped, ChangedAtUtc = DateTime.UtcNow, Reason = "isolated transition",
        });
        _db.CommerceHubRefunds.Add(new()
        {
            Id = Guid.NewGuid(), TenantId = sub.StoreId, SubOrderId = sub.Id, Amount = 110m,
            CommissionReversalAmount = 10m, VendorEarningReversalAmount = 100m,
            Currency = "EGP", Status = CommerceHubRefundStatus.Completed, Reason = "isolated completed refund",
            IdempotencyKey = "isolated-refund-unique", GatewayStatus = "confirmed",
            IsFullRefund = true, ProcessedAtUtc = DateTime.UtcNow,
        });
        await Save();
        var detail = await _service.GetOrderDetailsAsync(order.Id);
        Assert.NotNull(detail); Assert.Equal(110m, Assert.Single(detail.Refunds).Amount);
        Assert.Equal(sub.Id, Assert.Single(detail.StatusHistory).SubOrderId);
        var otherDetail = await _service.GetOrderDetailsAsync(other.Id);
        Assert.NotNull(otherDetail); Assert.Empty(otherDetail.Refunds); Assert.Empty(otherDetail.StatusHistory);
        Assert.Equal(10m, detail.PlatformCommissionTotal);
        Assert.Equal(PaymentStatus.Refunded, detail.PaymentStatus);
    }

    [Fact]
    public async Task LargeExport_IsRejectedBeforeMaterializingTheFilteredPopulation()
    {
        Add("CH-LIMIT-SEED"); await Save();
        // Only this randomly owned loopback test database. Preserve production-model
        // defaults/constraints by cloning the complete synthetic record, not customer data.
        await _db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "CommerceHubMarketplaceOrders"
            SELECT (jsonb_populate_record(NULL::"CommerceHubMarketplaceOrders",
              to_jsonb(seed) || jsonb_build_object('Id',gen_random_uuid(),'OrderNumber','CH-LIMIT-' || n))).*
            FROM "CommerceHubMarketplaceOrders" seed CROSS JOIN generate_series(1,10001) n
            WHERE seed."OrderNumber"='CH-LIMIT-SEED'
            """);
        var error = await Assert.ThrowsAsync<SuperAdminOrdersExportLimitExceededException>(
            () => _service.ExportOrdersCsvAsync(new()));
        Assert.Equal(10000, error.MaximumRows); Assert.Equal(10002, error.RowCount);
    }

    [Fact]
    public void InvalidEnumsDatesAndPagination_AreRejectedByQueryContract()
    {
        Assert.NotNull(new SuperAdminOrdersListQuery { Status = (OrderStatus)999 }.Validate());
        Assert.NotNull(new SuperAdminOrdersListQuery { PaymentStatus = (SuperAdminOrderPaymentFilter)999 }.Validate());
        Assert.NotNull(new SuperAdminOrdersListQuery { SortBy = (SuperAdminOrdersSortField)999 }.Validate());
        Assert.NotNull(new SuperAdminOrdersListQuery { Page = int.MaxValue, PageSize = 100 }.Validate());
        Assert.NotNull(new SuperAdminOrdersListQuery { FromDate = Day.AddDays(1), ToDate = Day }.Validate());
        Assert.NotNull(new SuperAdminOrdersListQuery { ToDate = DateTime.MaxValue }.Validate());
        Assert.NotNull(new SuperAdminOrdersListQuery { Search = new string('x', 101) }.Validate());
        Assert.Null(new SuperAdminOrdersListQuery { Search = "a", FromDate = Day }.Validate());
    }

    public void Dispose() => _db.Dispose();
}
