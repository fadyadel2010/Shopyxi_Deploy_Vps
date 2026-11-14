using System.Text.RegularExpressions;

namespace PointOfSale.Tests.Architecture;

public sealed class CrossSystemAuthorityRegressionGuardTests
{
    private static readonly Regex DirectSqlMutationCall = new(
        @"\.(ExecuteUpdateAsync|ExecuteDeleteAsync|ExecuteSqlAsync|ExecuteSqlRawAsync|ExecuteSqlInterpolatedAsync)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex RawMassTransitPublisherReference = new(
        @"\b(IPublishEndpoint|IBus|ISendEndpointProvider)\b|(?:\b\w*(?:publishEndpoint|bus|sendEndpointProvider)\w*\s*\.\s*Publish\s*(?:<[^>]+>)?\s*\()",
        RegexOptions.Compiled);

    private static readonly Regex DirectAuthoritativePublishCall = new(
        @"\b\w+\s*\.\s*Publish\s*(?:<[^>]+>)?\s*\(\s*(?:new\s+)?\w*?(Order|Payment|Inventory|Stock|CommerceHub|Marketplace|Affiliate|Refund|Payout|Settlement|Ledger|Accounting|Currency|ProviderTrust)\w*Event\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ApprovedDirectSqlMutationFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "PointOfSale.API/Controllers/ExpensesController.cs",
        "PointOfSale.API/Controllers/OrdersController.cs",
        "PointOfSale.API/Controllers/ProductsController.cs",
        "PointOfSale.API/Controllers/ProductVariantsController.cs",
        "PointOfSale.API/Controllers/PurchaseReturnsController.cs",
        "PointOfSale.API/Controllers/PurchasesController.cs",
        "PointOfSale.API/Controllers/SalesController.cs",
        "PointOfSale.API/Controllers/SalesReturnsController.cs",
        "PointOfSale.API/Controllers/StockTransfersController.cs",
        "PointOfSale.API/Controllers/StorefrontAccountController.cs",
        "PointOfSale.API/Controllers/TestAuthController.cs",
        "PointOfSale.API/Services/CommerceHub/CommerceHubCustomerAuthService.cs",
        "PointOfSale.API/Controllers/WarehousesController.cs",
        "PointOfSale.API/Services/Checkout/CheckoutRequestIdempotencyCoordinator.cs",
        "PointOfSale.API/Services/CommerceHub/CommerceHubFinanceReconciliationJob.cs",
        "PointOfSale.API/Services/CommerceHub/CommerceHubPaymentCompensationOutboxWorker.cs",
        "PointOfSale.API/Services/CommerceHub/CommerceHubPaymentService.cs",
        "PointOfSale.API/Services/CommerceHub/CommerceHubPayoutService.cs",
        "PointOfSale.API/Services/CommerceHub/CommerceHubProjectionReconciliationService.cs",
        "PointOfSale.API/Services/CommerceHub/CommerceHubRefundService.cs",
        "PointOfSale.API/Services/CommerceHub/CustomerTokenRevocationService.cs",
        "PointOfSale.API/Services/FinancialMutationIdempotencyService.cs",
        "PointOfSale.API/Services/Inventory/ProductVariantStockMutationSupport.cs",
        "PointOfSale.API/Services/Payments/PaymentService.cs",
        "PointOfSale.Infrastructure/Accounting/AccountingOutboxProcessor.cs",
        "PointOfSale.Infrastructure/Analytics/ProductPixelOverrideService.cs",
        "PointOfSale.Infrastructure/Data/Repositories/ProductVariantStockRepository.cs",
        "PointOfSale.Infrastructure/Discounts/CouponValidationService.cs",
        "PointOfSale.Infrastructure/Eventing/MarketplaceEventBackboneCleanupWorker.cs",
        "PointOfSale.Infrastructure/Eventing/MarketplaceEventOutboxProcessor.cs",
        "PointOfSale.Infrastructure/Eventing/StockSyncOutboxProcessor.cs",
        "PointOfSale.Infrastructure/Jobs/CommerceHubCartCleanupJob.cs",
        "PointOfSale.Infrastructure/Jobs/OrderRequestIdempotencyCleanupJob.cs",
        "PointOfSale.Infrastructure/Jobs/OutboxPublisherJob.cs",
        "PointOfSale.Infrastructure/Jobs/ProcessedTrustEventsCleanupJob.cs",
        "PointOfSale.Infrastructure/Payment/PaymentCancellationOutboxProcessor.cs",
        "PointOfSale.Infrastructure/Payments/PaymentLifecycleService.cs",
        "PointOfSale.Infrastructure/Reliability/ProjectionEventFence.cs",
        "PointOfSale.Infrastructure/Services/AbandonedCartService.cs",
        "PointOfSale.Infrastructure/Services/Affiliate/AffiliatePayoutService.cs",
        "PointOfSale.Infrastructure/Services/CheckoutSessionService.cs",
        "PointOfSale.Infrastructure/Services/WhatsAppRateLimitService.cs",
        "PointOfSale.Infrastructure/Shipping/CarrierShipmentService.cs",
    };

