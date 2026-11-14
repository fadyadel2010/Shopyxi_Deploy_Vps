using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.API.Controllers;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Core.Entities.Affiliate;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Text.Json;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateP54aReadModelTests
{
    private sealed class QueryCapture : DbCommandInterceptor
    {
        public List<string> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Queries.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task PrimaryReadOnly_HistoryQueriesAreBoundedAndBatched_WithoutCustomerColumns()
    {
        var capture = new QueryCapture();
        await using var db = await AffiliateP53ePrimarySupport.Open("P54A-query-shape", capture);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var affiliate = await db.AffiliateAccounts.IgnoreQueryFilters().Where(a => a.ReferralCode == "MEMOXP155504").Select(a => a.Id).SingleAsync();
        capture.Queries.Clear();
        await AffiliatePortalReadStore.ReadActivityAsync(db, affiliate, pageSize: 1);
        var small = capture.Queries.Count;
        capture.Queries.Clear();
        await AffiliatePortalReadStore.ReadActivityAsync(db, affiliate, pageSize: 100);
        Assert.InRange(capture.Queries.Count, 4, 8);
        Assert.InRange(small, 4, 8);
        Assert.Contains(capture.Queries, q => q.Contains("LIMIT") && q.Contains("RecordedAtUtc") && q.Contains("ORDER BY"));
        Assert.All(capture.Queries, q => { Assert.DoesNotContain("GuestEmail", q); Assert.DoesNotContain("ShippingAddress", q); Assert.DoesNotContain("PaymentReference", q); });
        var root = PointOfSale.Tests.Infrastructure.TestConfiguration.GetSolutionRoot();
        await File.WriteAllTextAsync(Path.Combine(root, ".ai/reports/p54a-query-shape.json"), JsonSerializer.Serialize(new
        { pageOneQueries = small, pageHundredQueries = capture.Queries.Count, sql = capture.Queries }, new JsonSerializerOptions { WriteIndented = true }));
    }
    [Fact]
    public void LineageValidator_AdmitsImmutableParents_RejectsForeignOrMissingLinks()
    {
        var store = Guid.NewGuid(); var account = Guid.NewGuid(); var order = Guid.NewGuid();
        var line = Guid.NewGuid(); var attribution = Guid.NewGuid();
        var earned = AffiliateCommissionLedger.CreateEarned(store, attribution, account, order, line, 50, .1m, null, "EGP");
        var approved = AffiliateCommissionLedger.CreateApproved(earned);
        var paid = AffiliateCommissionLedger.CreatePaid(approved, null);
        var reversed = AffiliateCommissionLedger.CreateReversal(store, earned.Id, attribution, account, order, line, 50, 5, .1m, "EGP");
        var canonical = new AffiliateCanonicalOrder { OrderId = order, StoreTenantId = store, AffiliateAccountId = account,
            AttributionId = attribution, OrderCurrency = "EGP", Items = [new() { OrderItemId = line }] };
        var entries = new[] { earned, approved, paid, reversed }.ToDictionary(e => e.Id);
        Assert.All(entries.Values, e => Assert.True(AffiliatePortalReadStore.HasLineage(e, canonical, entries, [])));
        entries.Remove(approved.Id);
        Assert.False(AffiliatePortalReadStore.HasLineage(paid, canonical, entries, []));
        canonical.StoreTenantId = Guid.NewGuid();
        Assert.False(AffiliatePortalReadStore.HasLineage(earned, canonical, entries, []));
        canonical.StoreTenantId = store; canonical.HasUnclassifiedQaPayment = true;
        Assert.False(AffiliatePortalReadStore.HasLineage(earned, canonical, entries, []));
        canonical.IsSyntheticQa = true;
        Assert.True(AffiliatePortalReadStore.HasLineage(earned, canonical, entries, []));
    }
    [Fact]
    public async Task PrimaryReadOnly_OrderDetailsMatchCanonicalSnapshots_AndRejectForeignScope()
    {
        await using var db = await AffiliateP53ePrimarySupport.Open("P54A-read-only-orders");
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var affiliate = await db.AffiliateAccounts.IgnoreQueryFilters().SingleAsync(a => a.ReferralCode == "MEMOXP155504");
        var ids = await AffiliatePortalReadStore.Orders(db, affiliate.Id).Select(o => o.Id).Take(100).ToArrayAsync();
        Assert.NotEmpty(ids);
        var orders = await AffiliatePortalReadStore.ReadOrdersAsync(db, affiliate.Id, ids);
        foreach (var dto in orders)
        {
            var order = await db.Orders.IgnoreQueryFilters().AsNoTracking().Include(o => o.Items).SingleAsync(o => o.Id == dto.OrderId);
            Assert.Equal(order.Total, dto.OrderTotal); Assert.Equal(order.Currency, dto.OrderCurrency);
            Assert.Equal(order.ShippingCost, dto.ShippingCharge); Assert.Equal(order.PaymentFee, dto.PaymentFee);
            Assert.Equal(order.Status.ToString(), dto.OrderStatus); Assert.Equal(order.FulfillmentStatus.ToString(), dto.FulfillmentStatus);
            Assert.All(dto.Items, i => Assert.Equal(order.Items.Single(x => x.Id == i.OrderItemId).Quantity, i.Quantity));
            Assert.Equal(order.Items.Count(i => !i.IsDeleted), dto.Items.Count);
            Assert.Equal(affiliate.Id, dto.AffiliateAccountId); Assert.Equal(order.PromotionLinkId, dto.PromotionLinkId);
            Assert.Equal(order.AffiliateReferralCode, dto.ReferralCode);
        }
        Assert.Empty(await AffiliatePortalReadStore.ReadOrdersAsync(db, Guid.NewGuid(), ids));
        Assert.Empty(await AffiliatePortalReadStore.ReadOrdersAsync(db, affiliate.Id, ids, Guid.NewGuid()));
        Assert.Empty(db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted));
    }

    [Fact]
    public async Task PrimaryReadOnly_ActivityIsStablePagedScoped_AndKeepsExactLedgerValues()
    {
        await using var db = await AffiliateP53ePrimarySupport.Open("P54A-read-only-activity");
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var affiliate = await db.AffiliateAccounts.IgnoreQueryFilters().SingleAsync(a => a.ReferralCode == "MEMOXP155504");
        var first = await AffiliatePortalReadStore.ReadActivityAsync(db, affiliate.Id, 1, 3);
        var second = await AffiliatePortalReadStore.ReadActivityAsync(db, affiliate.Id, 2, 3);
        Assert.Equal(3, first.Entries.Count); Assert.NotEmpty(second.Entries);
        Assert.Empty(first.Entries.Select(e => e.EntryId).Intersect(second.Entries.Select(e => e.EntryId)));
        Assert.Equal(first.Entries.Select(e => e.EntryId), (await AffiliatePortalReadStore.ReadActivityAsync(db, affiliate.Id, 1, 3)).Entries.Select(e => e.EntryId));
        var expected = await db.AffiliateCommissionLedger.IgnoreQueryFilters().AsNoTracking().Where(e => e.AffiliateAccountId == affiliate.Id)
            .OrderByDescending(e => e.RecordedAtUtc).ThenByDescending(e => e.Id).Take(6).ToListAsync();
        Assert.Equal(expected.Select(e => e.Id), first.Entries.Concat(second.Entries).Select(e => e.EntryId));
        foreach (var row in first.Entries.Concat(second.Entries))
        {
            var ledger = expected.Single(e => e.Id == row.EntryId);
            Assert.Equal(ledger.CommissionAmount, row.Amount); Assert.Equal(ledger.BaseAmount, row.CommissionBase);
            Assert.Equal(ledger.Currency, row.Currency); Assert.Equal(ledger.EntryType.ToString(), row.EntryType);
            Assert.True(row.SourceOrderAvailable); Assert.Equal(ledger.OriginalEntryId, row.OriginalEntryId);
        }
        Assert.Empty((await AffiliatePortalReadStore.ReadActivityAsync(db, Guid.NewGuid())).Entries);
        Assert.Empty((await AffiliatePortalReadStore.ReadActivityAsync(db, affiliate.Id, storeId: Guid.NewGuid())).Entries);
        var bounded = await AffiliatePortalReadStore.ReadActivityAsync(db, affiliate.Id, -1, 1000);
        Assert.Equal(1, bounded.Page); Assert.Equal(100, bounded.PageSize); Assert.True(bounded.Entries.Count <= 100);
        Assert.Contains(bounded.Entries, e => e.IsSyntheticQa && e.Classification == "SyntheticQaExcluded");
        Assert.DoesNotContain(bounded.Entries, e => e.Classification.Contains("Withdrawable"));
    }

    [Fact]
    public async Task PrimaryReadOnly_ControllerUsesSignedIdentity_AndSqlPagingAndStatusFilters()
    {
        await using var db = await AffiliateP53ePrimarySupport.Open("P54A-read-only-controller");
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var id = await db.AffiliateAccounts.IgnoreQueryFilters().Where(a => a.ReferralCode == "MEMOXP155504").Select(a => a.Id).SingleAsync();
        var controller = new AffiliatePortalController(db, null!, null!, null!, null!, null!, NullLogger<AffiliatePortalController>.Instance)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedResult>((await controller.GetCommissionActivity()).Result);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("affiliate_account_id", id.ToString())], "test"));
        var result = Assert.IsType<AffiliateOrdersResponse>(Assert.IsType<OkObjectResult>((await controller.GetOrders(pageSize: 2)).Result).Value);
        Assert.Equal(2, result.Orders.Count); Assert.All(result.Orders, o => Assert.NotNull(o.Canonical));
        var filtered = Assert.IsType<AffiliateOrdersResponse>(Assert.IsType<OkObjectResult>((await controller.GetOrders(status: "Reversed")).Result).Value);
        Assert.NotEmpty(filtered.Orders); Assert.All(filtered.Orders, o => Assert.Equal("Reversed", o.Status));
        Assert.IsType<NotFoundResult>((await controller.GetSourceOrder(result.Orders[0].OrderId, Guid.NewGuid())).Result);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("affiliate_account_id", Guid.NewGuid().ToString())], "test"));
        Assert.IsType<NotFoundResult>((await controller.GetSourceOrder(result.Orders[0].OrderId)).Result);
    }
}
