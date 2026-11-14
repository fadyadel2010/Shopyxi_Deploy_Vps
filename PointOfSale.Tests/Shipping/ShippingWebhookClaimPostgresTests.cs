using System.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Customs;
using PointOfSale.Core.Interfaces.Fulfillment;
using PointOfSale.Core.Interfaces.Geographic;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.EventBus;
using PointOfSale.Infrastructure.Fulfillment;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Shipping;
using PointOfSale.Infrastructure.Shipping.Adapters;
#if !CURRENT_DATABASE_RUN
using PointOfSale.Tests.Integration;
#endif
using BusMessage = MassTransit.EntityFrameworkCoreIntegration.OutboxMessage;

namespace PointOfSale.Tests.Shipping;

// Ordinary xUnit requires the guarded disposable PostgreSQL18/current-model
// schema. It never creates/migrates/resets a database or discovers shared config.
// The separately compiled CURRENT_DATABASE_RUN path is unchanged and uninvoked.
[Trait("Category", "Relational")]
public sealed class ShippingWebhookClaimPostgresTests
#if CURRENT_DATABASE_RUN
    : IDisposable
#else
    : IAsyncLifetime
#endif
{
#if CURRENT_DATABASE_RUN
    // Separate explicitly injected current-database path. The reset harness and its
    // authorization flag are not compiled into this diagnostic executable.
    private readonly Func<ApplicationDbContext> currentDatabase;
    private readonly IInterceptor[] manifestInterceptors;
    public ShippingWebhookClaimPostgresTests(Guid tenantId, Func<ApplicationDbContext> factory, params IInterceptor[] manifest)
    { tenant = tenantId; currentDatabase = factory; manifestInterceptors = manifest; }
    public void Dispose() { }
    private ApplicationDbContext Db() => currentDatabase();
#else
    private ProductionCoordinationAuthorityTests.Fixture database = null!;
    public async Task InitializeAsync()
    {
        database = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        try
        {
            await using var db = Db();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Disposable synthetic claim regression", Slug = $"claims-{tenant:N}",
                Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        catch { await database.DisposeAsync(); throw; }
    }
    public async Task DisposeAsync() => await database.DisposeAsync();
    private ApplicationDbContext Db() => database.ScopedDb(tenant);
#endif

    private readonly Guid tenant = Guid.NewGuid();
    private IQueryable<ShippingWebhookLog> Logs(ApplicationDbContext db) => db.ShippingWebhookLogs.IgnoreQueryFilters().Where(l => l.TenantId == tenant);
    private IQueryable<ShippingWebhookClaim> Claims(ApplicationDbContext db) => db.ShippingWebhookClaims.IgnoreQueryFilters().Where(c => c.TenantId == tenant);
#if CURRENT_DATABASE_RUN
    private IQueryable<BusMessage> Messages(ApplicationDbContext db) => db.Set<BusMessage>().AsNoTracking()
        .Where(m => m.Body.Contains(tenant.ToString()));
#else
    // The canonical publisher stages logistics facts in the application SQL
    // outbox inside the business transaction, not MassTransit's bus-outbox
    // table. Preserve the historical method IDs, not an obsolete table claim.
    private IQueryable<OutboxMessage> Messages(ApplicationDbContext db) => db.OutboxMessages.AsNoTracking().Where(m => m.TenantId == tenant);

    private async Task AssertCanonicalFacts(ApplicationDbContext db, IReadOnlyCollection<OutboxMessage> messages)
    {
        Assert.NotEmpty(messages);
        Assert.Equal(messages.Count, messages.Select(m => m.Id).Distinct().Count());
        foreach (var message in messages)
        {
            Assert.NotEqual(Guid.Empty, message.Id); Assert.Equal(tenant, message.TenantId);
            Assert.True(message.AggregateId.HasValue && message.AggregateId != Guid.Empty);
            Assert.True(message.EventVersion > 0); Assert.Null(message.ProcessedAtUtc); Assert.Equal(0, message.RetryCount);
            using var payload = System.Text.Json.JsonDocument.Parse(message.EventPayload);
            Assert.Equal(message.Id, payload.RootElement.GetProperty("eventId").GetGuid());
            Assert.Equal(tenant, payload.RootElement.GetProperty("tenantId").GetGuid());
            Assert.Equal(message.EventVersion, payload.RootElement.GetProperty("eventVersion").GetInt32());
        }
        Assert.Single(messages, m => m.EventType == typeof(PointOfSale.Core.Events.ShipmentStatusChangedEvent).AssemblyQualifiedName);
        Assert.Single(messages, m => m.EventType == typeof(PointOfSale.Core.Events.ShipmentDeliveredEvent).AssemblyQualifiedName);
        // No logistics fact may escape as a direct/second bus envelope. This
        // never starts the bus or executes the canonical outbox publisher job.
        Assert.Empty(await db.Set<BusMessage>().AsNoTracking().ToListAsync());
    }

    private static string Snapshot(IReadOnlyCollection<OutboxMessage> messages) => System.Text.Json.JsonSerializer.Serialize(
        messages.OrderBy(m => m.Id).Select(m => new { m.Id, m.TenantId, m.AggregateId, m.EventType, m.EventVersion, m.EventPayload }));
#endif

    private static async Task AssertLockRefused(Func<Task> action)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(action);
        while (error.InnerException != null && error is not PostgresException) error = error.InnerException;
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(error).SqlState);
    }

    private async Task<(ShippingWebhookLog Log, ShippingWebhookClaim Claim)> Seed()
    {
        await using var db = Db();
        var log = new ShippingWebhookLog { Id = Guid.NewGuid(), TenantId = tenant, CarrierCode = "bosta",
            EventId = Guid.NewGuid().ToString(), PayloadHash = new string('A', 64), EventType = "synthetic",
            ShipmentId = Guid.NewGuid(), OrderId = Guid.NewGuid(), SignatureVerified = true,
            ValidationMethod = "shared_secret", ProcessingStatus = WebhookProcessingStatus.Processing };
        await using var tx = await db.Database.BeginTransactionAsync();
        db.ShippingWebhookLogs.Add(log);
        var claim = await new ShippingWebhookClaimProtocol(db).CreateFirstAsync(log, default);
        await tx.CommitAsync();
        return (log, claim);
    }
    private async Task Expire(ShippingWebhookLog log)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ShippingWebhookClaims\" SET \"LeaseUntil\"=clock_timestamp()-interval '1 second' WHERE \"LogId\"={log.Id}");
    }
    private async Task<ShippingWebhookClaim?> Acquire(ShippingWebhookLog log)
    {
        await using var db = Db();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var claim = await new ShippingWebhookClaimProtocol(db).AcquireAsync(log, default);
        await tx.CommitAsync();
        return claim;
    }

    [Fact]
    public async Task InitialAdmission_RollbackLeavesNeitherInboxNorClaim()
    {
        await using var db = Db();
        var log = new ShippingWebhookLog { Id = Guid.NewGuid(), TenantId = tenant, CarrierCode = "bosta", EventId = "rollback", PayloadHash = "rollback", ProcessingStatus = WebhookProcessingStatus.Processing };
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.ShippingWebhookLogs.Add(log);
            await new ShippingWebhookClaimProtocol(db).CreateFirstAsync(log, default);
            await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Empty(await Logs(db).ToListAsync());
        Assert.Empty(await Claims(db).ToListAsync());
    }

    [Fact]
    public async Task ActiveDuplicate_CannotAcquireAnotherGeneration()
    {
        var (log, claim) = await Seed();
        Assert.Equal(1, claim.Generation); Assert.NotEqual(Guid.Empty, claim.Owner);
        Assert.Null(await Acquire(log));
        await using var db = Db(); Assert.Single(await Claims(db).ToListAsync());
    }

    [Fact]
    public async Task GenerationOne_RejectsPersistedLegacyRecordOrUnrelatedDirtyEffects()
    {
        await using var db = Db();
        var log = new ShippingWebhookLog { Id = Guid.NewGuid(), TenantId = tenant, CarrierCode = "bosta", EventId = "legacy",
            PayloadHash = "legacy", ProcessingStatus = WebhookProcessingStatus.Processing };
        db.ShippingWebhookLogs.Add(log); await db.SaveChangesAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var protocol = new ShippingWebhookClaimProtocol(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.CreateFirstAsync(log, default));
        var fresh = new ShippingWebhookLog { Id = Guid.NewGuid(), TenantId = tenant, CarrierCode = "bosta", EventId = "new",
            PayloadHash = "new", ProcessingStatus = WebhookProcessingStatus.Processing };
        db.ShippingWebhookLogs.Add(fresh); log.ProcessingError = "unrelated dirty audit";
        await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.CreateFirstAsync(fresh, default));
        await tx.RollbackAsync();
        Assert.Empty(await Claims(db).ToListAsync());
    }

    [Fact]
    public async Task ExpiredClaim_ReclaimsWithHigherGenerationAndDifferentOwner()
    {
        var (log, old) = await Seed(); await Expire(log);
        var next = (await Acquire(log))!;
        Assert.Equal(old.Generation + 1, next.Generation); Assert.NotEqual(old.Owner, next.Owner);
        Assert.Null(await Acquire(log));
        await using var db = Db(); Assert.Equal(2, await Claims(db).CountAsync());
    }

    [Fact]
    public async Task ConcurrentAcquisition_RowLockRejectsContenderAndOnlyOneGenerationWins()
    {
        var (log, _) = await Seed(); await Expire(log);
        await using var first = Db(); await using var tx = await first.Database.BeginTransactionAsync();
        var winner = await new ShippingWebhookClaimProtocol(first).AcquireAsync(log, default);
        Assert.NotNull(winner);
        await AssertLockRefused(() => Acquire(log));
        await tx.CommitAsync(); Assert.Null(await Acquire(log));
        await using var inspect = Db(); Assert.Equal(2, await Claims(inspect).CountAsync());
    }

    [Fact]
    public async Task OldOwner_CannotFenceFinishOrOverwriteNewOwnerFailure()
    {
        var (log, old) = await Seed(); await Expire(log); var next = (await Acquire(log))!;
        await using var db = Db(); var protocol = new ShippingWebhookClaimProtocol(db);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            Assert.False(await protocol.FenceBusinessAsync(log, old, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.FinishAsync(log, old, false, null, default));
            // Execute the conditional SQL as well, not merely the transaction-token guard.
            await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.FinishAsync(log, old, true, "stale SQL owner", default));
            await tx.RollbackAsync();
        }
        await protocol.FailAsync(log, old, "stale", 0, default);
        Assert.Equal("Processing", (await db.ShippingWebhookClaims.SingleAsync(c => c.Generation == next.Generation)).Outcome);
    }

    [Fact]
    public async Task PausedBusinessOwner_EvenExpired_CannotBeReclaimed()
    {
        var (log, owner) = await Seed();
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        var protocol = new ShippingWebhookClaimProtocol(db);
        Assert.True(await protocol.FenceBusinessAsync(log, owner, default));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ShippingWebhookClaims\" SET \"LeaseUntil\"=clock_timestamp()-interval '1 second' WHERE \"LogId\"={log.Id}");
        await AssertLockRefused(() => Acquire(log));
        await protocol.FinishAsync(log, owner, false, null, default);
        await db.ShippingWebhookLogs.IgnoreQueryFilters().Where(l => l.Id == log.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.ProcessingStatus, WebhookProcessingStatus.Completed));
        await tx.CommitAsync(); Assert.Null(await Acquire(log));
    }

    [Fact]
    public async Task RenewalWins_ReclaimerCannotPassLockOrNewLease()
    {
        var (log, owner) = await Seed();
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        Assert.True(await new ShippingWebhookClaimProtocol(db).RenewAsync(log, owner, default));
        await AssertLockRefused(() => Acquire(log));
        await tx.CommitAsync(); Assert.Null(await Acquire(log));
    }

    [Fact]
    public async Task ReclamationWins_ExpiredOwnerCannotRenew()
    {
        var (log, owner) = await Seed(); await Expire(log); Assert.NotNull(await Acquire(log));
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        Assert.False(await new ShippingWebhookClaimProtocol(db).RenewAsync(log, owner, default));
    }

    [Theory]
    [InlineData(WebhookProcessingStatus.Processing)]
    [InlineData(WebhookProcessingStatus.Failed)]
    [InlineData(WebhookProcessingStatus.Pending)]
    public async Task LegacyUnresolved_WithoutClaims_IsNeverAssignedHistoricalOwnership(WebhookProcessingStatus status)
    {
        var (log, _) = await Seed(); await using var db = Db();
        await Claims(db).Where(c => c.LogId == log.Id).ExecuteDeleteAsync();
        await Logs(db).Where(l => l.Id == log.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.ProcessingStatus, status));
        Assert.Null(await Acquire(log)); Assert.Empty(await Claims(db).ToListAsync());
    }

    [Fact]
    public async Task IndependentFailureAudit_RetriesWithoutErasingOriginalAttempt()
    {
        var (log, claim) = await Seed();
        using var request = new CancellationTokenSource(); request.Cancel();
        using var boundedAudit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var db = Db();
        await new ShippingWebhookClaimProtocol(db).FailAsync(log, claim, new string('X', 1200), 10, boundedAudit.Token);
        var next = (await Acquire(log))!; Assert.Equal(2, next.Generation);
        var old = await db.ShippingWebhookClaims.SingleAsync(c => c.Generation == 1);
        Assert.Equal("Failed", old.Outcome); Assert.Equal(1000, old.Error!.Length); Assert.NotNull(old.FinishedAt);
    }

    [Fact]
    public async Task IdentityOrTenantDisagreement_CannotAcquireOrRenew()
    {
        var (log, claim) = await Seed(); await Expire(log);
        log.TenantId = Guid.NewGuid(); Assert.Null(await Acquire(log));
        log.TenantId = tenant; log.EventId = "conflicting"; Assert.Null(await Acquire(log));
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        Assert.False(await new ShippingWebhookClaimProtocol(db).RenewAsync(log, claim, default));
    }

    private async Task<Shipment> SeedShipment()
    {
        await using var db = Db();
#if CURRENT_DATABASE_RUN
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Synthetic claim regression", Slug = $"claims-{tenant:N}", IsActive = true, Status = TenantStatus.Active });
#else
        Assert.True(await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenant && t.IsActive && t.Currency == "EGP"));
