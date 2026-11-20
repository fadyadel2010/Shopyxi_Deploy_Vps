using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs;
using PointOfSale.API.Infrastructure.Mvc;
using PointOfSale.API.Security;
using PointOfSale.API.Services.Analytics;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Financial;
using PointOfSale.Core.Security;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Infrastructure.Analytics;
using PointOfSale.Infrastructure.Inventory;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents;
using PointOfSale.Infrastructure.Analytics.Funnels;
using PointOfSale.Infrastructure.Redis.Services;
using PointOfSale.Core.Analytics;
using PointOfSale.Core.Analytics.Attribution;
using PointOfSale.Infrastructure.Attribution;
using PointOfSale.Infrastructure.CampaignCost;
using PointOfSale.Infrastructure.Redis.Configuration;
using Microsoft.Extensions.Options;
using IConnectionMultiplexer = StackExchange.Redis.IConnectionMultiplexer;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests;

public sealed class Group5CBusinessDefinitionTests
{
    private static readonly DateTime Day = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Order_Payment")]
    [InlineData("Order_COD")]
    [InlineData("Order_Wallet")]
    [InlineData("Order_UnresolvedPaymentClearance")]
    public async Task Storefront_PeriodMovement_DoesNotCarryPriorHistoryIntoNewRecognition(string source)
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var order = Order(db, tenant);
        AddLine(db, tenant, order.Id, source, Day.AddDays(-1), 100);
        AddLine(db, tenant, order.Id, source, Day, 50);
        AddLine(db, tenant, order.Id, "Order_Refund", Day, -20);
        AddLine(db, tenant, order.Id, source, Day.AddDays(1), 900);
        await db.SeedReadSnapshotAsync();
        var snapshot = await new StorefrontAnalyticsAuthorityReader(db).GetCommerceSnapshotAsync(tenant, Day, Day.AddDays(1));
        Assert.Equal(1, snapshot.PurchaseCount);
        Assert.Equal(50m, snapshot.RecognizedRevenue);
        Assert.Equal(-20m, snapshot.Adjustments);
        Assert.Equal(30m, snapshot.NetRevenue);
    }

    [Fact]
    public async Task Storefront_RefundOnlyPeriod_AndFullyRefundedOrdersRemainVisible()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var old = Order(db, tenant);
        var current = Order(db, tenant);
        current.IsDeleted = true;
        AddLine(db, tenant, old.Id, "Order_Payment", Day.AddDays(-1), 100);
        AddLine(db, tenant, old.Id, "Order_Refund", Day, -100);
        AddLine(db, tenant, current.Id, "Order_Wallet", Day, 80);
        AddLine(db, tenant, current.Id, "Order_Refund", Day, -80);
        await db.SeedReadSnapshotAsync();
        var reader = new StorefrontAnalyticsAuthorityReader(db);
        var snapshot = await reader.GetCommerceSnapshotAsync(tenant, Day, Day.AddDays(1));
        Assert.Equal(-100m, snapshot.NetRevenue);
        Assert.Equal(80m, snapshot.RecognizedRevenue);
        Assert.Equal(-180m, snapshot.Adjustments);
        Assert.Equal(current.Id, Assert.Single(snapshot.PurchaseOrderIds));
        Assert.Equal(0m, snapshot.NetRevenueByOrder[current.Id]);
        var lifetime = await reader.GetNetRevenueByOrderAsync(tenant, [old.Id, current.Id], Day.AddDays(1));
        Assert.All(lifetime.Values, value => Assert.Equal(0m, value));
    }

    [Fact]
    public async Task Storefront_ReversalChainsUseTenantScopedRelationships_NotSourceIdCoincidence()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var order = Order(db, tenant);
        var original = AddLine(db, tenant, order.Id, "Order_Payment", Day.AddDays(-1), 100);
        var unrelated = new Order { Id = original.Id, TenantId = tenant, Source = OrderSource.Ecommerce, Currency = "EGP", OrderNumber = "COLLISION" };
        db.Orders.Add(unrelated); // journal and order identities live in separate namespaces
        var reversal = AddLine(db, tenant, original.Id, "Reversal", Day, -100, original.Id);
        AddLine(db, tenant, reversal.Id, "Reversal", Day, 100, reversal.Id);
        AddLine(db, tenant, order.Id, "CommerceHubFinancialTransaction", Day, 999);
        var foreign = Tenant(db);
        var foreignOrder = Order(db, foreign);
        AddLine(db, foreign, foreignOrder.Id, "Order_Payment", Day, 777);
        await db.SeedReadSnapshotAsync();
        var snapshot = await new StorefrontAnalyticsAuthorityReader(db).GetCommerceSnapshotAsync(tenant, Day, Day.AddDays(1));
        Assert.Equal(0m, snapshot.NetRevenue);
        Assert.Equal(0, snapshot.PurchaseCount);
        Assert.True(snapshot.NetRevenueByOrder.ContainsKey(order.Id));
        Assert.False(snapshot.NetRevenueByOrder.ContainsKey(unrelated.Id));
        Assert.False(snapshot.NetRevenueByOrder.ContainsKey(foreignOrder.Id));
    }

    [Fact]
    public async Task Storefront_CrossTenantReversalFailsClosed()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var other = Tenant(db);
        var order = Order(db, other);
        var original = AddLine(db, other, order.Id, "Order_Payment", Day, 100);
        AddLine(db, tenant, original.Id, "Reversal", Day, -100, original.Id);
        await db.SeedReadSnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new StorefrontAnalyticsAuthorityReader(db).GetCommerceSnapshotAsync(tenant, Day, Day.AddDays(1)));
    }

    [Fact]
    public async Task Storefront_CrossCurrencyTotalIsRejected_NotConverted()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var order = Order(db, tenant);
        order.Currency = "USD";
        AddLine(db, tenant, order.Id, "Order_Payment", Day, 10);
        await db.SeedReadSnapshotAsync();
        await Assert.ThrowsAsync<FinancialCurrencyMismatchException>(() =>
            new StorefrontAnalyticsAuthorityReader(db).GetCommerceSnapshotAsync(tenant, Day, Day.AddDays(1)));
    }

    [Fact]
    public async Task OperationalActivity_UsesPostingTimestamp_NotAccountingMidnight_AndIsNotCapped()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var now = DateTime.UtcNow;
        for (var n = 0; n < 13; n++)
        {
            var order = Order(db, tenant);
            var journal = AddLine(db, tenant, order.Id, "Order_Payment", Day.AddDays(-1), 10);
            journal.SetPostedAtUtc(now.AddMinutes(-1));
        }
        var future = AddLine(db, tenant, Order(db, tenant).Id, "Order_Payment", Day, 10);
        future.SetPostedAtUtc(now);
        await db.SeedReadSnapshotAsync();
        var activity = await new StorefrontAnalyticsAuthorityReader(db)
            .GetRecognizedOrderActivityAsync(tenant, now.AddMinutes(-5), now);
        Assert.Equal(13, activity.Count);
    }

    [Fact]
    public async Task Inventory_UsesAvailableQuantityAndConfiguredZero_ValuesOwnedBucketsAndTransitOnce()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var stock = Stock(db, tenant, 5m, 5m, 2m, 3.125m, 0);
        var inStock = Stock(db, tenant, 1m, 0m, 0m, 2m, 0);
        Stock(db, Tenant(db), 100, 0, 0, 999, 10);
        var transfer = new StockTransfer
        {
            TenantId = tenant, Status = TransferStatus.Dispatched,
            FromWarehouseId = stock.WarehouseId, ToWarehouseId = inStock.WarehouseId,
            Items = [new StockTransferItem { ProductId = stock.Variant.ProductId, Quantity = 2m, DispatchedUnitCost = 4m }]
        };
        db.StockTransfers.Add(transfer);
        await db.SeedReadSnapshotAsync();
        var low = await InventoryReportingReader.LowStockPositions(db, tenant).ToListAsync();
        Assert.Equal(stock.Id, Assert.Single(low).Id);
        var valuation = await InventoryReportingReader.ReadValuationAsync(db, tenant);
        Assert.Equal(23.875m, valuation.WarehouseValue);
        Assert.Equal(8m, valuation.InTransitValue);
        Assert.Equal(31.875m, valuation.TotalOwnedValue);
        var controller = Controller(db, tenant, UserRole.Owner);
        var response = Assert.IsType<OkObjectResult>((await controller.GetLowStockItems()).Result);
        var row = Assert.Single(Assert.IsType<List<LowStockItemResponse>>(response.Value));
        Assert.Equal("Out of Stock", row.Status);
        Assert.Equal(0m, row.CurrentStock);
        Assert.Equal(0m, row.ReorderPoint);
    }

    [Fact]
    public async Task Inventory_MissingTransitCostMakesTotalUnknown_NotZero()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        db.StockTransfers.Add(new StockTransfer
        {
            TenantId = tenant, Status = TransferStatus.Dispatched,
            Items = [new StockTransferItem { Quantity = 4, DispatchedUnitCost = null }]
        });
        await db.SeedReadSnapshotAsync();
        var valuation = await InventoryReportingReader.ReadValuationAsync(db, tenant);
        Assert.False(valuation.IsComplete);
        Assert.Null(valuation.InTransitValue);
        Assert.Null(valuation.TotalOwnedValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public async Task Inventory_ReportPreservesCompleteLegacyNumericContract(int unitCost)
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        db.StockTransfers.Add(new StockTransfer
        {
            TenantId = tenant, Status = TransferStatus.Dispatched,
            Items = [new StockTransferItem { Quantity = 1, DispatchedUnitCost = unitCost }]
        });
        await db.SeedReadSnapshotAsync();
        var controller = new ReportsController(db, new TestTenantContext(tenant), NullLogger<ReportsController>.Instance,
            Mock.Of<ITreasuryReadService>(), Mock.Of<ICashboxLedgerAccountService>());

        var response = Assert.IsType<OkObjectResult>(
            (await controller.GetInventoryValuation(CancellationToken.None)).Result);
        var dto = Assert.IsType<InventoryValuationDto>(response.Value);
        var json = JsonSerializer.SerializeToElement(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        Assert.True(dto.ValuationComplete);
        Assert.Equal((decimal)unitCost, json.GetProperty("inTransitValue").GetDecimal());
        Assert.Equal((decimal)unitCost, json.GetProperty("totalInventoryValue").GetDecimal());
        Assert.Equal(0m, json.GetProperty("warehouseInventoryValue").GetDecimal());
    }

    [Fact]
    public async Task Inventory_IncompleteReportFailsClosedForLegacyAndCurrentClients()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        db.StockTransfers.Add(new StockTransfer
        {
            TenantId = tenant, Status = TransferStatus.Dispatched,
            Items = [new StockTransferItem { Quantity = 4, DispatchedUnitCost = null }]
        });
        await db.SeedReadSnapshotAsync();
        var controller = new ReportsController(db, new TestTenantContext(tenant), NullLogger<ReportsController>.Instance,
            Mock.Of<ITreasuryReadService>(), Mock.Of<ICashboxLedgerAccountService>());

        var response = Assert.IsType<ObjectResult>(
            (await controller.GetInventoryValuation(CancellationToken.None)).Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        var json = JsonSerializer.SerializeToElement(response.Value);
        Assert.Equal("INVENTORY_VALUATION_UNAVAILABLE", json.GetProperty("code").GetString());
        Assert.False(json.TryGetProperty("inTransitValue", out _));
        Assert.False(json.TryGetProperty("totalInventoryValue", out _));
        Assert.False(json.TryGetProperty("warehouseValuations", out _));
    }

    [Theory]
    [InlineData("inactive-warehouse")]
    [InlineData("deleted-warehouse")]
    [InlineData("inactive-variant")]
    [InlineData("deleted-variant")]
    [InlineData("inactive-product")]
    [InlineData("deleted-product")]
    [InlineData("untracked-product")]
    [InlineData("deleted-stock")]
    [InlineData("foreign-parent")]
    public async Task Inventory_EligibilityIsSharedAndTenantClosed(string exclusion)
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var stock = Stock(db, tenant, 1, 1, 1, 20, 10);
        switch (exclusion)
        {
            case "inactive-warehouse": stock.Warehouse.IsActive = false; break;
            case "deleted-warehouse": stock.Warehouse.IsDeleted = true; break;
            case "inactive-variant": stock.Variant.IsActive = false; break;
            case "deleted-variant": stock.Variant.IsDeleted = true; break;
            case "inactive-product": stock.Variant.Product.IsActive = false; break;
            case "deleted-product": stock.Variant.Product.IsDeleted = true; break;
            case "untracked-product": stock.Variant.Product.TrackStock = false; break;
            case "deleted-stock": stock.IsDeleted = true; break;
            case "foreign-parent": stock.Warehouse.TenantId = Guid.NewGuid(); break;
        }
        await db.SeedReadSnapshotAsync();
        Assert.Empty(await InventoryReportingReader.LowStockPositions(db, tenant).ToListAsync());
        Assert.Equal(0m, (await InventoryReportingReader.ReadValuationAsync(db, tenant)).TotalOwnedValue);
    }

    [Fact]
    public async Task DashboardAndReportsUseTheSameValuationAndPositionThresholds()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        Stock(db, tenant, 4, 4, 2, 10, 0);
        Stock(db, tenant, 3, 0, 0, 5, 0);
        await db.SeedReadSnapshotAsync();
        var dashboard = Assert.IsType<DashboardStatsResponse>(Assert.IsType<OkObjectResult>(
            (await Controller(db, tenant, UserRole.Owner).GetDashboardStats()).Result).Value);
        var reports = new ReportsController(db, new TestTenantContext(tenant), NullLogger<ReportsController>.Instance,
            Mock.Of<ITreasuryReadService>(), Mock.Of<ICashboxLedgerAccountService>());
        var report = Assert.IsType<InventoryValuationDto>(Assert.IsType<OkObjectResult>(
            (await reports.GetInventoryValuation(CancellationToken.None)).Result).Value);
        Assert.Equal(report.TotalInventoryValue, dashboard.InventoryValue);
        Assert.Equal(report.LowStockItemsCount, dashboard.LowStockCount);
        Assert.Equal(1, report.LowStockItemsCount);
        Assert.NotEqual(Guid.Empty, Assert.Single(report.LowStockItems).WarehouseId);
    }

    [Fact]
    public void SharedQueriesTranslateForNpgsqlWithoutOpeningAConnection()
    {
        var tenant = Guid.NewGuid();
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=synthetic_never_opened;Username=synthetic;Password=synthetic").Options,
            new TestTenantContext(tenant));
        var stockSql = InventoryReportingReader.LowStockPositions(db, tenant).ToQueryString();
        Assert.Contains("ReservedQuantity", stockSql);
        Assert.Contains("LowStockThreshold", stockSql);
        Assert.DoesNotContain("MinimumStockLevel", stockSql);
        var salesSql = PosSalesReportingReader.RevenueEntries(db, tenant).ToQueryString();
        Assert.Contains("4010", salesSql);
        Assert.Contains("TenantId", salesSql);
    }

    [Fact]
    public async Task LiveServiceBoundsFutureEvents_AndCountsAllOrdersNotOnlyTenActivityRows()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var activity = Enumerable.Range(0, 13).Select(_ => Order(db, tenant))
            .ToDictionary(o => o.Id, _ => DateTime.UtcNow.AddMinutes(-1));
        await db.SeedReadSnapshotAsync();
        var authority = new Mock<IStorefrontAnalyticsAuthorityReader>();
        authority.Setup(x => x.GetRecognizedOrderActivityAsync(tenant, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activity);
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        cursor.SetupGet(x => x.Current).Returns([new BsonDocument { ["summary"] = new BsonArray(), ["recentEvents"] = new BsonArray() }]);
        cursor.SetupSequence(x => x.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true).ReturnsAsync(false);
        var collection = new Mock<IMongoCollection<AnalyticsEventDocument>>(MockBehavior.Strict);
        collection.SetupGet(x => x.Settings).Returns(new MongoCollectionSettings());
        collection.SetupGet(x => x.DocumentSerializer).Returns(BsonSerializer.SerializerRegistry.GetSerializer<AnalyticsEventDocument>());
        PipelineDefinition<AnalyticsEventDocument, BsonDocument>? observed = null;
        collection.Setup(x => x.AggregateAsync(It.IsAny<PipelineDefinition<AnalyticsEventDocument, BsonDocument>>(),
                It.IsAny<AggregateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineDefinition<AnalyticsEventDocument, BsonDocument>, AggregateOptions, CancellationToken>((pipeline, _, _) => observed = pipeline)
            .ReturnsAsync(cursor.Object);
        collection.Setup(x => x.AggregateAsync(It.IsAny<IClientSessionHandle>(),
                It.IsAny<PipelineDefinition<AnalyticsEventDocument, BsonDocument>>(), It.IsAny<AggregateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IClientSessionHandle, PipelineDefinition<AnalyticsEventDocument, BsonDocument>, AggregateOptions, CancellationToken>((_, pipeline, _, _) => observed = pipeline)
            .ReturnsAsync(cursor.Object);
        var mongo = new Mock<IMongoDatabase>();
        mongo.Setup(x => x.GetCollection<AnalyticsEventDocument>("analytics_events", It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        var response = await new StorefrontLiveMonitoringService(db, authority.Object, mongo.Object).GetLiveMonitoringAsync(tenant);
        Assert.Equal(13, response.RecentOrders);
        Assert.Equal(10, response.RecentActivity.Count);
        var rendered = observed!.Render(new RenderArgs<AnalyticsEventDocument>(
            BsonSerializer.SerializerRegistry.GetSerializer<AnalyticsEventDocument>(), BsonSerializer.SerializerRegistry));
        var match = rendered.Documents[0]["$match"].AsBsonDocument;
        Assert.Equal(tenant.ToString(), match["TenantId"].AsString);
        Assert.Equal(response.WindowEndUtc, match["OccurredAtUtc"]["$lt"].ToUniversalTime());
        Assert.Equal(response.WindowStartUtc, match["OccurredAtUtc"]["$gte"].ToUniversalTime());
    }

    [Theory]
    [InlineData(UserRole.Owner, true)]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Cashier, false)]
    [InlineData(UserRole.Warehouse, false)]
    public async Task Dashboard_FieldAuthorizationOmitsFinancialValues_NotFalseZero(UserRole role, bool financial)
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        await db.SeedReadSnapshotAsync();
        var result = await Controller(db, tenant, role).GetDashboardStats();
        var dto = Assert.IsType<DashboardStatsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(financial, dto.FinancialSummaryAvailable);
        var json = JsonSerializer.SerializeToElement(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        foreach (var field in new[] { "todaySales", "inventoryValue", "totalRevenue", "outstandingReceivables", "outstandingPayables" })
            Assert.Equal(financial, json.TryGetProperty(field, out _));
        Assert.True(json.TryGetProperty("lowStockCount", out _));
    }

    [Fact]
    public async Task Pos_DistinctInvoicesSignedReversalsAndFutureUpperBounds()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var today = DateTime.UtcNow.Date;
        var invoice = Guid.NewGuid();
        AddLine(db, tenant, invoice, "SalesInvoice", today, 100, code: "4010");
        AddLine(db, tenant, invoice, "SalesInvoice", today, 120, code: "4010");
        AddLine(db, tenant, Guid.NewGuid(), "Return", today, -20, code: "4010");
        AddLine(db, tenant, Guid.NewGuid(), "Reversal", today, -100, code: "4010");
        AddLine(db, tenant, Guid.NewGuid(), "SalesInvoice", today.AddDays(1), 900, code: "4010");
        await db.SeedReadSnapshotAsync();
        var dto = Assert.IsType<DashboardStatsResponse>(Assert.IsType<OkObjectResult>(
            (await Controller(db, tenant, UserRole.Owner).GetDashboardStats()).Result).Value);
        Assert.Equal(100m, dto.TodaySales);
        Assert.Equal(100m, dto.MonthSales);
        Assert.Equal(100m, dto.YearSales);
        Assert.Equal(1, dto.TodayTransactionCount);
        Assert.Null(dto.SalesChangePercentage);
    }

    [Fact]
    public void CostRedaction_RemovesNestedCosts_PreservesSellingPricesAndQuantities()
    {
        var node = JsonNode.Parse("""{"inTransitValue":24,"postedValue":36,"items":[{"costPrice":12,"purchasePrice":13,"price":20,"quantity":5,"units":[{"purchasePrice":2,"sellingPrice":3}]}]}""")!;
        FinancialCostResponseFilter.Redact(node);
        Assert.DoesNotContain("costPrice", node.ToJsonString());
        Assert.DoesNotContain("purchasePrice", node.ToJsonString());
        Assert.Null(node["inTransitValue"]);
        Assert.Null(node["postedValue"]);
        Assert.Equal(20, node["items"]![0]!["price"]!.GetValue<int>());
        Assert.Equal(5, node["items"]![0]!["quantity"]!.GetValue<int>());
    }

    [Fact]
    public void PurchaseCostRedaction_PreservesReceivingIdentityAndQuantity_NotAmounts()
    {
        var node = JsonNode.Parse("""{"invoiceNumber":"SYNTHETIC","totalAmount":120,"items":[{"quantity":5,"unitPrice":24,"totalAmount":120}],"payments":[{"amount":120}]}""")!;
        FinancialCostResponseFilter.Redact(node, purchase: true);
        Assert.Equal("SYNTHETIC", node["invoiceNumber"]!.GetValue<string>());
        Assert.Equal(5, node["items"]![0]!["quantity"]!.GetValue<int>());
        Assert.DoesNotContain("unitPrice", node.ToJsonString());
        Assert.DoesNotContain("totalAmount", node.ToJsonString());
        Assert.Null(node["payments"]![0]!["amount"]);
    }

    private static Guid Tenant(DashboardReadQueryFixture db)
    {
        var tenant = new Tenant { Name = "Synthetic only", Slug = Guid.NewGuid().ToString("N"), Currency = "EGP" };
        db.Tenants.Add(tenant);
        return tenant.Id;
    }

    [Theory]
    [InlineData(2027, 1, 1)]
    [InlineData(2026, 12, 1)]
    [InlineData(2028, 3, 1)]
    public async Task Pos_UtcCalendarBoundariesAgreeWithNormalizedGeneralLedgerWindow(int year, int month, int day)
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var start = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        var old = AddLine(db, tenant, Guid.NewGuid(), "SalesInvoice", start.AddTicks(-10), 40m, code: "4010");
        AddLine(db, tenant, old.Id, "Reversal", start, -40m, old.Id, code: "4010");
        AddLine(db, tenant, old.SourceId, "SalesInvoice", start, 55m, code: "4010");
        AddLine(db, tenant, Guid.NewGuid(), "Return", start.AddDays(1).AddTicks(-10), -5m, code: "4010");
        AddLine(db, tenant, Guid.NewGuid(), "SalesInvoice", start.AddDays(1), 999m, code: "4010");
        var accountId = db.ChangeTracker.Entries<Account>().Single(e => e.Entity.TenantId == tenant).Entity.Id;
        await db.SeedReadSnapshotAsync();
        var snapshot = await PosSalesReportingReader.ReadAsync(db, tenant, start, CancellationToken.None);
        Assert.Equal(10m, snapshot.TodaySales);
        Assert.Equal(40m, snapshot.YesterdaySales);
        Assert.Equal(-75m, snapshot.SalesChangePercentage);
        var gl = await new FinancialReportService(db, NullLogger<FinancialReportService>.Instance)
            .GetGeneralLedgerAsync(tenant, accountId, start, start.AddDays(1).AddTicks(-10));
        Assert.Equal(snapshot.TodaySales, gl.Entries.Sum(e => e.CreditAmount - e.DebitAmount));
        Assert.Equal(1, snapshot.TodayTransactionCount);
    }

    [Fact]
    public async Task SignedPartyNetsRetainCustomerCreditAndSupplierDebit_NotOverdueOrPositiveClamp()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        db.AccountLedgers.AddRange(
            new AccountLedger { TenantId = tenant, PartyType = PartyType.Customer, PartyId = Guid.NewGuid(), DebitAmount = 10m, CreditAmount = 50m },
            new AccountLedger { TenantId = tenant, PartyType = PartyType.Customer, PartyId = Guid.NewGuid(), DebitAmount = 10m },
            new AccountLedger { TenantId = tenant, PartyType = PartyType.Supplier, PartyId = Guid.NewGuid(), DebitAmount = 20m },
            new AccountLedger { TenantId = Guid.NewGuid(), PartyType = PartyType.Customer, PartyId = Guid.NewGuid(), DebitAmount = 999m });
        await db.SeedReadSnapshotAsync();
        var dto = Assert.IsType<DashboardStatsResponse>(Assert.IsType<OkObjectResult>(
            (await Controller(db, tenant, UserRole.Owner).GetDashboardStats()).Result).Value);
        Assert.Equal(-30m, dto.OutstandingReceivables);
        Assert.Equal(-20m, dto.OutstandingPayables);
    }

    [Theory]
    [InlineData(TransferStatus.Dispatched, 8)]
    [InlineData(TransferStatus.Completed, 0)]
    [InlineData(TransferStatus.Cancelled, 0)]
    [InlineData(TransferStatus.Reversed, 0)]
    public async Task TransitOnlyIncludesDispatchedNotReceivedCancelledOrReversed(TransferStatus status, int expected)
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        db.StockTransfers.Add(new StockTransfer
        {
            TenantId = tenant, Status = status,
            Items = [new StockTransferItem { Quantity = 2m, DispatchedUnitCost = 4m }]
        });
        await db.SeedReadSnapshotAsync();
        Assert.Equal((decimal)expected, (await InventoryReportingReader.ReadValuationAsync(db, tenant)).InTransitValue);
    }

    [Fact]
    public async Task StorefrontService_AovUsesGrossRecognition_AndFullyRefundedOrderUnitsStayVisible()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        var order = Order(db, tenant);
        order.IsDeleted = true;
        db.OrderItems.Add(new OrderItem
        {
            OrderId = order.Id, Order = order, ProductName = "Synthetic refunded item", Quantity = 2,
        });
        await db.SeedReadSnapshotAsync();
        var authority = new Mock<IStorefrontAnalyticsAuthorityReader>();
        authority.Setup(x => x.GetCommerceSnapshotAsync(tenant, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorefrontCommerceAnalyticsSnapshot([order.Id], new Dictionary<Guid, decimal> { [order.Id] = 0m })
            {
                RecognizedRevenue = 80m, CurrencyCode = "EGP"
            });
        var response = await new StorefrontAnalyticsDashboardService(db, EmptyBehavioralMongo(), authority.Object,
            Mock.Of<IAnalyticsFunnelService>()).GetDashboardAsync(tenant, "today");
        Assert.Equal(0m, response.Revenue);
        Assert.Equal(80m, response.RecognizedRevenue);
        Assert.Equal(-80m, response.RevenueAdjustments);
        Assert.Equal(80m, response.AverageOrderValue);
        Assert.Equal(1, response.Orders);
        Assert.Equal(2, Assert.Single(response.TopSellingProducts).Sales);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData(null)]
    public async Task StorefrontService_AbandonedEstimateCannotSumAnUnconfirmedOrDifferentCurrency(string? currency)
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        db.CheckoutSessions.Add(new CheckoutSession
        {
            TenantId = tenant, Status = CheckoutSessionStatus.Abandoned, AbandonedAtUtc = DateTime.UtcNow,
            CartTotal = 123m, Currency = currency,
        });
        await db.SeedReadSnapshotAsync();
        var authority = new Mock<IStorefrontAnalyticsAuthorityReader>();
        authority.Setup(x => x.GetCommerceSnapshotAsync(tenant, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorefrontCommerceAnalyticsSnapshot([], new Dictionary<Guid, decimal>()) { CurrencyCode = "EGP" });
        var service = new StorefrontAnalyticsDashboardService(db, EmptyBehavioralMongo(), authority.Object, Mock.Of<IAnalyticsFunnelService>());
        if (currency == null)
            await Assert.ThrowsAsync<FinancialCurrencyMissingException>(() => service.GetDashboardAsync(tenant, "today"));
        else
            await Assert.ThrowsAsync<FinancialCurrencyMismatchException>(() => service.GetDashboardAsync(tenant, "today"));
    }

    private static IMongoDatabase EmptyBehavioralMongo()
    {
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        cursor.SetupGet(x => x.Current).Returns([new BsonDocument { ["counts"] = new BsonArray(), ["visits"] = new BsonArray() }]);
        cursor.SetupSequence(x => x.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true).ReturnsAsync(false);
        var collection = new Mock<IMongoCollection<AnalyticsEventDocument>>();
        collection.SetupGet(x => x.Settings).Returns(new MongoCollectionSettings());
        collection.SetupGet(x => x.DocumentSerializer).Returns(BsonSerializer.SerializerRegistry.GetSerializer<AnalyticsEventDocument>());
        collection.Setup(x => x.AggregateAsync(It.IsAny<PipelineDefinition<AnalyticsEventDocument, BsonDocument>>(),
            It.IsAny<AggregateOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(cursor.Object);
        var mongo = new Mock<IMongoDatabase>();
        mongo.Setup(x => x.GetCollection<AnalyticsEventDocument>("analytics_events", It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        return mongo.Object;
    }

    [Fact]
    public async Task RealtimeObservationsExcludeFutureTelemetry_AndDoNotUseFinancialPeriodPurchaseCounts()
    {
        var tenant = Guid.NewGuid();
        var observed = AnalyticsEvent.Create(tenant, AnalyticsEventType.PageView);
        observed.OccurredAtUtc = DateTime.UtcNow.AddMinutes(-1);
        observed.Actor.SessionId = "observed";
        var future = AnalyticsEvent.Create(tenant, AnalyticsEventType.PageView);
        future.OccurredAtUtc = DateTime.UtcNow.AddHours(1);
        future.Actor.SessionId = "future";
        var hot = new Mock<IAnalyticsHotStoreService>();
        hot.Setup(x => x.GetRecentEventsAsync(tenant, null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([observed, future]);
        var authority = new Mock<IStorefrontAnalyticsAuthorityReader>();
        authority.Setup(x => x.GetRecognizedOrderActivityAsync(tenant, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, DateTime>());
        var response = await new AnalyticsRealtimeService(hot.Object, authority.Object, NullLogger<AnalyticsRealtimeService>.Instance)
            .GetDashboardAsync(tenant);
        Assert.Equal(1, response.ActiveUsersLast5Min);
        Assert.Equal(observed.EventId, Assert.Single(response.RecentEvents).EventId);
        authority.Verify(x => x.GetCommerceSnapshotAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AttributionAndRoi_IncludeRefundOnlyMovementsWithoutInventingNewOrdersOrNetAov()
    {
        await using var db = DashboardReadQueryFixture.Create();
        var tenant = Tenant(db);
        await db.SeedReadSnapshotAsync();
        var recognized = Guid.NewGuid();
        var refunded = Guid.NewGuid();
        var snapshot = new StorefrontCommerceAnalyticsSnapshot([recognized],
            new Dictionary<Guid, decimal> { [recognized] = 0m, [refunded] = -30m })
        {
            CurrencyCode = "EGP", RecognizedRevenue = 100m,
            RecognizedRevenueByOrder = new Dictionary<Guid, decimal> { [recognized] = 100m },
        };
        var authority = new Mock<IStorefrontAnalyticsAuthorityReader>();
        authority.Setup(x => x.GetCommerceSnapshotAsync(tenant, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        var documents = new[] { recognized, refunded }.Select(id => new OrderAttributionDocument
        {
            TenantId = tenant, OrderId = id, Source = "synthetic", Medium = "cpc", Campaign = "review",
            Model = AttributionModel.LastTouch.ToString(), Currency = "USD", OrderValue = 999m,
        }).ToList(); // Projection money/currency is deliberately wrong; SQL overlay must win.
        var collection = new Mock<IMongoCollection<OrderAttributionDocument>>();
        collection.SetupGet(x => x.Settings).Returns(new MongoCollectionSettings());
        collection.SetupGet(x => x.DocumentSerializer).Returns(BsonSerializer.SerializerRegistry.GetSerializer<OrderAttributionDocument>());
        collection.Setup(x => x.CountDocumentsAsync(It.IsAny<FilterDefinition<OrderAttributionDocument>>(),
            It.IsAny<CountOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(2);
        collection.Setup(x => x.FindAsync(It.IsAny<FilterDefinition<OrderAttributionDocument>>(),
            It.IsAny<FindOptions<OrderAttributionDocument, OrderAttributionDocument>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var cursor = new Mock<IAsyncCursor<OrderAttributionDocument>>();
                cursor.SetupGet(x => x.Current).Returns(documents);
                cursor.SetupSequence(x => x.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true).ReturnsAsync(false);
                return Task.FromResult(cursor.Object);
            });
        var mongo = new Mock<IMongoDatabase>();
        mongo.Setup(x => x.GetCollection<OrderAttributionDocument>("order_attributions", It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        var store = new OrderAttributionStore(Mock.Of<IConnectionMultiplexer>(), mongo.Object, authority.Object,
            Options.Create(new RedisSettings()), NullLogger<OrderAttributionStore>.Instance);
        var source = Assert.Single(await store.GetRevenueBySourceAsync(tenant, Day, Day.AddDays(1), AttributionModel.LastTouch));
        Assert.Equal(-30m, source.TotalRevenue);
        Assert.Equal(100m, source.RecognizedRevenue);
        Assert.Equal(-130m, source.RevenueAdjustments);
        Assert.Equal(1, source.OrderCount);
        Assert.Equal(2, source.AffectedOrderCount);
        Assert.Equal(100m, source.AverageOrderValue);
        var controller = new AdminAnalyticsAttributionController(store, Mock.Of<IAttributionContextStore>(), new TestTenantContext(tenant));
        var api = Assert.IsType<RevenueSummaryResponse>(Assert.IsType<OkObjectResult>(
            await controller.GetRevenueSummary("today", null, null)).Value);
        Assert.Equal(-30m, api.TotalRevenue);
        Assert.Equal(100m, api.RecognizedRevenue);
        Assert.Equal(-130m, api.RevenueAdjustments);
        Assert.Equal(1, api.TotalOrders);
        Assert.Equal(2, api.AffectedOrders);
        Assert.Equal(100m, Assert.Single(api.RevenueBySource).AverageOrderValue);
        var page = await store.GetAttributionPageAsync(tenant, Day, Day.AddDays(1), AttributionModel.LastTouch);
        Assert.All(page.Items, item => Assert.Equal("EGP", item.Currency));
        var costs = new Mock<ICampaignCostService>();
        costs.Setup(x => x.GetByPeriodAsync(tenant, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CampaignCost { Source = "synthetic", Medium = "cpc", Campaign = "review", CostAmount = 10m, Currency = "EGP" }]);
        var roi = new CampaignRoiService(costs.Object, store, db, NullLogger<CampaignRoiService>.Instance);
        var campaign = Assert.Single(await roi.CalculateAsync(tenant, Day, Day.AddDays(1), AttributionModel.LastTouch));
        Assert.Equal(-30m, campaign.Revenue);
        Assert.Equal(100m, campaign.AverageOrderValue);
        Assert.Equal(1, campaign.Orders);
        Assert.Equal(-40m, campaign.Profit);
        var sourceRoi = Assert.Single(await roi.GetRoiBySourceAsync(tenant, Day, Day.AddDays(1), AttributionModel.LastTouch));
        Assert.Equal(100m, sourceRoi.AverageOrderValue);
        Assert.Equal(-30m, sourceRoi.TotalRevenue);
        var roiController = new AdminCampaignCostController(costs.Object, roi, new TestTenantContext(tenant),
            Mock.Of<IAnalyticsTenantCurrencyResolver>());
        var roiApi = Assert.IsType<RoiSummaryResponse>(Assert.IsType<OkObjectResult>(
            await roiController.GetRoiSummary("today", null, null)).Value);
        Assert.Equal(100m, roiApi.RecognizedRevenue);
        Assert.Equal(-130m, roiApi.RevenueAdjustments);
        Assert.Equal(100m, Assert.Single(roiApi.Sources).AverageOrderValue);
        var campaignApi = Assert.IsType<CampaignCostRoiResponse>(Assert.IsType<OkObjectResult>(
            await roiController.GetRoiByCampaign("today", null, null)).Value);
        Assert.Equal(100m, campaignApi.RecognizedRevenue);
        Assert.Equal(-130m, Assert.Single(campaignApi.Campaigns).RevenueAdjustments);
    }

    private static Order Order(DashboardReadQueryFixture db, Guid tenant)
    {
        var order = new Order { TenantId = tenant, Source = OrderSource.Ecommerce, Currency = "EGP", OrderNumber = Guid.NewGuid().ToString("N") };
        db.Orders.Add(order);
        return order;
    }

    private static JournalEntry AddLine(DashboardReadQueryFixture db, Guid tenant, Guid sourceId,
        string type, DateTime date, decimal net, Guid? reverses = null, string code = "4020")
    {
        var key = Guid.NewGuid().ToString("N");
        var journal = JournalEntry.Create(tenant, key, date, type, sourceId, null, "Synthetic read fixture", key, null);
        if (reverses.HasValue) journal.MarkAsReversal(reverses.Value);
        var account = db.ChangeTracker.Entries<Account>().Select(e => e.Entity)
            .FirstOrDefault(a => a.TenantId == tenant && a.AccountCode == code);
        if (account is null) { account = Account.Create(tenant, code, "Synthetic revenue", AccountType.Revenue); db.LedgerAccounts.Add(account); }
        db.JournalEntries.Add(journal);
        db.LedgerEntries.Add(net > 0
            ? LedgerEntry.CreateCredit(tenant, journal.Id, account.Id, net, 0, 1)
            : LedgerEntry.CreateDebit(tenant, journal.Id, account.Id, -net, 0, 1));
        return journal;
    }

    private static ProductVariantStock Stock(DashboardReadQueryFixture db, Guid tenant,
        decimal quantity, decimal reserved, decimal damaged, decimal cost, int threshold)
    {
        var product = new Product { TenantId = tenant, Name = "Synthetic product", IsActive = true, TrackStock = true, LowStockThreshold = threshold };
        var variant = new ProductVariant { TenantId = tenant, ProductId = product.Id, Product = product, IsActive = true, CostPrice = cost };
        var warehouse = new Warehouse { TenantId = tenant, Name = "Synthetic warehouse", IsActive = true };
        var stock = new ProductVariantStock
        {
            TenantId = tenant, Variant = variant, VariantId = variant.Id, Warehouse = warehouse, WarehouseId = warehouse.Id,
            Quantity = quantity, ReservedQuantity = reserved, DamagedQuantity = damaged
        };
        db.ProductVariantStocks.Add(stock);
        return stock;
    }

    private static DashboardController Controller(DashboardReadQueryFixture db, Guid tenant, UserRole role)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("tenant_id", tenant.ToString()),
            new Claim(ClaimTypes.Role, role.ToString()), new Claim(PosSecurityConstants.SessionBindingClaim, "synthetic"),
            new Claim(PosSecurityConstants.SqlAuthorityClaim, PosSecurityConstants.SqlAuthorityValue)
        ], "synthetic"));
        return new(db, new TestTenantContext(tenant), new MemoryCache(new MemoryCacheOptions()), NullLogger<DashboardController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
    }
}
