using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using PointOfSale.API.CommerceHub.Services;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Financial;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Elasticsearch.Services;
using PointOfSale.Infrastructure.MongoDB.Documents;

namespace PointOfSale.API.Services.CommerceHub;

public partial class CommerceHubQueryService
{
    private async Task<IDbContextTransaction?> BeginAuthoritySnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational()) return null;
        if (_dbContext.Database.CurrentTransaction is { } current)
        {
            var isolation = current.GetDbTransaction().IsolationLevel;
            if (isolation is not (IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
                throw new InvalidOperationException("CommerceHub discovery requires a repeatable SQL authority snapshot.");
            return null;
        }

        // No SaveChanges or authority mutation occurs in discovery. Disposal
        // ends this read snapshot, including on cancellation or SQL failure.
        return await _dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
    }

    // SQL owns the ordered candidate stream. Batches bound catalog memory; a
    // complete scan (never a candidate cap) owns count and pre-page summaries.
    private async Task<CommerceHubSearchReadResult> SearchSqlAuthorityFirstAsync(
        CommerceHubSearchRequest request,
        CancellationToken cancellationToken,
        Guid[]? capturedOverrideTenantIds = null)
    {
        const int batchSize = 256;
        var requestedTenantId = request.TenantId;
        var requestedCategoryId = request.CategoryId;
        var excludedTenantIds = request.ExcludedTenantIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        var overrideTenantIds = capturedOverrideTenantIds ?? CommerceHubPriceOverrideRuntimePolicy.CaptureEnabledTenantIds();
        var categoryRows = await _dbContext.CommerceHubCategories.AsNoTracking()
            .Select(category => new AuthorityCategory(category.Id, category.Name, category.ParentId, category.IsActive))
            .ToListAsync(cancellationToken);
        var categoryPaths = BuildLiveCategoryPaths(categoryRows);
        var categoryNames = categoryRows.ToDictionary(category => category.Id, category => category.Name);
        var liveCategoryIds = categoryPaths.Keys.ToArray();
        var useBinaryCollation = _dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

        var candidates =
            from listing in _dbContext.CommerceHubListings.AsNoTracking().IgnoreQueryFilters()
            join product in _dbContext.Products.AsNoTracking().IgnoreQueryFilters()
                on new { listing.TenantId, listing.ProductId } equals new { product.TenantId, ProductId = product.Id }
            join variant in _dbContext.ProductVariants.AsNoTracking().IgnoreQueryFilters()
                on new { listing.TenantId, listing.VariantId, listing.ProductId }
                equals new { variant.TenantId, VariantId = variant.Id, variant.ProductId }
            join tenant in _dbContext.Tenants.AsNoTracking().IgnoreQueryFilters() on listing.TenantId equals tenant.Id
            where !listing.IsDeleted && listing.IsPublished && !product.IsDeleted && product.IsActive &&
                  product.IsOnlineVisible && product.IsSearchable && product.PublishStatus == PublishStatus.Published &&
                  !variant.IsDeleted && variant.IsActive &&
                  variant.SemanticLifecycleState == ProductVariantSemanticLifecycleState.Current &&
                  variant.SemanticSupersededAtUtc == null && variant.SemanticReplacementVariantId == null &&
                  tenant.IsActive && tenant.Status == TenantStatus.Active && tenant.DeletedAtUtc == null &&
                  tenant.Currency == FinancialCurrencyGuard.PlatformBaseCurrencyCode &&
                  _dbContext.UserGovernanceProfiles.AsNoTracking().Any(profile =>
                      profile.SubjectId == listing.TenantId && profile.Role == UserGovernanceRole.Vendor &&
                      profile.Status == UserGovernanceStatus.Active && profile.IsKycVerified && profile.IsMarketplaceApproved) &&
                  (!requestedTenantId.HasValue || listing.TenantId == requestedTenantId.Value) &&
                  !excludedTenantIds.Contains(listing.TenantId) &&
                  (!requestedCategoryId.HasValue || listing.CommerceHubCategoryId == requestedCategoryId.Value) &&
                  liveCategoryIds.Contains(listing.CommerceHubCategoryId)
            let available = (
                from stock in _dbContext.ProductVariantStocks.AsNoTracking().IgnoreQueryFilters()
                join warehouse in _dbContext.Warehouses.AsNoTracking().IgnoreQueryFilters()
                    on new { stock.TenantId, stock.WarehouseId } equals new { warehouse.TenantId, WarehouseId = warehouse.Id }
                where stock.TenantId == listing.TenantId && stock.VariantId == variant.Id &&
                      !stock.IsDeleted && !warehouse.IsDeleted && warehouse.IsActive
                select (decimal?)(stock.Quantity - stock.ReservedQuantity)).Sum() ?? 0m
            let basePrice = variant.OnlinePrice.GetValueOrDefault() > 0m ? variant.OnlinePrice.GetValueOrDefault() : variant.Price
            let overridePrice = overrideTenantIds.Contains(listing.TenantId) && listing.CommerceHubPrice.GetValueOrDefault() > 0m
                ? listing.CommerceHubPrice : null
            select new
            {
                listing.Id, listing.TenantId, StoreName = tenant.Name, listing.ProductId, listing.VariantId,
                ProductName = product.Name, product.Description, Sku = variant.SKU,
                CategoryId = listing.CommerceHubCategoryId, Price = overridePrice ?? basePrice, OverridePrice = overridePrice,
                AvailableStock = available > 0m ? available : 0m,
                InStock = !product.TrackStock || available > 0m || product.AllowBackorder,
                listing.AttributesJson, listing.CreatedAt, listing.UpdatedAt,
                SortName = useBinaryCollation ? EF.Functions.Collate(product.Name, "C") : product.Name
            };

        candidates = candidates.Where(item =>
            (!request.InStock.HasValue || item.InStock == request.InStock.Value) &&
            (!request.MinPrice.HasValue || item.Price >= request.MinPrice.Value) &&
            (!request.MaxPrice.HasValue || item.Price <= request.MaxPrice.Value));

        // Preserve active sort keys; a unique final key stabilizes ties.
        var ordered = request.Sort switch
        {
            "price_asc" => candidates.OrderBy(item => item.Price).ThenByDescending(item => item.InStock)
                .ThenByDescending(item => item.AvailableStock).ThenBy(item => item.SortName).ThenBy(item => item.Id),
            "price_desc" => candidates.OrderByDescending(item => item.Price).ThenByDescending(item => item.InStock)
                .ThenByDescending(item => item.AvailableStock).ThenBy(item => item.SortName).ThenBy(item => item.Id),
            "newest" => candidates.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.InStock)
                .ThenByDescending(item => item.AvailableStock).ThenBy(item => item.Id),
            _ => string.IsNullOrWhiteSpace(request.Query)
                ? candidates.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.InStock)
                    .ThenByDescending(item => item.AvailableStock).ThenBy(item => item.Id)
                : candidates.OrderByDescending(item => item.InStock).ThenByDescending(item => item.AvailableStock)
                    .ThenBy(item => item.SortName).ThenBy(item => item.Id)
        };

        var page = request.Page <= 0 ? 1 : request.Page;
        var pageSize = request.PageSize <= 0 ? 20 : Math.Min(request.PageSize, 100);
        var skip = (long)(page - 1) * pageSize;
        var pageItems = new List<CommerceHubProductDocument>(pageSize);
        var categoryCounts = new Dictionary<Guid, long>();
        long totalCount = 0;
        double priceSum = 0;
        double? minPrice = null;
        double? maxPrice = null;
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await ordered.Skip(offset).Take(batchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0) break;
            var vendorIds = batch.Select(item => item.TenantId).Distinct().ToArray();
            // No TTL cache may admit a newly financially suppressed vendor.
            var suspended = (await _vendorFinancialControlService.GetVendorsSuspendedFromNewOrdersAsync(vendorIds, cancellationToken)).ToHashSet();
            var categoryIds = batch.Select(item => item.CategoryId).Distinct().ToArray();
            var schemaRows = await _dbContext.CommerceHubCategoryAttributes.AsNoTracking()
                .Where(attribute => categoryIds.Contains(attribute.CategoryId))
                .Select(attribute => new { attribute.CategoryId, attribute.Name }).ToListAsync(cancellationToken);
            var schema = schemaRows.GroupBy(attribute => attribute.CategoryId)
                .ToDictionary(group => group.Key, group => group.Select(attribute => CommerceHubAttributeService.NormalizeKey(attribute.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase));

            foreach (var item in batch)
            {
                if (suspended.Contains(item.TenantId)) continue;
                var attributes = ParseAuthorityAttributes(item.AttributesJson);
                var normalizedAttributes = new BsonDocument();
                if (attributes != null && schema.TryGetValue(item.CategoryId, out var keys))
                {
                    foreach (var attribute in attributes)
                    {
                        var key = CommerceHubAttributeService.NormalizeKey(attribute.Name);
                        if (keys.Contains(key) && (attribute.Value.IsNumeric || attribute.Value.IsString || attribute.Value.IsBoolean))
                            normalizedAttributes[key] = attribute.Value;
                    }
                }
                var document = new CommerceHubProductDocument
                {
                    Id = item.Id, TenantId = item.TenantId, StoreName = item.StoreName?.Trim() ?? string.Empty,
                    ProductId = item.ProductId, VariantId = item.VariantId, ProductName = item.ProductName,
                    Description = item.Description, Price = item.Price, CommerceHubPriceOverride = item.OverridePrice,
                    OverridePresent = item.OverridePrice.HasValue,
                    PriceSource = item.OverridePrice.HasValue ? CommerceHubPriceSource.CommerceHubOverride.ToString() : CommerceHubPriceSource.ProductVariantEffectiveOnlinePrice.ToString(),
                    CategoryId = item.CategoryId, CategoryName = categoryNames[item.CategoryId],
                    CategoryPath = categoryPaths[item.CategoryId], Currency = FinancialCurrencyGuard.PlatformBaseCurrencyCode,
                    AvailableStock = item.AvailableStock, InStock = item.InStock, IsPublished = true,
                    Attributes = normalizedAttributes, AttributesJson = normalizedAttributes.ToJson(),
                    CreatedAtUtc = item.CreatedAt, UpdatedAtUtc = item.UpdatedAt ?? item.CreatedAt
                };
                if (!MatchesAuthorityFilters(document, item.Sku, request)) continue;
                if (totalCount >= skip && pageItems.Count < pageSize) pageItems.Add(document);
                totalCount = checked(totalCount + 1);
                categoryCounts[document.CategoryId] = categoryCounts.GetValueOrDefault(document.CategoryId) + 1;
                var price = Convert.ToDouble(document.Price);
                priceSum += price;
                minPrice = minPrice.HasValue ? Math.Min(minPrice.Value, price) : price;
                maxPrice = maxPrice.HasValue ? Math.Max(maxPrice.Value, price) : price;
            }
            if (batch.Count < batchSize) break;
            // Overflow fails closed, never returns a silently truncated total.
            offset = checked(offset + batchSize);
        }

        await HydrateBoundedAuthorityPageAsync(pageItems, cancellationToken);
        return new CommerceHubSearchReadResult
        {
            Items = pageItems,
            TotalCount = totalCount,
            CategoryBuckets = categoryCounts.Select(pair => new CommerceHubSearchCategoryBucket { CategoryId = pair.Key, Count = pair.Value }).ToList(),
            PriceStats = totalCount == 0 ? null : new CommerceHubSearchStatsResult
            {
                Min = minPrice, Max = maxPrice, Avg = priceSum / totalCount, Count = totalCount
            }
        };
    }

    private async Task HydrateBoundedAuthorityPageAsync(
        IReadOnlyCollection<CommerceHubProductDocument> pageItems, CancellationToken cancellationToken)
    {
        if (pageItems.Count == 0) return;
        var ids = pageItems.Select(item => item.Id).ToArray();
        var metadata = await _dbContext.CommerceHubListings.AsNoTracking().IgnoreQueryFilters()
            .Where(listing => ids.Contains(listing.Id))
            .Select(listing => new
            {
                listing.Id,
                Image = (from variant in _dbContext.ProductVariants.AsNoTracking().IgnoreQueryFilters()
                         join media in _dbContext.ProductMedia.AsNoTracking().IgnoreQueryFilters() on variant.MediaId equals (Guid?)media.Id
                         where variant.Id == listing.VariantId && variant.TenantId == listing.TenantId &&
                               variant.ProductId == listing.ProductId && media.TenantId == listing.TenantId &&
                               media.ProductId == listing.ProductId && media.Url.Trim() != ""
                         select media.Url).FirstOrDefault() ??
                        _dbContext.ProductMedia.AsNoTracking().IgnoreQueryFilters()
                            .Where(media => media.TenantId == listing.TenantId && media.ProductId == listing.ProductId &&
                                            media.MediaType.ToLower() == "image" && media.Url.Trim() != "")
                            .OrderBy(media => media.Position).ThenBy(media => media.CreatedAtUtc).ThenBy(media => media.Id)
                            .Select(media => media.Url).FirstOrDefault(),
                ShippingFee = _dbContext.ShippingMethods.AsNoTracking().IgnoreQueryFilters()
                    .Where(method => method.TenantId == listing.TenantId && method.IsActive && !method.IsDeleted &&
                                     (method.FreeShippingThreshold == null || method.BaseRate > 0m))
                    .OrderBy(method => method.SortOrder).ThenBy(method => method.Name).ThenBy(method => method.Id)
                    .Select(method => (decimal?)method.BaseRate).FirstOrDefault() ?? 0m
            }).ToDictionaryAsync(item => item.Id, cancellationToken);
        foreach (var item in pageItems)
        {
            if (!metadata.TryGetValue(item.Id, out var current))
                throw new InvalidOperationException("CommerceHub authority page changed outside its SQL snapshot.");
            item.Image = current.Image;
            item.StoreShippingFee = Math.Max(0m, current.ShippingFee);
        }
    }
}