#endif
        var order = new Order { Id = Guid.NewGuid(), TenantId = tenant, OrderNumber = $"CLAIM-{Guid.NewGuid():N}", Currency = "EGP",
            ShippingAddress = "Synthetic", FulfillmentMode = FulfillmentMode.Carrier, FulfillmentCarrierCode = "bosta",
            FulfillmentStatus = FulfillmentStatus.OutForDelivery, Status = OrderStatus.Shipped };
        var shipment = new Shipment { Id = Guid.NewGuid(), TenantId = tenant, OrderId = order.Id, Order = order,
            CarrierCode = "bosta", TrackingNumber = $"CLAIM-{Guid.NewGuid():N}", ShipmentNumber = $"CLAIM-{Guid.NewGuid():N}",
            Status = ShipmentStatus.OutForDelivery, Currency = "EGP", ShippingAddress = "Synthetic", RecipientName = "Synthetic" };
        db.Shipments.Add(shipment);
        db.CarrierConfigurations.Add(new CarrierConfiguration { Id = Guid.NewGuid(), TenantId = tenant,
            CarrierCode = "bosta", DisplayName = "Synthetic", IsEnabled = true, SupportsWebhooks = true, WebhookSecret = "synthetic" });
        await db.SaveChangesAsync(); return shipment;
    }

    private ServiceProvider Provider(Shipment shipment, params IInterceptor[] interceptors) => Provider(shipment, true, interceptors);

    private ServiceProvider Provider(Shipment shipment, bool enabled, params IInterceptor[] interceptors)
        => Provider(shipment, enabled, null, interceptors);

    private ServiceProvider Provider(Shipment shipment, bool enabled, IShippingCarrierAdapter? suppliedAdapter, params IInterceptor[] interceptors)
    {
        using var db = Db();
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(interceptors);
#if CURRENT_DATABASE_RUN
        builder.AddInterceptors(manifestInterceptors);
#endif
        var options = builder.Options;
        var services = new ServiceCollection(); services.AddLogging();
        services.AddScoped<TenantContext>(); services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped(sp => new ApplicationDbContext(options, sp.GetRequiredService<ITenantContext>()));
        services.AddScoped<IEventPublisher, MassTransitEventPublisher>(); services.AddScoped<IOrderFulfillmentService, OrderFulfillmentService>();
        services.AddSingleton<CommerceReliabilityTelemetry>();
        services.AddSingleton(Mock.Of<ICustomsEvidenceAdmissionService>()); services.AddSingleton(Mock.Of<ICarrierGeographicMappingService>());
        var encryption = new Mock<ICarrierCredentialEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<string?>())).Returns((string? value) => value ?? "");
        services.AddSingleton(encryption.Object);
        services.AddScoped<ICarrierIntegrationAuthority, CarrierIntegrationAuthority>();
        services.AddScoped<ICarrierCapabilityRegistry, CarrierCapabilityRegistry>();
        var adapter = new Mock<IShippingCarrierAdapter>(MockBehavior.Strict);
        adapter.SetupGet(a => a.CarrierCode).Returns("bosta");
        adapter.SetupGet(a => a.DisplayName).Returns("Synthetic Bosta");
        adapter.Setup(a => a.GetSupportedServiceLevels()).Returns(Array.Empty<ServiceLevelInfo>());
        adapter.Setup(a => a.ParseWebhook(It.IsAny<string>(), null, null)).Returns((string payload, string? _, string? __) =>
            new WebhookParseResult { TrackingNumber = shipment.TrackingNumber!, Status = ShipmentStatus.Delivered, RawPayload = payload });
        var registry = new Mock<IShippingCarrierRegistry>(MockBehavior.Strict);
        var activeAdapter = suppliedAdapter ?? adapter.Object;
        registry.Setup(r => r.GetAdapter("bosta")).Returns(activeAdapter);
        registry.Setup(r => r.GetAllAdapters()).Returns([activeAdapter]);
        registry.Setup(r => r.GetSupportedCarrierCodes()).Returns(["bosta"]);
        services.AddSingleton(registry.Object);
        services.AddSingleton<IOptions<ShippingWebhookClaimOptions>>(Options.Create(new ShippingWebhookClaimOptions { Enabled = enabled }));
        services.AddScoped<ICarrierShipmentService, CarrierShipmentService>();
        services.AddMassTransitTestHarness(config => config.AddEntityFrameworkOutbox<ApplicationDbContext>(outbox => { outbox.UsePostgres(); outbox.UseBusOutbox(); }));
        // Bus never started; scoped EF bus outbox is real, no transport/carrier/financial execution.
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<WebhookProcessResult> Deliver(ServiceProvider provider, Shipment shipment, CancellationToken ct = default)
    {
        await using var scope = provider.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenantId(tenant);
        return await scope.ServiceProvider.GetRequiredService<ICarrierShipmentService>().ProcessWebhookAsync("bosta",
            $$"""{"eventId":"{{shipment.Id}}","trackingNumber":"{{shipment.TrackingNumber}}","status":"delivered"}""", "synthetic", null, null, ct);
    }

    [Fact]
    public async Task DisabledHandler_DoesNotIssueAnyClaimsTableSql()
    {
        var shipment = await SeedShipment();
        await using var provider = Provider(shipment, false, new RejectClaimsSql());
        Assert.True((await Deliver(provider, shipment)).Success);
        Assert.True((await Deliver(provider, shipment)).IsDuplicate);
        await using var db = Db(); Assert.Empty(await Claims(db).ToListAsync());
    }

    [Fact]
    public async Task ActualHandler_CommitThenLostAcknowledgement_ReplayPreservesExactlyOnceEffectsAndRealBusOutbox()
    {
        var shipment = await SeedShipment();
        await using var provider = Provider(shipment, new LostBusinessAcknowledgement());
        Assert.False((await Deliver(provider, shipment)).Success);
        await using var db = Db();
        Assert.Equal(ShipmentStatus.Delivered, (await db.Shipments.SingleAsync()).Status);
        Assert.Equal(WebhookProcessingStatus.Completed, (await Logs(db).SingleAsync()).ProcessingStatus);
        Assert.Equal("Completed", (await Claims(db).SingleAsync()).Outcome);
        var messages = await Messages(db).ToListAsync(); Assert.NotEmpty(messages);
#if CURRENT_DATABASE_RUN
        Assert.Single(messages, m => m.MessageType.Contains(":ShipmentStatusChangedEvent", StringComparison.Ordinal));
        Assert.Single(messages, m => m.MessageType.Contains(":ShipmentDeliveredEvent", StringComparison.Ordinal));
        Assert.Equal(messages.Count, messages.Select(m => m.MessageId).Distinct().Count());
#else
        await AssertCanonicalFacts(db, messages);
        var beforeReplay = Snapshot(messages);
#endif
        var order = await db.Orders.AsNoTracking().SingleAsync();
        Assert.Equal(FulfillmentStatus.Delivered, order.FulfillmentStatus);
        Assert.True(order.ProjectionMutationVersion > shipment.Order.ProjectionMutationVersion);
        Assert.True((await Deliver(provider, shipment)).IsDuplicate);
#if CURRENT_DATABASE_RUN
        Assert.Equal(messages.Select(m => m.SequenceNumber).Order(), (await Messages(db).ToListAsync()).Select(m => m.SequenceNumber).Order());
#else
        Assert.Equal(beforeReplay, Snapshot(await Messages(db).ToListAsync()));
        await AssertCanonicalFacts(db, await Messages(db).ToListAsync());
#endif
        Assert.Equal(order.ProjectionMutationVersion, (await db.Orders.AsNoTracking().SingleAsync()).ProjectionMutationVersion);
        Assert.Single(await Claims(db).ToListAsync());
    }

    [Fact]
    public async Task TerminalInbox_WithInconsistentClaimAudit_IsNotAcknowledged()
    {
        var shipment = await SeedShipment(); await using var provider = Provider(shipment);
        Assert.True((await Deliver(provider, shipment)).Success);
        await using var db = Db();
        var before = await Messages(db).CountAsync();
        await Claims(db).ExecuteUpdateAsync(s => s.SetProperty(c => c.Outcome, "Failed"));
        var result = await Deliver(provider, shipment);
        Assert.False(result.Success); Assert.Equal("WEBHOOK_RECONCILIATION_REQUIRED", result.ErrorCode);
        Assert.Equal(before, await Messages(db).CountAsync());
    }

    [Fact]
    public async Task ActualHandler_ActiveDuplicateWhileBusinessPaused_IsNotAcknowledgedOrReplayed()
    {
        var shipment = await SeedShipment(); var pause = new PauseAfterBusinessSave();
        await using var provider = Provider(shipment, pause);
        var first = Deliver(provider, shipment);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var duplicate = await Deliver(provider, shipment);
            Assert.False(duplicate.Success); Assert.Equal(503, duplicate.StatusCode);
            Assert.Equal("WEBHOOK_PROCESSING_UNRESOLVED", duplicate.ErrorCode);
        }
        finally { pause.Release.TrySetResult(); }
        Assert.True((await first).Success);
        await using var db = Db(); Assert.Single(await Claims(db).ToListAsync());
        var messages = await Messages(db).ToListAsync(); Assert.NotEmpty(messages);
