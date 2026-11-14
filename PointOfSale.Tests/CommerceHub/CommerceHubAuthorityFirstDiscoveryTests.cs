using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using Npgsql;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Configuration;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.Services.CommerceHub.Finance;
using PointOfSale.API.Services.CommerceHub.FinancialLedger;
using PointOfSale.API.Services.CommerceHub.Wallet;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.CommerceHubFinance;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Enums.CommerceHubFinance;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Elasticsearch.Services;
using PointOfSale.Infrastructure.MongoDB.Configuration;
using PointOfSale.Infrastructure.MongoDB.Documents;
using PointOfSale.Infrastructure.MongoDB.Services;

namespace PointOfSale.Tests.CommerceHub;

// Every scenario runs against both a local unit store and the required real
// PostgreSQL/Mongo fixture. A missing fixture is an explicit failure, not a skip.
[Collection("CommerceHub authority discovery")]
public sealed class CommerceHubAuthorityFirstDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicListAndSearch_HaveExactStableTotalsAndFacetsOnAllPages(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 31);
        var seen = new List<Guid>();
        var pages = new List<CommerceHubSearchResponse>();
        foreach (var page in new[] { 1, 2, 3, 4, 5 })
        {
            var search = await f.Service.SearchAsync(new CommerceHubSearchQuery { Page = page, PageSize = 10, Sort = "price_asc" }, []);
            pages.Add(search);
            var list = await f.Service.GetProductsAsync(new CommerceHubProductsQuery { Page = page, PageSize = 10, Sort = "price_asc" });
            Assert.Equal(31, search.Total);
            Assert.Equal(31, list.TotalCount);
            Assert.Equal(search.Items.Select(item => item.Id), list.Items.Select(item => item.Id));
            Assert.Equal(page <= 3 ? 10 : page == 4 ? 1 : 0, search.Items.Count);
            Assert.Equal(31, search.Facets.Categories.Sum(category => category.Count));
            Assert.Equal(100d, search.Facets.Price.Min);
            Assert.Equal(130d, search.Facets.Price.Max);
            Assert.Empty(search.Aggregations); // Raw derivative attributes remain suppressed.
            seen.AddRange(search.Items.Select(item => item.Id));
            var raw = await f.AuthorityAsync(new CommerceHubSearchRequest { Page = page, PageSize = 10, Sort = "price_asc" });
            Assert.Equal(115d, raw.PriceStats!.Avg);
            Assert.Equal(31, raw.PriceStats.Count);
        }
        Assert.Equal(31, seen.Distinct().Count());
        Assert.Equal(f.Variants.OrderBy(item => item.Price).Select(item => item.Id),
            (await f.Service.GetProductsAsync(new CommerceHubProductsQuery { PageSize = 100, Sort = "price_asc" })).Items.Select(item => item.VariantId));
        f.Repository.Verify(repository => repository.SearchPublishedProductsAsync(It.IsAny<CommerceHubSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        f.SearchRead.VerifyNoOtherCalls();
        if (real)
        {
            var directory = Environment.GetEnvironmentVariable("APP17B_EVIDENCE_DIR") ?? throw new InvalidOperationException("Native evidence directory required");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "frontend-authoritative-pages.json"),
                JsonSerializer.Serialize(pages, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingStaleHiddenDuplicateAndReorderedProjections_CannotOwnMembership(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 37);
        var expected = (await f.Service.GetProductsAsync(new CommerceHubProductsQuery { PageSize = 100, Sort = "price_asc" })).Items;
        if (real)
        {
            // All first-page derivative records are hidden/nonexistent in SQL;
            // valid rows occur later and most admitted SQL rows have no Mongo row.
            await f.Mongo.CommerceHubProducts.InsertManyAsync(Enumerable.Range(0, 12).Select(index => new CommerceHubProductDocument
            {
                Id = Guid.NewGuid(), TenantId = f.TenantId, ProductId = Guid.NewGuid(), VariantId = Guid.NewGuid(),
                ProductName = "poison first page", Price = 1m, IsPublished = true, CategoryId = f.CategoryId
            }));
            await f.Mongo.CommerceHubProducts.InsertManyAsync(expected.Take(3).Reverse().Select(item => new CommerceHubProductDocument
            {
                Id = item.Id, TenantId = item.TenantId, ProductId = item.ProductId, VariantId = item.VariantId,
                ProductName = "stale name", Price = 999999m, IsPublished = false, CategoryId = Guid.NewGuid()
            }));
            // Duplicate product/variant projection with an unrelated listing ID.
            await f.Mongo.CommerceHubProducts.InsertOneAsync(new CommerceHubProductDocument
            {
                Id = Guid.NewGuid(), TenantId = f.TenantId, ProductId = expected[0].ProductId,
                VariantId = expected[0].VariantId, ProductName = "duplicate", IsPublished = true
            });
            Assert.True(await f.Mongo.CommerceHubProducts.CountDocumentsAsync(Builders<CommerceHubProductDocument>.Filter.Empty) >= 16);
        }
        var actual = await f.Service.GetProductsAsync(new CommerceHubProductsQuery { PageSize = 100, Sort = "price_asc" });
        Assert.Equal(37, actual.TotalCount);
        Assert.Equal(expected.Select(item => item.Id), actual.Items.Select(item => item.Id));
        Assert.All(actual.Items, item => Assert.StartsWith("Authority", item.ProductName));
        Assert.DoesNotContain(actual.Items, item => item.Price == 999999m || item.Price == 1m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentSqlPriceStockCategoryAndTypedFilters_AreAppliedBeforePaging(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 35);
        f.Variants[30].OnlinePrice = 5m;
        f.Products[0].TrackStock = true; // no stock: must not be in an InStock page
        f.Listings[1].Publish(f.SecondCategoryId, "{\"brand\":\"other\",\"size\":5}");
        await f.Db.SaveChangesAsync();
        var response = await f.Service.SearchAsync(new CommerceHubSearchQuery { PageSize = 5, Sort = "price_asc", InStock = true }, []);
        Assert.Equal(34, response.Total);
        Assert.Equal(f.Variants[30].Id, response.Items[0].VariantId);
        Assert.Equal(5m, response.Items[0].Price);
        Assert.DoesNotContain(response.Items, item => item.ProductId == f.Products[0].Id);
        var byCategory = await f.Service.SearchAsync(new CommerceHubSearchQuery { CategoryId = f.SecondCategoryId }, []);
        Assert.Single(byCategory.Items);
        Assert.Equal(1, byCategory.Total);
        var byAttribute = await f.Service.SearchAsync(new CommerceHubSearchQuery { CategoryId = f.CategoryId, PageSize = 5 },
            [new CommerceHubSearchFilter { Key = "brand", Terms = ["sql"] }, new CommerceHubSearchFilter { Key = "size", GreaterThanOrEqualTo = 10 }]);
        Assert.Equal(25, byAttribute.Total);
        Assert.Equal(25, byAttribute.Facets.Categories.Sum(category => category.Count));
        var wrongCase = await f.Service.SearchAsync(new CommerceHubSearchQuery { CategoryId = f.CategoryId },
            [new CommerceHubSearchFilter { Key = "brand", Terms = ["SQL"] }]);
        Assert.Empty(wrongCase.Items);
        Assert.Equal(0, wrongCase.Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenantExclusionGovernanceAndFinancialSuppression_AffectCountsFacetsAndPages(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 25);
        var second = await f.AddVendorAsync("other");
        await f.AddListingAsync(second, 500m);
        var all = await f.Service.GetProductsAsync(new CommerceHubProductsQuery { PageSize = 100 });
        Assert.Equal(26, all.TotalCount);
        var scoped = await f.Service.GetProductsAsync(new CommerceHubProductsQuery { TenantId = f.TenantId, PageSize = 100 });
        Assert.Equal(25, scoped.TotalCount);
        Assert.All(scoped.Items, item => Assert.Equal(f.TenantId, item.TenantId));
        var excluded = await f.AuthorityAsync(new CommerceHubSearchRequest { ExcludedTenantIds = [f.TenantId], PageSize = 100 });
        Assert.Equal(1, excluded.TotalCount);
        Assert.All(excluded.Items, item => Assert.Equal(second, item.TenantId));
        f.Suspended.Add(f.TenantId);
        var suppressed = await f.Service.SearchAsync(new CommerceHubSearchQuery { PageSize = 100 }, []);
        Assert.Equal(1, suppressed.Total);
        Assert.All(suppressed.Items, item => Assert.Equal(second, item.TenantId));
        Assert.Equal(500d, suppressed.Facets.Price.Min);
        Assert.Equal(1, suppressed.Facets.Categories.Sum(category => category.Count));
        f.Suspended.Clear();
        var profile = await f.Db.UserGovernanceProfiles.SingleAsync(profile => profile.SubjectId == f.TenantId);
        profile.Status = UserGovernanceStatus.Suspended;
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, (await f.Service.GetProductsAsync(new CommerceHubProductsQuery())).TotalCount);
        profile.Status = UserGovernanceStatus.Active;
        var tenant = await f.Db.Tenants.IgnoreQueryFilters().SingleAsync(tenant => tenant.Id == f.TenantId);
        tenant.IsActive = false;
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, (await f.Service.GetProductsAsync(new CommerceHubProductsQuery())).TotalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpansionTrackingAndZeroResultSuggestions_UseOnlySqlAdmittedPopulation(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 29);
        f.Intelligence.Setup(service => service.ExpandQueryAsync("alias", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "authority" });
        var response = await f.Service.SearchAsync(new CommerceHubSearchQuery { Q = "alias", PageSize = 10 }, []);
        Assert.Equal(29, response.Total);
        Assert.Equal(10, response.Items.Count);
        f.Intelligence.Verify(service => service.TrackQuery(It.Is<CommerceHubTrackedSearchQuery>(query => query.ResultCount == 0)), Times.Once);
        f.Suspended.Add(f.TenantId);
        var hidden = await f.Service.SearchAsync(new CommerceHubSearchQuery { Q = "authority" }, []);
        Assert.Equal(0, hidden.Total);
        Assert.Empty(hidden.Items);
        Assert.NotNull(hidden.ZeroResultSuggestions);
        Assert.Empty(hidden.ZeroResultSuggestions.Products);
        Assert.Empty(hidden.ZeroResultSuggestions.Categories);
        Assert.Empty(hidden.ZeroResultSuggestions.Filters);
        f.Repository.Verify(repository => repository.GetZeroResultSuggestionsAsync(It.IsAny<CommerceHubSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityFailureAndFinancialFailure_DoNotReturnDerivativeFallbackOrTrackSuccess(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 3);
        f.Finance.Setup(service => service.GetVendorsSuspendedFromNewOrdersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fixture authority admission failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SearchAsync(new CommerceHubSearchQuery(), []));
        f.Intelligence.Verify(service => service.TrackQuery(It.IsAny<CommerceHubTrackedSearchQuery>()), Times.Never);
        f.Repository.VerifyNoOtherCalls();
        if (real)
        {
            await using var transaction = await f.Db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.GetProductsAsync(new CommerceHubProductsQuery()));
        }
    }

    [Fact]
    public async Task RealPostgresAuthenticationFailure_IsFailClosed_AndRecoveryUsesSqlAgain()
    {
        await using var f = await Fixture.CreateAsync(true, 3);
        var original = f.Db.Database.GetConnectionString()!;
        var invalid = new NpgsqlConnectionStringBuilder(original) { Password = "deliberately-invalid-fixture-password", Pooling = false };
        f.Db.Database.SetConnectionString(invalid.ConnectionString);
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => f.Service.SearchAsync(new CommerceHubSearchQuery(), []));
        f.Repository.VerifyNoOtherCalls();
        f.Intelligence.Verify(service => service.TrackQuery(It.IsAny<CommerceHubTrackedSearchQuery>()), Times.Never);
        f.Db.Database.SetConnectionString(original);
        Assert.Equal(3, (await f.Service.GetProductsAsync(new CommerceHubProductsQuery())).TotalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoreThanTwoAuthorityBatches_AreExhaustedWithoutTruncation(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 513);
        foreach (var page in new[] { 1, 51, 52, 53 })
        {
            var result = await f.Service.SearchAsync(new CommerceHubSearchQuery { Page = page, PageSize = 10, Sort = "price_asc" }, []);
            Assert.Equal(513, result.Total);
            Assert.Equal(page <= 51 ? 10 : page == 52 ? 3 : 0, result.Items.Count);
            Assert.Equal(513, result.Facets.Categories.Sum(category => category.Count));
            Assert.Equal(100d, result.Facets.Price.Min);
            Assert.Equal(612d, result.Facets.Price.Max);
        }
        if (real)
        {
            await f.RecordPlansAsync();
            var changed = false;
            f.Finance.Setup(service => service.GetVendorsSuspendedFromNewOrdersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
                .Returns(async (IEnumerable<Guid> _, CancellationToken cancellation) =>
                {
                    if (!changed)
                    {
                        changed = true;
                        // Commit on a separate fixture connection after batch one
                        // was read. The ongoing request must retain its snapshot.
                        await using var connection = new NpgsqlConnection(f.Db.Database.GetConnectionString());
                        await connection.OpenAsync(cancellation);
                        await using var update = connection.CreateCommand();
                        update.CommandText = "UPDATE \"Products\" SET \"IsOnlineVisible\" = false WHERE \"Id\" = @id";
                        update.Parameters.AddWithValue("id", f.Products[500].Id);
                        Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellation));
                    }
                    return (IReadOnlyCollection<Guid>)Array.Empty<Guid>();
                });
            var coherent = await f.Service.SearchAsync(new CommerceHubSearchQuery { Page = 52, PageSize = 10, Sort = "price_asc" }, []);
            Assert.True(changed);
            Assert.Equal(513, coherent.Total);
            Assert.Equal(3, coherent.Items.Count);
            Assert.Equal(513, coherent.Facets.Categories.Sum(category => category.Count));
            var subsequent = await f.Service.SearchAsync(new CommerceHubSearchQuery { Page = 52, PageSize = 10, Sort = "price_asc" }, []);
            Assert.Equal(512, subsequent.Total);
            Assert.Equal(2, subsequent.Items.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EverySupportedSortAndExtremeBeyondEndPage_UsesCurrentSqlKeysAndExactTotals(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 29);
        var now = DateTime.UtcNow;
        for (var index = 0; index < f.Listings.Count; index++) f.Listings[index].CreatedAt = now.AddDays(-index);
        f.Variants[1].OnlinePrice = f.Variants[0].OnlinePrice;
        await f.Db.SaveChangesAsync();
        foreach (var sort in new[] { "price_asc", "price_desc", "newest", "relevance" })
        {
            var query = new CommerceHubSearchQuery { Page = 2, PageSize = 7, Sort = sort, Q = "authority" };
            var response = await f.Service.SearchAsync(query, []);
            var expected = sort == "price_desc"
                ? f.Variants.OrderByDescending(variant => variant.OnlinePrice).ThenBy(variant => f.Products.Single(product => product.Id == variant.ProductId).Name, StringComparer.Ordinal)
                : f.Variants.OrderBy(variant => f.Products.Single(product => product.Id == variant.ProductId).Name, StringComparer.Ordinal);
            Assert.Equal(29, response.Total);
            Assert.Equal(expected.Skip(7).Take(7).Select(variant => variant.Id), response.Items.Select(item => item.VariantId));
        }
        var beyond = await f.Service.SearchAsync(new CommerceHubSearchQuery { Page = int.MaxValue, PageSize = 100 }, []);
        Assert.Equal(29, beyond.Total);
        Assert.Empty(beyond.Items);
        Assert.Equal(29, beyond.Facets.Categories.Sum(category => category.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenCategoryAncestorsProductsAndVariantLineage_CannotContribute(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 9);
        f.Products[0].IsActive = false;
        f.Products[1].IsOnlineVisible = false;
        f.Products[2].IsSearchable = false;
        f.Products[3].PublishStatus = PublishStatus.Draft;
        f.Variants[4].IsActive = false;
        f.Variants[5].SemanticLifecycleState = ProductVariantSemanticLifecycleState.SplitParent;
        f.Variants[5].SemanticSupersededAtUtc = DateTime.UtcNow;
        f.Variants[5].SemanticOperationId = Guid.NewGuid();
        f.Listings[6].Unpublish();
        f.Listings[7].Publish(f.SecondCategoryId);
        var root = await f.Db.CommerceHubCategories.SingleAsync(category => category.Id == f.SecondCategoryId);
        root.IsActive = false;
        await f.Db.SaveChangesAsync();
        var result = await f.Service.SearchAsync(new CommerceHubSearchQuery(), []);
        Assert.Equal(1, result.Total);
        Assert.Equal(f.Products[8].Id, Assert.Single(result.Items).ProductId);
        Assert.Equal(108d, result.Facets.Price.Min);
        Assert.Equal(1, result.Facets.Categories.Sum(category => category.Count));
        var primary = await f.Db.CommerceHubCategories.SingleAsync(category => category.Id == f.CategoryId);
        primary.ParentId = f.SecondCategoryId;
        await f.Db.SaveChangesAsync();
        Assert.Equal(0, (await f.Service.SearchAsync(new CommerceHubSearchQuery(), [])).Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealFinancialDebtAdmission_SuppressesOnlyTheIndebtedVendor_AndRecovers(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 27);
        var other = await f.AddVendorAsync("solvent");
        await f.AddListingAsync(other, 900m);
        var debt = CommerceHubFinancialTransaction.Create(CommerceHubFinancialEntityType.Vendor, f.TenantId,
            CommerceHubFinancialTransactionType.Adjustment, CommerceHubFinancialTransactionDirection.Debit,
            2000m, "EGP", CommerceHubFinancialReferenceType.Adjustment, Guid.NewGuid(), "Fixture aged vendor debt",
            createdAtUtc: DateTime.UtcNow.AddDays(-5));
        f.Db.CommerceHubFinancialTransactions.Add(debt);
        using (CommerceHubBoundaryMutationScope.BeginFinancialWorkflow()) await f.Db.SaveChangesAsync();
        var control = new CommerceHubVendorFinancialControlService(f.Db, new CommerceHubWalletService(f.Db),
            new CommerceHubVendorDebtService(f.Db, Options.Create(new CommerceHubVendorDebtOptions())),
            Mock.Of<ICommerceHubFinancialLedgerService>(), Mock.Of<ICommerceHubFinancialSafetyGuardService>());
        f.Finance.Setup(service => service.GetVendorsSuspendedFromNewOrdersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<Guid> vendors, CancellationToken cancellation) => control.GetVendorsSuspendedFromNewOrdersAsync(vendors, cancellation));
        Assert.Contains(f.TenantId, await control.GetVendorsSuspendedFromNewOrdersAsync([f.TenantId, other]));
        var result = await f.Service.SearchAsync(new CommerceHubSearchQuery { PageSize = 100 }, []);
        Assert.Equal(1, result.Total);
        Assert.Equal(other, Assert.Single(result.Items).TenantId);
        Assert.Equal(900d, result.Facets.Price.Min);
        Assert.Equal(0, (await f.Service.GetProductsAsync(new CommerceHubProductsQuery { TenantId = f.TenantId })).TotalCount);
        f.Db.CommerceHubFinancialTransactions.Add(CommerceHubFinancialTransaction.Create(CommerceHubFinancialEntityType.Vendor, f.TenantId,
            CommerceHubFinancialTransactionType.Adjustment, CommerceHubFinancialTransactionDirection.Credit,
            2000m, "EGP", CommerceHubFinancialReferenceType.Adjustment, Guid.NewGuid(), "Fixture debt recovery"));
        using (CommerceHubBoundaryMutationScope.BeginFinancialWorkflow()) await f.Db.SaveChangesAsync();
        Assert.Equal(28, (await f.Service.GetProductsAsync(new CommerceHubProductsQuery { PageSize = 100 })).TotalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentStockReservationsAndWarehouseVisibility_DetermineMembershipAndSorting(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 28);
        f.Products[0].TrackStock = true;
        var warehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = f.TenantId, Name = "Fixture warehouse", IsActive = true };
        var stock = new ProductVariantStock { Id = Guid.NewGuid(), TenantId = f.TenantId, VariantId = f.Variants[0].Id,
            WarehouseId = warehouse.Id, Quantity = 7m, ReservedQuantity = 2m };
        f.Db.AddRange(warehouse, stock);
        await f.Db.SaveChangesAsync();
        var stocked = await f.Service.SearchAsync(new CommerceHubSearchQuery { Q = "authority", InStock = true, PageSize = 10 }, []);
        Assert.Equal(28, stocked.Total);
        Assert.Equal(f.Variants[0].Id, stocked.Items[0].VariantId);
        Assert.Equal(5m, stocked.Items[0].AvailableStock);
        stock.ReservedQuantity = 7m;
        await f.Db.SaveChangesAsync();
        Assert.Equal(27, (await f.Service.SearchAsync(new CommerceHubSearchQuery { InStock = true }, [])).Total);
        stock.ReservedQuantity = 0m;
        warehouse.IsActive = false;
        await f.Db.SaveChangesAsync();
        Assert.Equal(27, (await f.Service.SearchAsync(new CommerceHubSearchQuery { InStock = true }, [])).Total);
    }

    [Fact]
    public void PriceRolloutCapture_ExactlyMatchesExistingTenantAndCohortPolicy_AndIsNotMutableConfiguration()
    {
        var enabled = Guid.NewGuid();
        var disabled = Guid.NewGuid();
        try
        {
            CommerceHubPriceOverrideRuntimePolicy.Configure(new CommerceHubPriceOverrideRolloutOptions
            {
                Enabled = true, TenantAllowlist = [enabled, disabled], EnabledCohorts = ["pilot"],
                TenantCohorts = new Dictionary<Guid, string> { [enabled] = "pilot", [disabled] = "later" }
            });
            var captured = CommerceHubPriceOverrideRuntimePolicy.CaptureEnabledTenantIds();
            Assert.Equal(new[] { enabled }, captured);
            Assert.True(CommerceHubPriceOverrideRuntimePolicy.IsEnabledForTenant(enabled));
            Assert.False(CommerceHubPriceOverrideRuntimePolicy.IsEnabledForTenant(disabled));
            captured[0] = disabled;
            Assert.Equal(new[] { enabled }, CommerceHubPriceOverrideRuntimePolicy.CaptureEnabledTenantIds());
        }
        finally { CommerceHubPriceOverrideRuntimePolicy.Configure(new CommerceHubPriceOverrideRolloutOptions()); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PriceOverridePolicy_IsStableThroughExpansion_AndNextRequestHonorsKillSwitch(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 27);
        f.Listings[26].Update(f.CategoryId, 1m, actorUserId: Guid.NewGuid(), overrideReason: "Fixture approved override",
            expectedPriceOverrideMutationVersion: f.Listings[26].CommerceHubPriceOverrideMutationVersion);
        await f.Db.SaveChangesAsync();
        try
        {
            CommerceHubPriceOverrideRuntimePolicy.Configure(new CommerceHubPriceOverrideRolloutOptions { Enabled = true, TenantAllowlist = [f.TenantId] });
            f.Intelligence.Setup(service => service.ExpandQueryAsync("alias", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    CommerceHubPriceOverrideRuntimePolicy.Configure(new CommerceHubPriceOverrideRolloutOptions());
                    return new[] { "authority" };
                });
            var expanded = await f.Service.SearchAsync(new CommerceHubSearchQuery { Q = "alias", Sort = "price_asc", PageSize = 5 }, []);
            Assert.Equal(27, expanded.Total);
            Assert.Equal(f.Variants[26].Id, expanded.Items[0].VariantId);
            Assert.Equal(1m, expanded.Items[0].Price);
            Assert.Equal(1d, expanded.Facets.Price.Min);
            var subsequent = await f.Service.SearchAsync(new CommerceHubSearchQuery { Q = "authority", Sort = "price_asc", PageSize = 5 }, []);
            Assert.Equal(27, subsequent.Total);
            Assert.Equal(100m, subsequent.Items[0].Price);
            Assert.Equal(100d, subsequent.Facets.Price.Min);
        }
        finally { CommerceHubPriceOverrideRuntimePolicy.Configure(new CommerceHubPriceOverrideRolloutOptions()); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuggestionsAndAutocomplete_RejectCrossTenantCandidatesAndDerivativeCategoryCounts(bool real)
    {
        await using var f = await Fixture.CreateAsync(real, 27);
        var other = await f.AddVendorAsync("other suggestions");
        await f.AddListingAsync(other, 900m);
        var foreign = f.Listings[^1];
        f.SearchRead.Setup(service => service.GetSuggestionsAsync(It.IsAny<CommerceHubSearchSuggestionsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommerceHubSearchSuggestionsResult?)null);
        f.Repository.Setup(repository => repository.GetSuggestionsAsync(It.IsAny<CommerceHubSearchSuggestionsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommerceHubSearchSuggestionsResult
            {
                Products = [new CommerceHubSearchSuggestionProduct { Id = foreign.Id, ProductName = "poison other tenant" }]
            });
        var suggestions = await f.Service.GetSuggestionsAsync(new CommerceHubSearchSuggestionsQuery { Q = "authority", TenantId = f.TenantId });
        Assert.Empty(suggestions.Products);
        f.SearchRead.Setup(service => service.GetAutocompleteAsync(It.IsAny<CommerceHubSearchAutocompleteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommerceHubSearchAutocompleteResult
            {
                Products = [new CommerceHubSearchAutocompleteProduct { Id = foreign.Id, ProductName = "poison other tenant" }],
                Categories = [new CommerceHubSearchAutocompleteCategoryBucket { CategoryId = f.CategoryId, Count = 999999 }]
            });
        var autocomplete = await f.Service.GetAutocompleteAsync(new CommerceHubSearchAutocompleteQuery { Q = "authority", TenantId = f.TenantId });
        Assert.Empty(autocomplete.Products);
        Assert.Equal(27, Assert.Single(autocomplete.Categories).Count);
        f.Suspended.Add(f.TenantId);
        var hidden = await f.Service.GetAutocompleteAsync(new CommerceHubSearchAutocompleteQuery { Q = "authority", TenantId = f.TenantId });
        Assert.Empty(hidden.Products);
        Assert.Empty(hidden.Categories);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ApplicationDbContext Db { get; private init; } = null!;
        public MongoDbContext Mongo { get; private init; } = null!;
        public CommerceHubQueryService Service { get; private set; } = null!;
        public Guid TenantId { get; private set; }
        public Guid CategoryId { get; } = Guid.NewGuid();
        public Guid SecondCategoryId { get; } = Guid.NewGuid();
        public List<Product> Products { get; } = [];
        public List<ProductVariant> Variants { get; } = [];
        public List<CommerceHubListing> Listings { get; } = [];
        public HashSet<Guid> Suspended { get; } = [];
        public Mock<ICommerceHubReadRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<ICommerceHubSearchReadService> SearchRead { get; } = new(MockBehavior.Strict);
        public Mock<ICommerceHubSearchIntelligenceService> Intelligence { get; } = new();
        public Mock<ICommerceHubVendorFinancialControlService> Finance { get; } = new();
        private MemoryCache Cache { get; } = new(new MemoryCacheOptions());
        private bool Real { get; init; }
        private string DatabaseName { get; init; } = "";
        private QueryTrace Trace { get; init; } = new();

        private static async Task ExecuteSchemaDdlAsync(ApplicationDbContext db, string schema, bool create)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^app17b_[0-9a-f]{32}$"))
                throw new InvalidOperationException("Refusing a non-generated fixture schema identifier.");
            using var identifiers = new NpgsqlCommandBuilder();
            var quotedSchema = identifiers.QuoteIdentifier(schema);
            await db.Database.OpenConnectionAsync();
            try
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = create ? "CREATE SCHEMA " + quotedSchema : "DROP SCHEMA " + quotedSchema + " CASCADE";
                command.CommandTimeout = 60;
                await command.ExecuteNonQueryAsync();
            }
            finally { await db.Database.CloseConnectionAsync(); }
        }

        public static async Task<Fixture> CreateAsync(bool real, int count)
        {
            var name = "app17b_" + Guid.NewGuid().ToString("N");
            var options = new DbContextOptionsBuilder<ApplicationDbContext>();
            string mongoConnection;
            if (real)
            {
                var connection = Environment.GetEnvironmentVariable("APP17B_POSTGRES") ?? throw new InvalidOperationException("Disposable APP17B_POSTGRES fixture required");
                var cs = new NpgsqlConnectionStringBuilder(connection);
                if (cs.Database != "app17b_fixture" || cs.Host != "app17b-pg" || cs.Username != "app17b_fixture")
                    throw new InvalidOperationException("Refusing non-fixture PostgreSQL database");
                cs.SearchPath = name;
                cs.Pooling = false;
                options.UseNpgsql(cs.ConnectionString);
                mongoConnection = Environment.GetEnvironmentVariable("APP17B_MONGO") ?? throw new InvalidOperationException("Disposable APP17B_MONGO fixture required");
                var mongoUrl = new MongoUrl(mongoConnection);
                if (mongoUrl.Server.Host != "app17b-mongo" || mongoUrl.Username != "app17b_fixture")
                    throw new InvalidOperationException("Refusing non-fixture MongoDB endpoint");
            }
            else
            {
                options.UseInMemoryDatabase(name);
                mongoConnection = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100";
            }
            var trace = new QueryTrace();
            options.AddInterceptors(trace);
            var f = new Fixture
            {
                Db = new ApplicationDbContext(options.Options), Real = real, DatabaseName = name,
                Mongo = new MongoDbContext(Options.Create(new MongoDbSettings { ConnectionString = mongoConnection, DatabaseName = name }), NullLogger<MongoDbContext>.Instance),
                Trace = trace
            };
            if (real)
            {
                // Fixture-only isolated schema; never migrations, seeders or Program.
                await ExecuteSchemaDdlAsync(f.Db, name, create: true);
                await f.Db.Database.OpenConnectionAsync();
                await using (var create = f.Db.Database.GetDbConnection().CreateCommand())
                {
                    create.CommandText = f.Db.Database.GenerateCreateScript();
                    create.CommandTimeout = 60;
                    await create.ExecuteNonQueryAsync();
                }
                await f.Db.Database.CloseConnectionAsync();
                var version = await f.Mongo.Database.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
                Assert.Equal("9.0.4", version["version"].AsString);
                await f.Db.Database.OpenConnectionAsync();
                Assert.StartsWith("18.", f.Db.Database.GetDbConnection().ServerVersion);
                await f.Db.Database.CloseConnectionAsync();
            }
            f.Db.AddRange(new CommerceHubCategory { Id = f.CategoryId, Name = "Primary", Slug = "primary", IsActive = true },
                new CommerceHubCategory { Id = f.SecondCategoryId, Name = "Secondary", Slug = "secondary", IsActive = true });
            foreach (var category in new[] { f.CategoryId, f.SecondCategoryId })
            {
                f.Db.Add(new CommerceHubCategoryAttribute { Id = Guid.NewGuid(), CategoryId = category, Name = "brand", Type = "text", IsFilterable = true });
                f.Db.Add(new CommerceHubCategoryAttribute { Id = Guid.NewGuid(), CategoryId = category, Name = "size", Type = "number", IsFilterable = true });
            }
            f.TenantId = await f.AddVendorAsync("main");
            for (var index = 0; index < count; index++) await f.AddListingAsync(f.TenantId, 100m + index, index, save: false);
            await f.Db.SaveChangesAsync();
            f.Finance.Setup(service => service.GetVendorsSuspendedFromNewOrdersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<Guid> vendors, CancellationToken _) => vendors.Where(f.Suspended.Contains).ToArray());
            f.Intelligence.Setup(service => service.ExpandQueryAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
            var intent = new Mock<ICommerceHubSearchIntentDetector>();
            intent.Setup(service => service.DetectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(CommerceHubSearchIntentContext.Unknown());
            f.Service = new CommerceHubQueryService(f.Repository.Object, f.SearchRead.Object, f.Intelligence.Object, intent.Object,
                Mock.Of<ICommerceHubAutoHealingQueue>(), f.Mongo, f.Db, f.Finance.Object,
                Mock.Of<ICommerceHubGovernanceCacheInvalidationService>(), f.Cache, NullLogger<CommerceHubQueryService>.Instance);
            return f;
        }

        public async Task<Guid> AddVendorAsync(string suffix)
        {
            var id = Guid.NewGuid();
            Db.Add(new Tenant { Id = id, Name = "Vendor " + suffix, Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
            Db.Add(new UserGovernanceProfile { Id = Guid.NewGuid(), SubjectId = id, Role = UserGovernanceRole.Vendor,
                Status = UserGovernanceStatus.Active, IsKycVerified = true, IsMarketplaceApproved = true });
            await Db.SaveChangesAsync();
            return id;
        }

        public async Task AddListingAsync(Guid tenant, decimal price, int index = 0, bool save = true)
        {
            var category = new Category { Id = Guid.NewGuid(), TenantId = tenant, Name = "Source " + Guid.NewGuid().ToString("N") };
            var product = new Product { Id = Guid.NewGuid(), TenantId = tenant, CategoryId = category.Id,
                Name = "Authority " + index.ToString("D3"), Description = "SQL catalog", HasVariants = true, IsActive = true,
                IsOnlineVisible = true, IsSearchable = true, PublishStatus = PublishStatus.Published, TrackStock = false };
            var variant = new ProductVariant { Id = Guid.NewGuid(), TenantId = tenant, ProductId = product.Id, Price = price,
                OnlinePrice = price, IsActive = true, IsDefault = true, SKU = Guid.NewGuid().ToString("N") };
            var listing = CommerceHubListing.Create(tenant, product.Id, variant.Id, CategoryId, null,
                "{\"brand\":\"sql\",\"size\":" + index + "}");
            Db.AddRange(category, product, variant, listing);
            if (save) await Db.SaveChangesAsync();
            Products.Add(product); Variants.Add(variant); Listings.Add(listing);
        }

        public async Task<CommerceHubSearchReadResult> AuthorityAsync(CommerceHubSearchRequest request)
        {
            var method = typeof(CommerceHubQueryService).GetMethod("SearchSqlAuthorityFirstAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            return await (Task<CommerceHubSearchReadResult>)method.Invoke(Service, [request, CancellationToken.None, null])!;
        }

        public async Task RecordPlansAsync()
        {
            var directory = Environment.GetEnvironmentVariable("APP17B_EVIDENCE_DIR") ?? throw new InvalidOperationException("Native evidence directory required");
            Directory.CreateDirectory(directory);
            Assert.NotEmpty(Trace.Batches);
            var plans = new List<object>();
            await using var connection = new NpgsqlConnection(Db.Database.GetConnectionString());
            await connection.OpenAsync();
            foreach (var batch in Trace.Batches)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + batch.Sql;
                foreach (var parameter in batch.Parameters) command.Parameters.Add((NpgsqlParameter)parameter.Clone());
                var plan = (string)(await command.ExecuteScalarAsync())!;
                plans.Add(new { sql = batch.Sql, parameters = batch.Parameters.Select(parameter => new { parameter.ParameterName, value = parameter.Value }), plan = JsonSerializer.Deserialize<JsonElement>(plan) });
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "authority-query-plans.json"), JsonSerializer.Serialize(plans, new JsonSerializerOptions { WriteIndented = true }));
        }

        public async ValueTask DisposeAsync()
        {
            if (Real)
            {
                await Mongo.Database.Client.DropDatabaseAsync(DatabaseName);
                await ExecuteSchemaDdlAsync(Db, DatabaseName, create: false);
            }
            Cache.Dispose();
            await Db.DisposeAsync();
        }
    }

    private sealed class QueryTrace : DbCommandInterceptor
    {
        public List<(string Sql, NpgsqlParameter[] Parameters)> Batches { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Batches.Count < 3 && command.CommandText.Contains("CommerceHubListings", StringComparison.Ordinal) &&
                command.CommandText.Contains("OFFSET", StringComparison.Ordinal))
                Batches.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(parameter => (NpgsqlParameter)parameter.Clone()).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

[CollectionDefinition("CommerceHub authority discovery", DisableParallelization = true)]
public sealed class CommerceHubAuthorityDiscoveryCollection;