    private static readonly HashSet<string> ApprovedRawMassTransitPublisherFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "PointOfSale.Infrastructure/EventBus/MassTransitEventPublisher.cs",
        "PointOfSale.Infrastructure/Eventing/MarketplaceEventOutboxProcessor.cs",
        "PointOfSale.Infrastructure/Eventing/StockSyncOutboxProcessor.cs",
        "PointOfSale.Infrastructure/Jobs/OutboxPublisherJob.cs",
    };

    private static readonly HashSet<string> ApprovedHighRiskReplaceOneFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "PointOfSale.API/Development/CommerceHub/DevelopmentCommerceHubSeedService.cs",
        "PointOfSale.API/Services/ReadModels/DiscountReadModelRepairService.cs",
        "PointOfSale.Infrastructure/Consumers/Affiliate/AffiliateProductPolicyProjectionConsumer.cs",
        "PointOfSale.Infrastructure/Consumers/CommerceHubProjectionConsumer.cs",
        "PointOfSale.Infrastructure/Consumers/CmsPageProjectionConsumer.cs",
        "PointOfSale.Infrastructure/Consumers/CouponProjectionConsumer.cs",
        "PointOfSale.Infrastructure/Consumers/PaymentMethodProjectionConsumer.cs",
        "PointOfSale.Infrastructure/Consumers/PromotionProjectionConsumer.cs",
        "PointOfSale.Infrastructure/Consumers/NavigationProjectionConsumer.cs",
        "PointOfSale.Infrastructure/Consumers/StorefrontConfigProjectionConsumer.cs",
        "PointOfSale.Infrastructure/Consumers/StorefrontFooterProjectionConsumer.cs",
        "PointOfSale.Infrastructure/MongoDB/Services/CouponReadRepository.cs",
        "PointOfSale.Infrastructure/MongoDB/Services/PaymentMethodReadRepository.cs",
        "PointOfSale.Infrastructure/MongoDB/Services/ProductReadRepository.cs",
        "PointOfSale.Infrastructure/MongoDB/Services/ShippingMethodReadRepository.cs",
    };

    private static readonly HashSet<string> ApprovedRawElasticClientWriteFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "PointOfSale.Infrastructure/Consumers/Affiliate/AffiliateProductPolicyProjectionConsumer.cs",
        "PointOfSale.Infrastructure/Consumers/ServiceDiscoveryProjectionConsumer.cs",
        "PointOfSale.Infrastructure/Elasticsearch/Services/CommerceHubSearchIndexService.cs",
        "PointOfSale.Infrastructure/Elasticsearch/Services/ProductSearchIndexService.cs",
        "PointOfSale.Infrastructure/Elasticsearch/Services/UnifiedSearchIndexService.cs",
        "PointOfSale.Infrastructure/Jobs/ElasticsearchDriftReconciliationJob.cs",
        "PointOfSale.Infrastructure/Scripts/AF16E_ReindexAffiliateProductsWithStorefrontUrl.cs",
    };

    private static readonly HashSet<string> ApprovedPatternInvalidationFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "PointOfSale.API/Consumers/RedisInvalidationConsumer.cs",
        "PointOfSale.Infrastructure/Consumers/ProductCacheInvalidationConsumer.cs",
        "PointOfSale.Infrastructure/Redis/Services/RedisJsonCacheService.cs",
    };

    [Fact]
    public void DirectSqlAndBulkMutationBypasses_MustRemainExplicitlyApproved()
    {
        var solutionRoot = DiscoverSolutionRoot(AppContext.BaseDirectory);
        var taxonomySource = File.ReadAllText(Path.Combine(solutionRoot, ReviewedTaxonomySeederSqlBoundary.Path));
        var taxonomyCatalog = File.ReadAllText(Path.Combine(solutionRoot, ReviewedTaxonomySeederSqlBoundary.CatalogPath));
        Assert.True(ReviewedTaxonomySeederSqlBoundary.IsApproved(taxonomySource, taxonomyCatalog),
            "SQL14 approval is limited to the exact reviewed insert-only transaction and fixed catalog; changes require individual re-review/provider proof.");
        // Exercise refusal in this original guard, without removing its identity
        // or any other offender. These are architecture controls, not PG tests.
        foreach (var changed in new[]
        {
            taxonomySource.Replace("ON CONFLICT DO NOTHING", "ON CONFLICT DO UPDATE SET \"IsActive\" = TRUE", StringComparison.Ordinal),
            taxonomySource.Replace("{entry.Id}", "{Guid.NewGuid()}", StringComparison.Ordinal),
            taxonomySource.Replace("await EnsureBaselineIdentityAsync(entries, cancellationToken);", "", StringComparison.Ordinal),
            taxonomySource.Replace("await transaction.CommitAsync(cancellationToken);", "await _dbContext.AffiliateCategories.ExecuteDeleteAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);", StringComparison.Ordinal),
            taxonomySource.Replace(".BeginTransactionAsync(cancellationToken)", ".BeginTransactionAsync()", StringComparison.Ordinal)
        }) Assert.False(ReviewedTaxonomySeederSqlBoundary.IsApproved(changed, taxonomyCatalog));
        Assert.False(ReviewedTaxonomySeederSqlBoundary.IsApproved(taxonomySource, taxonomyCatalog + "\n// unreviewed catalog change\n"));
        var readinessSource = File.ReadAllText(Path.Combine(solutionRoot, ReviewedShippingReadinessSqlBoundary.Path));
        Assert.True(ReviewedShippingReadinessSqlBoundary.IsReadOnlyDatabaseBoundary(readinessSource),
            "SQL11 classification covers only the reviewed rollback-only read-only database core, not application startup approval.");
        foreach (var changed in new[]
        {
            readinessSource.Replace("SET TRANSACTION READ ONLY", "SET TRANSACTION READ ONLY; DELETE FROM \"ShippingWebhookClaims\"", StringComparison.Ordinal),
            readinessSource.Replace("\"SET TRANSACTION READ ONLY\"", "sqlFromCaller", StringComparison.Ordinal),
            readinessSource.Replace("await transaction.RollbackAsync(cleanup.Token);", "await transaction.CommitAsync(cleanup.Token);", StringComparison.Ordinal),
            readinessSource.Replace("await transaction.RollbackAsync(cleanup.Token);", "", StringComparison.Ordinal),
            readinessSource.Replace(".AsNoTracking()", "", StringComparison.Ordinal),
            readinessSource.Replace("transaction.RollbackAsync(cleanup.Token)", "transaction.RollbackAsync(probe.Token)", StringComparison.Ordinal)
        }) Assert.False(ReviewedShippingReadinessSqlBoundary.IsReadOnlyDatabaseBoundary(changed));
        var lifecycleSource = File.ReadAllText(Path.Combine(solutionRoot, ReviewedAffiliateLifecycleSqlBoundary.Path));
        Assert.True(ReviewedAffiliateLifecycleSqlBoundary.ContainsOnlyReviewedLifecycleSql(lifecycleSource),
            "Affiliate lifecycle classification is limited to the exact two reviewed parameterized lock statements and their transactional callers, not the financial service.");
        foreach (var changed in new[]
        {
            lifecycleSource.Replace("FOR UPDATE", "", StringComparison.Ordinal),
            lifecycleSource.Replace("{userId}", "{affiliateAccountId}", StringComparison.Ordinal),
            lifecycleSource.Replace("await _context.Database.ExecuteSqlInterpolatedAsync(", "await _context.Database.ExecuteSqlRawAsync(", StringComparison.Ordinal),
            lifecycleSource.Replace("await using var transaction = await _context.Database.BeginTransactionAsync(ct);", "", StringComparison.Ordinal),
            lifecycleSource.Replace("await _context.Entry(account).ReloadAsync(ct);", "", StringComparison.Ordinal),
            lifecycleSource.Replace("await _context.Entry(user).ReloadAsync(ct);", "", StringComparison.Ordinal),
            lifecycleSource.Replace("if (!userIds.SequenceEqual(currentIds))", "if (false)", StringComparison.Ordinal),
            lifecycleSource.Replace("if (user.TokensValidAfterUtc is { } cutoff && cutoff > nowUtc) nowUtc = cutoff;", "", StringComparison.Ordinal),
            lifecycleSource.Replace("await _context.SaveChangesAsync(ct);", "await _context.AffiliateUsers.ExecuteDeleteAsync(ct); await _context.SaveChangesAsync(ct);", StringComparison.Ordinal)
        }) Assert.False(ReviewedAffiliateLifecycleSqlBoundary.ContainsOnlyReviewedLifecycleSql(changed));
        var slugSource = File.ReadAllText(Path.Combine(solutionRoot, ReviewedAuthoringSlugSqlBoundary.Path));
        Assert.True(ReviewedAuthoringSlugSqlBoundary.ContainsOnlyReviewedTenantAdvisorySql(slugSource),
            "SQL10 classification covers only the exact tenant advisory transaction and its reviewed callers, not every authoring business or delivery path.");
        foreach (var changed in new[]
        {
            slugSource.Replace("SELECT pg_advisory_xact_lock({0});", "SELECT pg_advisory_lock({0});", StringComparison.Ordinal),
            slugSource.Replace("SELECT pg_advisory_xact_lock({0});", "DELETE FROM Products; SELECT pg_advisory_xact_lock({0});", StringComparison.Ordinal),
            slugSource.Replace("new object[] { lockKey }", "new object[] { tenantId }", StringComparison.Ordinal),
            slugSource.Replace("product-slug:{tenantId:N}", "product-slug:global", StringComparison.Ordinal),
            slugSource.Replace("await transaction.DisposeAsync();", "", StringComparison.Ordinal),
            slugSource.Replace("await using var slugTransaction", "var slugTransaction", StringComparison.Ordinal),
            slugSource + "\n// unreviewed helper/control-flow edit requires re-review\n"
        }) Assert.False(ReviewedAuthoringSlugSqlBoundary.ContainsOnlyReviewedTenantAdvisorySql(changed));
        var sourceRoots = new[]
        {
            Path.Combine(solutionRoot, "PointOfSale.API"),
            Path.Combine(solutionRoot, "PointOfSale.Infrastructure"),
            Path.Combine(solutionRoot, "PointOfSale.Core")
        };

        // Exact versioned statement/source/caller-context approvals only. The
        // original matcher, classifier, restricted contracts and final assertion
        // remain intact; unknown or changed SQL/control flow is still an offender.
        var reviewedSqlContext = ReviewedSqlGovernanceBoundaries.ReadProductionContext(solutionRoot);
        ReviewedSqlGovernanceBoundaries.AssertRefusalControls(solutionRoot, reviewedSqlContext);
        var offenders = sourceRoots
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                Path = path,
                RelativePath = NormalizeRelativePath(solutionRoot, path),
                Source = File.ReadAllText(path)
            })
            .Where(file => DirectSqlMutationCall.IsMatch(file.Source))
            .Where(file => SqlExecutionBoundary.RequiresMutationGovernance(file.Source))
            .Where(file => !ApprovedDirectSqlMutationFiles.Contains(file.RelativePath))
            .Where(file => file.RelativePath != ReviewedTaxonomySeederSqlBoundary.Path ||
                !ReviewedTaxonomySeederSqlBoundary.IsApproved(file.Source, taxonomyCatalog))
            .Where(file => file.RelativePath != ReviewedShippingReadinessSqlBoundary.Path ||
                !ReviewedShippingReadinessSqlBoundary.IsReadOnlyDatabaseBoundary(file.Source))
            .Where(file => file.RelativePath != ReviewedAffiliateLifecycleSqlBoundary.Path ||
                !ReviewedAffiliateLifecycleSqlBoundary.ContainsOnlyReviewedLifecycleSql(file.Source))
            .Where(file => file.RelativePath != ReviewedAuthoringSlugSqlBoundary.Path ||
                !ReviewedAuthoringSlugSqlBoundary.ContainsOnlyReviewedTenantAdvisorySql(file.Source))
            .Where(file => !ReviewedSqlGovernanceBoundaries.IsApproved(file.RelativePath, file.Source, reviewedSqlContext))
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "New direct SQL/bulk mutation paths bypass SaveChanges authority guards. " +
            "Move the write behind an existing authority service or add an explicit architecture-test approval after reviewing tenant scope, transaction boundary, lifecycle guards, and outbox/reconciliation behavior. Offenders: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void AuthoritativeEvents_MustNotBePublishedDirectlyFromMassTransitConsumeContext()
    {
        var solutionRoot = DiscoverSolutionRoot(AppContext.BaseDirectory);
        var sourceRoots = new[]
        {
            Path.Combine(solutionRoot, "PointOfSale.API"),
            Path.Combine(solutionRoot, "PointOfSale.Infrastructure")
        };

        var offenders = sourceRoots
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                Path = path,
                RelativePath = NormalizeRelativePath(solutionRoot, path),
                Source = StripCommentsAndStringLiterals(File.ReadAllText(path))
            })
            .Where(file => DirectAuthoritativePublishCall.IsMatch(file.Source))
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Authoritative events must go through IEventPublisher so tenant scope, aggregate versioning, transaction boundary, and affected-tenant checks are enforced before the authority commit. Offenders: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void ApplicationCode_MustNotInjectOrUseRawMassTransitPublishers()
    {
        var solutionRoot = DiscoverSolutionRoot(AppContext.BaseDirectory);
        var sourceRoots = new[]
        {
            Path.Combine(solutionRoot, "PointOfSale.API"),
            Path.Combine(solutionRoot, "PointOfSale.Infrastructure")
        };

        var offenders = sourceRoots
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                RelativePath = NormalizeRelativePath(solutionRoot, path),
                Source = StripCommentsAndStringLiterals(File.ReadAllText(path))
            })
            .Where(file => RawMassTransitPublisherReference.IsMatch(file.Source))
            .Where(file => !ApprovedRawMassTransitPublisherFiles.Contains(file.RelativePath))
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Application/business code must publish integration events through IEventPublisher. " +
            "Raw IPublishEndpoint, IBus, ISendEndpointProvider, or direct Publish(...) calls are allowed only in reviewed outbox/event-bus infrastructure. Offenders: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void ApprovedDirectSqlBypasses_MustStayNarrowAndDocumentedByReviewedPatterns()
    {
        var solutionRoot = DiscoverSolutionRoot(AppContext.BaseDirectory);

        ExtractedAuthorityCallPaths.AssertProductIdempotencyDelegation();

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.API/Controllers/SalesReturnsController.cs",
            "SalesRequestIdempotency",
            "ON CONFLICT",
            "WITH (UPDLOCK, HOLDLOCK)");

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.API/Services/CommerceHub/CommerceHubFinanceReconciliationJob.cs",
            "LockCommerceHubPaymentForRecoveryAsync",
            "FOR UPDATE",
            "WITH (UPDLOCK, ROWLOCK, HOLDLOCK)");

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.Infrastructure/Reliability/ProjectionEventFence.cs",
            "pg_try_advisory_lock",
            "pg_advisory_unlock",
            "ProcessedEvents");

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.Infrastructure/Payment/PaymentCancellationOutboxProcessor.cs",
            "BeginTransactionAsync",
            "SELECT pg_advisory_xact_lock",
            "ComputeStableAdvisoryLockKey(message.TenantId, message.OrderId)",
            "message.TryClaim(_options.ProcessingLeaseSeconds)",
            "PaymentCancellationClaimOutcome.ConcurrencyConflict");

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.API/Controllers/TestAuthController.cs",
            "Belt-and-suspenders idempotent schema fixes",
            "ALTER TABLE",
            "CREATE UNIQUE INDEX IF NOT EXISTS");
    }

    [Fact]
    public void HighRiskReplaceOneWrites_MustRemainExplicitlyApproved()
    {
        var solutionRoot = DiscoverSolutionRoot(AppContext.BaseDirectory);
        var sourceRoots = new[]
        {
            Path.Combine(solutionRoot, "PointOfSale.API"),
            Path.Combine(solutionRoot, "PointOfSale.Infrastructure")
        };

        var highRiskDocumentMarkers = new[]
        {
            new Regex(@"\bProductDocument\b", RegexOptions.Compiled),
            new Regex(@"\bPublicAffiliateProductDocument\b", RegexOptions.Compiled),
            new Regex(@"\bPublicAffiliateStoreDocument\b", RegexOptions.Compiled),
            new Regex(@"\bStorefrontConfigDocument\b", RegexOptions.Compiled),
            new Regex(@"\bStorefrontFooterDocument\b", RegexOptions.Compiled),
            new Regex(@"\bPaymentMethodDocument\b", RegexOptions.Compiled),
            new Regex(@"\bShippingMethodDocument\b", RegexOptions.Compiled),
            new Regex(@"\bCouponDocument\b", RegexOptions.Compiled),
            new Regex(@"\bPromotionDocument\b", RegexOptions.Compiled),
            new Regex(@"\bStorefrontNavigationDocument\b", RegexOptions.Compiled),
            new Regex(@"\bStorefrontPageDocument\b", RegexOptions.Compiled)
        };

        var offenders = sourceRoots
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                RelativePath = NormalizeRelativePath(solutionRoot, path),
                Source = File.ReadAllText(path)
            })
            .Where(file => file.Source.Contains("ReplaceOneAsync(", StringComparison.Ordinal))
            .Where(file =>
                highRiskDocumentMarkers.Any(marker => marker.IsMatch(file.Source)) ||
                file.Source.Contains("CommerceHubProducts.ReplaceOneAsync(", StringComparison.Ordinal))
            .Where(file => !ApprovedHighRiskReplaceOneFiles.Contains(file.RelativePath))
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "High-risk full-document Mongo replacement is only allowed on explicitly reviewed Tier-1 projection files. " +
            "Move the write behind an existing repository/writer or add an explicit allowlist entry with dedicated authority/freshness assertions. Offenders: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void ApprovedDerivativeMutationPrimitives_MustRetainRuntimeGovernanceMarkers()
    {
        var solutionRoot = DiscoverSolutionRoot(AppContext.BaseDirectory);
        ExtractedAuthorityCallPaths.AssertMongoOnlyStorefrontConsumers();

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.Infrastructure/Consumers/StorefrontFooterProjectionConsumer.cs",
            "ProjectionEventFence.TryAcquireAsync",
            "DerivativeProjectionMutationGuard.RequireReviewedMutation",
            "DangerousProjectionPrimitive.MongoFullDocumentReplace",
            "SqlAuthorityOverlayKind.StorefrontFooter",
            "BuildFreshFooterEventFilter",
            "fence.CompleteAsync");

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.Infrastructure/Consumers/StorefrontConfigProjectionConsumer.cs",
            "ProjectionEventFence.TryAcquireAsync",
            "RequireReviewedStoreConfigMutation",
            "DerivativeProjectionMutationGuard.RequireReviewedMutation",
            "DangerousProjectionPrimitive.MongoFullDocumentReplace",
            "SqlAuthorityOverlayKind.StorefrontConfig",
            "SqlAuthorityOverlayKind.Checkout",
            "SqlAuthorityOverlayKind.StoreMetadata",
            "BuildFreshStoreConfigEventFilter",
            "fence.CompleteAsync");

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.Infrastructure/Consumers/NavigationProjectionConsumer.cs",
            "ProjectionEventFence.TryAcquireAsync",
            "TenantLineageAuthority.LoadCurrentStoreConfigProjectionAuthorityAsync",
            "FailClosedNavigationProjectionAsync",
            "DerivativeProjectionMutationGuard.RequireReviewedMutation",
            "DangerousProjectionPrimitive.MongoFullDocumentReplace",
            "SqlAuthorityOverlayKind.StorefrontNavigation",
            "BuildFreshNavigationEventFilter",
            "filter.Ne(document => document.LastEventId, eventId)",
            "fence.CompleteAsync");

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.Infrastructure/Consumers/CmsPageProjectionConsumer.cs",
            "ProjectionEventFence.TryAcquireAsync",
            "TenantLineageAuthority.IsActiveTenantAsync",
            "FailClosedCmsPageProjectionAsync",
            "DerivativeProjectionMutationGuard.RequireReviewedMutation",
            "DangerousProjectionPrimitive.MongoFullDocumentReplace",
            "SqlAuthorityOverlayKind.StorefrontCms",
            "BuildFreshPageEventFilter",
            "filter.Ne(item => item.LastEventId, eventId)",
            "fence.CompleteAsync");

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.Infrastructure/Reliability/DerivativeProjectionMutationGuard.cs",
            "DERIVATIVE_MUTATION_SQL_AUTHORITY_REQUIRED",
            "DERIVATIVE_MUTATION_FRESHNESS_GUARD_REQUIRED",
            "DERIVATIVE_MUTATION_FAIL_CLOSED_REQUIRED");
    }

    [Fact]
    public void RawElasticClientWrites_MustRemainExplicitlyApproved()
    {
        var solutionRoot = DiscoverSolutionRoot(AppContext.BaseDirectory);
        var sourceRoots = new[]
        {
            Path.Combine(solutionRoot, "PointOfSale.API"),
            Path.Combine(solutionRoot, "PointOfSale.Infrastructure")
        };

        var offenders = sourceRoots
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                RelativePath = NormalizeRelativePath(solutionRoot, path),
                Source = StripCommentsAndStringLiterals(File.ReadAllText(path))
            })
            .Where(file =>
                file.Source.Contains("IOpenSearchClient", StringComparison.Ordinal) &&
                (file.Source.Contains(".IndexAsync(", StringComparison.Ordinal) ||
                 file.Source.Contains(".UpdateAsync<", StringComparison.Ordinal) ||
                 file.Source.Contains(".BulkAsync(", StringComparison.Ordinal) ||
                 file.Source.Contains(".DeleteAsync<", StringComparison.Ordinal)))
            .Where(file => !ApprovedRawElasticClientWriteFiles.Contains(file.RelativePath))
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Raw Elasticsearch client writes must stay behind reviewed search-index services or explicitly governed Tier-1 exceptions. Offenders: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void ApprovedElasticProjectionExceptions_MustRetainReviewedFreshnessPatterns()
    {
        var solutionRoot = DiscoverSolutionRoot(AppContext.BaseDirectory);

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.Infrastructure/Consumers/ServiceDiscoveryProjectionConsumer.cs",
            "ctx.op = 'noop'",
            "lastEventOccurredAtUtc",
            "LoadLiveServiceDocumentAsync",
            "LoadLiveServiceDocumentsByProviderAsync",
            "UpsertLiveServiceDocumentAsync(document",
            "tenant.DeletedAtUtc == null",
            "profile.TenantId == service.TenantId",
            "DeleteServiceDocumentAsync");
    }

    [Fact]
    public void ApprovedDevelopmentReplaceOneBypasses_MustStayDevelopmentScopedAndSearchWrapped()
    {
        var solutionRoot = DiscoverSolutionRoot(AppContext.BaseDirectory);

        AssertReviewedDirectSqlMarkers(
            solutionRoot,
            "PointOfSale.API/Development/CommerceHub/DevelopmentCommerceHubSeedService.cs",
            "namespace PointOfSale.API.Development.CommerceHub;",
            "ICommerceHubSearchIndexService",
            "_mongoDbContext.CommerceHubProducts.ReplaceOneAsync(",
            "BuildListingSpecs()");
    }

    [Fact]
    public void RedisPatternInvalidation_MustRemainExplicitlyApproved()
    {
        var solutionRoot = DiscoverSolutionRoot(AppContext.BaseDirectory);
        var sourceRoots = new[]
        {
            Path.Combine(solutionRoot, "PointOfSale.API"),
            Path.Combine(solutionRoot, "PointOfSale.Infrastructure")
        };

        var offenders = sourceRoots
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                RelativePath = NormalizeRelativePath(solutionRoot, path),
                Source = File.ReadAllText(path)
            })
            .Where(file => file.Source.Contains("RemoveByPatternAsync(", StringComparison.Ordinal))
            .Where(file => !ApprovedPatternInvalidationFiles.Contains(file.RelativePath))
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Redis pattern invalidation fanout is a dangerous primitive and must stay behind explicitly reviewed consumers/services. Offenders: " +
            string.Join(", ", offenders));
    }

    private static void AssertReviewedDirectSqlMarkers(
        string solutionRoot,
        string relativePath,
        params string[] markers)
    {
        var absolutePath = Path.Combine(solutionRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(absolutePath);

        foreach (var marker in markers)
        {
            Assert.Contains(
                marker,
                source,
                StringComparison.Ordinal);
        }
    }

    private static string NormalizeRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string DiscoverSolutionRoot(string startPath)
    {
        var currentDir = new DirectoryInfo(startPath);

        while (currentDir != null)
        {
            if (currentDir.GetFiles("*.sln").Any() ||
                currentDir.GetDirectories("PointOfSale.API").Any())
            {
                return currentDir.FullName;
            }

            currentDir = currentDir.Parent;
        }

        throw new InvalidOperationException($"Could not discover solution root from '{startPath}'.");
    }

    private static string StripCommentsAndStringLiterals(string source)
    {
        var result = new char[source.Length];
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var inVerbatimString = false;
        var inRawString = false;
        var rawStringQuoteCount = 0;
        var inChar = false;

        for (var i = 0; i < source.Length; i++)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                result[i] = current == '\n' || current == '\r' ? current : ' ';
                if (current == '\n')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBlockComment)
            {
                result[i] = current == '\n' || current == '\r' ? current : ' ';
                if (current == '*' && next == '/')
                {
                    result[++i] = ' ';
                    inBlockComment = false;
                }
                continue;
            }

            if (inRawString)
            {
                result[i] = current == '\n' || current == '\r' ? current : ' ';
                
                // Check for closing raw string quotes
                if (current == '"')
                {
                    var closingQuoteCount = 0;
                    var j = i;
                    while (j < source.Length && source[j] == '"')
                    {
                        closingQuoteCount++;
                        j++;
                    }

                    if (closingQuoteCount >= rawStringQuoteCount)
                    {
                        // Found closing sequence
                        for (var k = i; k < i + rawStringQuoteCount; k++)
                        {
                            result[k] = ' ';
                        }
                        i += rawStringQuoteCount - 1;
                        inRawString = false;
                        inString = false;
                        rawStringQuoteCount = 0;
                    }
                }
                continue;
            }

            if (inString)
            {
                result[i] = current == '\n' || current == '\r' ? current : ' ';
                if (inVerbatimString)
                {
                    if (current == '"' && next == '"')
                    {
                        result[++i] = ' ';
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                        inVerbatimString = false;
                    }
                    continue;
                }

                if (current == '\\' && next != '\0')
                {
                    result[++i] = ' ';
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (inChar)
            {
                result[i] = current == '\n' || current == '\r' ? current : ' ';
                if (current == '\\' && next != '\0')
                {
                    result[++i] = ' ';
                    continue;
                }

                if (current == '\'')
                {
                    inChar = false;
                }
                continue;
            }

            if (current == '/' && next == '/')
            {
                result[i] = ' ';
                result[++i] = ' ';
                inLineComment = true;
                continue;
            }

            if (current == '/' && next == '*')
            {
                result[i] = ' ';
                result[++i] = ' ';
                inBlockComment = true;
                continue;
            }

            // Check for raw string literals (""" or more quotes)
            // Also handle interpolated raw strings ($""" or $$""" etc.)
            // and verbatim strings (@"" or @$"" or $@"" in any order)
            if (current == '"' || current == '$' || current == '@')
            {
                var startPos = i;
                var isAtPrefixed = false;
                
                // Consume any sequence of '$' and '@' prefix characters
                while (i < source.Length && (source[i] == '$' || source[i] == '@'))
                {
                    if (source[i] == '@')
                    {
                        isAtPrefixed = true;
                    }
                    result[i] = ' ';
                    i++;
                }
                
                // Verify the next character(s) are quotes, otherwise revert
                if (i >= source.Length || source[i] != '"')
                {
                    // Not a string literal, revert masking and continue
                    for (var k = startPos; k < i; k++)
                    {
                        result[k] = source[k];
                    }
                    i = startPos;
                    result[i] = current;
                    continue;
                }
                
                current = source[i];

                // Count consecutive quotes
                var quoteCount = 0;
                var j = i;
                while (j < source.Length && source[j] == '"')
                {
                    quoteCount++;
                    j++;
                }

                if (quoteCount >= 3)
                {
                    // Raw string literal
                    for (var k = i; k < i + quoteCount; k++)
                    {
                        result[k] = ' ';
                    }
                    i += quoteCount - 1;
                    inString = true;
                    inRawString = true;
                    rawStringQuoteCount = quoteCount;
                }
                else if (isAtPrefixed)
                {
                    // Verbatim string (@"..." or @$"..." or $@"...")
                    result[i] = ' ';
                    inString = true;
                    inVerbatimString = true;
                }
                else
                {
                    // Regular string (including interpolated $"...")
                    result[i] = ' ';
                    inString = true;
                }
                continue;
            }

            if (current == '\'')
            {
                result[i] = ' ';
                inChar = true;
                continue;
            }

            result[i] = current;
        }

        return new string(result);
    }
}