#if !CURRENT_DATABASE_RUN
        await AssertCanonicalFacts(db, messages);
        var beforeReplay = Snapshot(messages);
#endif
        Assert.True((await Deliver(provider, shipment)).IsDuplicate);
        Assert.Equal(messages.Count, await Messages(db).CountAsync());
#if !CURRENT_DATABASE_RUN
        Assert.Equal(beforeReplay, Snapshot(await Messages(db).ToListAsync()));
#endif
    }

    [Fact]
    public async Task ActualHandler_FailureBeforeBusinessTransaction_LeavesRecoverableOwnedAuditAndNoEffects()
    {
        var shipment = await SeedShipment(); using var request = new CancellationTokenSource();
        await using var provider = Provider(shipment, new CancelBeforeRenewal(request));
        Assert.False((await Deliver(provider, shipment, request.Token)).Success);
        await using var db = Db();
        Assert.Equal(ShipmentStatus.OutForDelivery, (await db.Shipments.SingleAsync()).Status);
        Assert.Equal(FulfillmentStatus.OutForDelivery, (await db.Orders.AsNoTracking().SingleAsync()).FulfillmentStatus);
        Assert.Equal(shipment.Order.ProjectionMutationVersion, (await db.Orders.AsNoTracking().SingleAsync()).ProjectionMutationVersion);
        Assert.Empty(await Messages(db).ToListAsync());
        Assert.Equal(WebhookProcessingStatus.Failed, (await Logs(db).SingleAsync()).ProcessingStatus);
        Assert.Equal("Failed", (await Claims(db).SingleAsync()).Outcome);
        Assert.True((await Deliver(provider, shipment)).Success);
        Assert.Equal(2, await Claims(db).CountAsync());
    }

    [Fact]
    public async Task FailureAuditUnavailable_ProcessingRemainsSafelyReclaimableAfterExpiry()
    {
        var (log, owner) = await Seed();
        await using var db = Db(); using var unavailable = new CancellationTokenSource(); unavailable.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ShippingWebhookClaimProtocol(db).FailAsync(log, owner, "unavailable", 0, unavailable.Token));
        Assert.Equal("Processing", (await db.ShippingWebhookClaims.SingleAsync()).Outcome);
        await Expire(log); Assert.Equal(2, (await Acquire(log))!.Generation);
    }

    [Fact]
    public async Task ActualHandler_FailureDuringTransaction_RollsBackEffectsOutboxAndPersistsFailureDespiteCancellation()
    {
        var shipment = await SeedShipment(); using var request = new CancellationTokenSource();
        await using var provider = Provider(shipment, new CancelAfterBusinessSave(request));
        Assert.False((await Deliver(provider, shipment, request.Token)).Success);
        await using var db = Db();
        Assert.Equal(ShipmentStatus.OutForDelivery, (await db.Shipments.SingleAsync()).Status);
        Assert.Equal(FulfillmentStatus.OutForDelivery, (await db.Orders.AsNoTracking().SingleAsync()).FulfillmentStatus);
        Assert.Equal(shipment.Order.ProjectionMutationVersion, (await db.Orders.AsNoTracking().SingleAsync()).ProjectionMutationVersion);
        Assert.Empty(await Messages(db).ToListAsync());
        Assert.Equal(WebhookProcessingStatus.Failed, (await Logs(db).SingleAsync()).ProcessingStatus);
        Assert.Equal("Failed", (await Claims(db).SingleAsync()).Outcome);
        Assert.True((await Deliver(provider, shipment)).Success);
        var count = await Messages(db).CountAsync(); Assert.True(count > 0);
#if !CURRENT_DATABASE_RUN
        await AssertCanonicalFacts(db, await Messages(db).ToListAsync());
        var beforeReplay = Snapshot(await Messages(db).ToListAsync());
#endif
        Assert.True((await Deliver(provider, shipment)).IsDuplicate); Assert.Equal(count, await Messages(db).CountAsync());
#if !CURRENT_DATABASE_RUN
        Assert.Equal(beforeReplay, Snapshot(await Messages(db).ToListAsync()));
#endif
        Assert.Equal(2, await Claims(db).CountAsync());
    }

    [Theory]
    [InlineData("qualified")]
    [InlineData("review-amount")]
    [InlineData("wrong-secret")]
    [InlineData("foreign-tenant")]
    [InlineData("sql-save-failure")]
    public async Task MonetaryObservationUsesItsOwnAtomicInboxAndCanonicalOutboxWithoutLogisticsClaimsOrPaymentRecognition(string state)
    {
        Shipment shipment;
        await using (var seed = Db())
        {
            var order = new Order { Id = Guid.NewGuid(), TenantId = tenant, OrderNumber = "FICTIONAL-OBSERVATION-ONLY", Total = 100m,
                Currency = "EGP", PaymentMethodCode = "COD", PaymentStatus = PaymentStatus.Pending, Status = OrderStatus.Confirmed,
                FulfillmentMode = FulfillmentMode.Carrier, FulfillmentCarrierCode = "bosta", FulfillmentStatus = FulfillmentStatus.Delivered };
            shipment = new Shipment { Id = Guid.NewGuid(), TenantId = tenant, OrderId = order.Id, Order = order, CarrierCode = "bosta",
                ShipmentNumber = "FICTIONAL-OBSERVATION", TrackingNumber = "12345678901234567890", ExternalReferenceId = "delivery-1",
                Currency = "EGP", IsCashOnDelivery = true, CodAmount = 100m, Status = ShipmentStatus.Delivered,
                ExternalRegistrationCompletedAtUtc = DateTime.UtcNow.AddHours(-1), ExternalRegistrationState = "Completed" };
            var config = new CarrierConfiguration { Id = Guid.NewGuid(), TenantId = tenant, CarrierCode = "bosta", DisplayName = "Fictional observation fixture",
                IsEnabled = true, SupportsCod = true, SupportsWebhooks = true, ApiKey = "fictional-only", WebhookSecret = "synthetic",
                AccountId = "account-1", UseSandbox = true, Configuration = "{\"codCollectionCurrency\":\"EGP\"}" };
            seed.AddRange(order, shipment, config);
            using var authority = CarrierMoneyPersistenceAuthority.Begin();
            seed.CarrierCodShipmentBindings.Add(new CarrierCodShipmentBinding { Id = Guid.NewGuid(), TenantId = tenant, OrderId = order.Id,
                ShipmentId = shipment.Id, ConfigurationId = config.Id, CarrierCode = "bosta", AccountId = "account-1", Environment = "Sandbox",
                Currency = "EGP", TrackingNumber = shipment.TrackingNumber, DeliveryReference = "delivery-1", ExternalReferenceId = "delivery-1",
                Fingerprint = CarrierCollectionMoneyAuthority.BindingFingerprint(config, shipment, "account-1", "EGP", "delivery-1") });
            await seed.SaveChangesAsync();
        }
        using var network = new RejectCarrierNetwork(); using var http = new HttpClient(network);
        var adapter = new BostaAdapter(new BostaOfficialCollectionParserTests.Factory(http), NullLogger<BostaAdapter>.Instance);
        var failure = new RejectObservationSave();
        IInterceptor[] interceptors = state == "sql-save-failure" ? [new RejectClaimsSql(), failure] : [new RejectClaimsSql()];
        await using var provider = Provider(shipment, true, adapter, interceptors);
        var payload = BostaOfficialCollectionParserTests.Payload(state == "review-amount" ? "99" : "100",
            time: DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        async Task<WebhookProcessResult> Observe(ServiceProvider services)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenantId(state == "foreign-tenant" ? Guid.NewGuid() : tenant);
            return await scope.ServiceProvider.GetRequiredService<ICarrierShipmentService>().ProcessWebhookAsync("bosta", payload,
                state == "wrong-secret" ? "wrong" : "synthetic", "203.0.113.10", "fictional-local-observation", default);
        }
        var result = await Observe(provider);
        var accepted = state is "qualified" or "review-amount";
        Assert.Equal(accepted, result.Success);
        await using var verify = Db();
        Assert.Empty(await Claims(verify).ToListAsync());
        Assert.Equal(accepted ? 1 : 0, await verify.CarrierCollectionEvidence.CountAsync());
        Assert.Equal(accepted ? 1 : 0, await Logs(verify).CountAsync());
        if (state == "sql-save-failure")
        {
            Assert.True(failure.Observed); Assert.Empty(await verify.OutboxMessages.ToListAsync());
            Assert.False((await verify.Shipments.AsNoTracking().SingleAsync()).CodCollected);
            await using var retry = Provider(shipment, true, adapter, new RejectClaimsSql());
            Assert.True((await Observe(retry)).Success);
            Assert.Single(await verify.CarrierCollectionEvidence.AsNoTracking().ToListAsync());
            Assert.Single(await Logs(verify).ToListAsync());
            Assert.Single(await verify.OutboxMessages.Where(m => m.EventType == typeof(ShipmentCodCollectedEvent).AssemblyQualifiedName).ToListAsync());
        }
        else if (accepted)
        {
            var evidence = await verify.CarrierCollectionEvidence.SingleAsync();
            Assert.Equal(state == "qualified" ? "Qualified" : "Review", evidence.AdmissionOutcome);
            Assert.Equal(state == "qualified" ? "COD_WHOLE_ORDER_QUALIFIED" : "COD_WHOLE_ORDER_AMOUNT_MISMATCH", evidence.AdmissionCode);
            Assert.Equal(tenant, evidence.TenantId); Assert.Equal(shipment.Id, evidence.ShipmentId);
            Assert.Equal(state == "qualified", (await verify.Shipments.AsNoTracking().SingleAsync()).CodCollected);
            var facts = await verify.OutboxMessages.AsNoTracking().ToListAsync();
            Assert.Equal(state == "qualified" ? 1 : 0, facts.Count(m => m.EventType == typeof(ShipmentCodCollectedEvent).AssemblyQualifiedName));
            if (state == "qualified")
            {
                var fact = Assert.Single(facts); Assert.Equal(tenant, fact.TenantId); Assert.Null(fact.ProcessedAtUtc); Assert.Equal(0, fact.RetryCount);
                using var body = System.Text.Json.JsonDocument.Parse(fact.EventPayload);
                Assert.Equal(evidence.Id, body.RootElement.GetProperty("collectionEvidenceId").GetGuid());
                Assert.Equal(fact.Id, body.RootElement.GetProperty("eventId").GetGuid());
            }
            var before = System.Text.Json.JsonSerializer.Serialize(facts.OrderBy(m => m.Id).Select(m => new { m.Id, m.EventType, m.EventPayload }));
            Assert.True((await Observe(provider)).IsDuplicate);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize((await verify.OutboxMessages.AsNoTracking().ToListAsync()).OrderBy(m => m.Id).Select(m => new { m.Id, m.EventType, m.EventPayload })));
            Assert.Single(await verify.CarrierCollectionEvidence.AsNoTracking().ToListAsync());
        }
        else Assert.Empty(await verify.OutboxMessages.ToListAsync());
        Assert.Empty(await verify.Set<BusMessage>().ToListAsync()); Assert.Empty(await Claims(verify).ToListAsync());
        Assert.Equal(PaymentStatus.Pending, (await verify.Orders.AsNoTracking().SingleAsync()).PaymentStatus);
        Assert.Empty(await verify.JournalEntries.ToListAsync()); Assert.Empty(await verify.LedgerEntries.ToListAsync());
        Assert.Empty(await verify.AccountingOutboxMessages.ToListAsync()); Assert.Empty(await verify.PaymentSettlementBatches.ToListAsync());
        Assert.Equal(0, network.Calls);
    }

    private sealed class RejectCarrierNetwork : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("No external carrier call is authorized by the observation fixture."); }
    }

    private sealed class RejectObservationSave : SaveChangesInterceptor
    {
        internal bool Observed { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<CarrierCollectionEvidence>().Any(e => e.State == EntityState.Added))
            {
                Assert.Contains(data.Context.ChangeTracker.Entries<OutboxMessage>(), e => e.State == EntityState.Added && e.Entity.EventType == typeof(ShipmentCodCollectedEvent).AssemblyQualifiedName);
                Observed = true; throw new InvalidOperationException("Synthetic observation save failure after canonical outbox staging");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class LostBusinessAcknowledgement : DbTransactionInterceptor
    {
        private int commits;
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == 3) throw new IOException("Synthetic lost business commit acknowledgement");
            return Task.CompletedTask;
        }
    }
    private sealed class CancelAfterBusinessSave(CancellationTokenSource request) : SaveChangesInterceptor
    {
        private int saves;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref saves) == 2) { request.Cancel(); throw new OperationCanceledException("Synthetic failure after SQL effects, before commit", request.Token); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PauseAfterBusinessSave : SaveChangesInterceptor
    {
        private int saves;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref saves) == 2)
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            return result;
        }
    }
    private sealed class CancelBeforeRenewal(CancellationTokenSource request) : DbCommandInterceptor
    {
        private int injected;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SET \"LeaseUntil\"", StringComparison.Ordinal) && Interlocked.Exchange(ref injected, 1) == 0)
            {
                request.Cancel(); throw new OperationCanceledException("Synthetic failure before business transaction", request.Token);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RejectClaimsSql : DbCommandInterceptor
    {
        private static void Check(System.Data.Common.DbCommand command)
        {
            if (command.CommandText.Contains("ShippingWebhookClaims", StringComparison.Ordinal))
                throw new InvalidOperationException("Disabled protocol queried the new claims table.");
        }
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command,
            CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
    }
}
