using System.Data.Common;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PointOfSale.API.Services.CommerceHub.Finance;
using PointOfSale.API.Infrastructure.Idempotency;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Eventing;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.Analytics.Delivery;
using PointOfSale.Core.Analytics;
using OrderCreatedEvent = PointOfSale.Core.Events.Marketplace.OrderCreatedEvent;

namespace PointOfSale.Tests.Integration;

// No skip or in-memory fallback: these claims require a disposable PostgreSQL18.
public sealed class ProductionCoordinationAuthorityTests
{
    [Theory]
    [InlineData("success", false)]
    [InlineData("retry", false)]
    [InlineData("terminal", false)]
    [InlineData("pre-send", false)]
    [InlineData("reload", false)]
    [InlineData("success", true)]
    public async Task ProviderOutcomeCannotOverwriteTakenOverLease(string outcome, bool retainOwnership)
    {
        await using var f = await Fixture.CreateAsync();
        var message = ProviderDeliveryOutboxMessage.Create(f.VendorId, nameof(AnalyticsProvider.GoogleAnalytics),
            "fixture", "APP17D-provider", "hash", "{}", "fixture", endpointUrl: "https://provider.invalid/never-sent");
        f.Db.Set<ProviderDeliveryOutboxMessage>().Add(message);
        await f.Db.SaveChangesAsync();
        var sender = new Mock<IProviderDeliveryHttpSender>(MockBehavior.Strict);
        DateTime? otherLease = null;
        async Task TakeOver(CancellationToken ct)
        {
            await using var other = new NpgsqlConnection(f.ConnectionString); await other.OpenAsync(ct);
            await using var command = new NpgsqlCommand("UPDATE \"ProviderDeliveryOutbox\" SET \"ClaimedUntilUtc\"=clock_timestamp()+interval '10 minutes', \"UpdatedAt\"=clock_timestamp() WHERE \"Id\"=@id RETURNING \"ClaimedUntilUtc\"", other);
            command.Parameters.AddWithValue("id", message.Id);
            otherLease = (DateTime)(await command.ExecuteScalarAsync(ct))!;
        }
        sender.Setup(s => s.SendAsync(It.IsAny<ProviderDeliveryOutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProviderDeliveryOutboxMessage sent, CancellationToken ct) =>
            {
                if (!retainOwnership)
                {
                    await TakeOver(ct);
                }
                return outcome == "success" ? ProviderDeliverySendResult.Ok(204, "", TimeSpan.Zero)
                    : ProviderDeliverySendResult.Fail(outcome == "retry" ? 503 : 401, "fixture", "fixture", TimeSpan.Zero);
            });
        var breaker = new Mock<IProviderCircuitBreaker>(MockBehavior.Loose);
        breaker.Setup(b => b.CanAttemptAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        breaker.Setup(b => b.GetStateAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyticsProvider _, Guid? _, CancellationToken ct) =>
            {
                if (outcome == "pre-send") await TakeOver(ct);
                return new ProviderCircuitState(false, 0, null);
            });
        var services = new ServiceCollection();
        var reloadTakeover = new ProviderClaimTakeover(TakeOver);
        services.AddDbContext<ApplicationDbContext>(o =>
        {
            o.UseNpgsql(f.ConnectionString);
            if (outcome == "reload") o.AddInterceptors(reloadTakeover);
        });
        services.AddSingleton(sender.Object); services.AddSingleton(breaker.Object);
        services.AddSingleton<IProviderDeliveryRetryEngine, ProviderDeliveryRetryEngine>();
        await using var provider = services.BuildServiceProvider();
        using var metrics = new ProviderDeliveryMetrics();
        var count = await new ProviderDeliveryProcessor(provider, Options.Create(new ProviderDeliveryOptions()),
            NullLogger<ProviderDeliveryProcessor>.Instance, metrics).ProcessPendingMessagesAsync(1);
        Assert.Equal(retainOwnership ? 1 : 0, count);
        f.Db.ChangeTracker.Clear();
        var current = await f.Db.Set<ProviderDeliveryOutboxMessage>().IgnoreQueryFilters().SingleAsync();
        Assert.Equal(0, current.AttemptCount);
        Assert.False(current.IsFailed); Assert.False(current.IsDeadLettered);
        if (retainOwnership) { Assert.NotNull(current.DeliveredAtUtc); Assert.Null(current.ClaimedUntilUtc); }
        else
        {
            Assert.Null(current.DeliveredAtUtc); Assert.Equal(otherLease, current.ClaimedUntilUtc);
            Assert.Null(current.FailureClass); Assert.Null(current.NextRetryAtUtc);
            breaker.Verify(b => b.RecordSuccessAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
            breaker.Verify(b => b.RecordFailureAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<ProviderDeliveryOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        sender.Verify(s => s.SendAsync(It.IsAny<ProviderDeliveryOutboxMessage>(), It.IsAny<CancellationToken>()), outcome is "pre-send" or "reload" ? Times.Never() : Times.Once());
    }

    private sealed class ProviderClaimTakeover(Func<CancellationToken, Task> takeover) : DbCommandInterceptor
    {
        private bool _executed;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!_executed && command.Transaction is null && result == 1 && command.CommandText.StartsWith("UPDATE \"ProviderDeliveryOutbox\"", StringComparison.Ordinal))
            {
                _executed = true;
                await takeover(cancellationToken);
            }
            return result;
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!_executed && command.CommandText.Contains("\"PayloadJson\"", StringComparison.Ordinal))
            {
                _executed = true;
                await takeover(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task ProductCoordinationRejectsForeignTenantBeforeInsertOrLeaseMutation()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = f.ScopedDb(Guid.NewGuid());
        var request = new ProductMutationIdempotencyRequest(f.VendorId, "foreign", "APP17D", Guid.NewGuid(), null, "same");
        var service = new ProductMutationIdempotencyService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryClaimAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryRecoverAbandonedClaimAsync(request, TimeSpan.FromSeconds(1), DateTime.UtcNow));
        Assert.Empty(await db.ProductRequestIdempotencyRecords.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task ConcurrentProductClaimsHaveOneWinnerAndRejectFingerprintConflict()
    {
        await using var f = await Fixture.CreateAsync();
        var request = new ProductMutationIdempotencyRequest(f.VendorId, "quoted'العربية", "APP17D", Guid.NewGuid(), null, "fingerprint-A");
        async Task<ProductMutationIdempotencyClaim> Claim()
        {
            await using var db = f.ScopedDb(f.VendorId);
            return await new ProductMutationIdempotencyService(db).TryClaimAsync(request);
        }
        var results = await Task.WhenAll(Claim(), Claim());
        Assert.Single(results, r => r.Record != null);
        Assert.Single(results, r => r.Replay?.Kind == ProductMutationIdempotencyReplayKind.InProgress);
        await using var fresh = f.ScopedDb(f.VendorId);
        var conflict = await new ProductMutationIdempotencyService(fresh).TryClaimAsync(request with { RequestFingerprint = "fingerprint-B" });
        Assert.Equal(ProductMutationIdempotencyReplayKind.Conflict, conflict.Replay!.Kind);
        Assert.Equal(1, await fresh.ProductRequestIdempotencyRecords.CountAsync());
    }

    [Fact]
    public async Task ProductClaimTakeoverFencesStaleCompletionAndWrongTenant()
    {
        await using var f = await Fixture.CreateAsync();
        var request = new ProductMutationIdempotencyRequest(f.VendorId, "lease", "APP17D", Guid.NewGuid(), null, "same");
        await using var oldDb = f.ScopedDb(f.VendorId);
        var oldService = new ProductMutationIdempotencyService(oldDb);
        var old = (await oldService.TryClaimAsync(request)).Record!;
        await using var newDb = f.ScopedDb(f.VendorId);
        var newService = new ProductMutationIdempotencyService(newDb);
        var recovered = await newService.TryRecoverAbandonedClaimAsync(request, TimeSpan.FromSeconds(1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(ProductMutationIdempotencyRecoveryKind.Recovered, recovered.Kind);
        Assert.False(await oldService.TryRenewClaimLeaseAsync(old, DateTime.UtcNow.AddMinutes(2)));
        Assert.False(await oldService.TryWriteIncompleteClaimStateAsync(old, "{\"stale\":true}"));
        Assert.Equal(ProductMutationIdempotencyCompletionKind.LostOwnership,
            (await oldService.TryCompleteDurablyAsync(old, 200, new { stale = true })).Kind);
        await using var wrongDb = f.ScopedDb(Guid.NewGuid());
        Assert.Null(await new ProductMutationIdempotencyService(wrongDb).TryResolveAsync(request.TenantId, request.Key, request.Operation));
        Assert.Equal(ProductMutationIdempotencyCompletionKind.PersistedNow,
            (await newService.TryCompleteDurablyAsync(recovered.Record!, 201, new { current = true })).Kind);
        var replay = await newService.TryClaimAsync(request);
        Assert.Equal(ProductMutationIdempotencyReplayKind.Completed, replay.Replay!.Kind);
        Assert.Equal(201, replay.Replay.Record!.ResponseStatusCode);
    }

    [Fact]
    public async Task ProductClaimAndCompletionRollbackTogether()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = f.ScopedDb(f.VendorId);
        var request = new ProductMutationIdempotencyRequest(f.VendorId, "rollback", "APP17D", Guid.NewGuid(), null, "same");
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var service = new ProductMutationIdempotencyService(db);
            var record = (await service.TryClaimAsync(request)).Record!;
            Assert.Equal(ProductMutationIdempotencyCompletionKind.PersistedNow,
                (await service.TryCompleteDurablyAsync(record, 201, new { current = true })).Kind);
            await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Empty(await db.ProductRequestIdempotencyRecords.ToListAsync());
    }

    [Fact]
    public async Task FinancialCaptureWithoutTransactionRejectsBeforeAnyCoordinationWrite()
    {
        await using var f = await Fixture.CreateAsync();
        var guard = new CommerceHubFinancialSafetyGuardService(f.Db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => guard.CaptureStateForUpdateAsync(f.VendorId, "EGP"));
        Assert.Contains("active transaction", error.Message, StringComparison.Ordinal);
        Assert.Empty(await f.Db.Set<PointOfSale.Core.Entities.CommerceHubFinance.CommerceHubFinancialGuardLock>().ToListAsync());
        Assert.Empty(await f.Db.CommerceHubFinancialTransactions.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialCaptureHoldsVendorAndPlatformLocksUntilCallerRollback(bool platform)
    {
        await using var f = await Fixture.CreateAsync();
        var guard = new CommerceHubFinancialSafetyGuardService(f.Db);
        // Establish the global lock row in a separate committed transaction.
        await using (var seed = await f.Db.Database.BeginTransactionAsync())
        {
            await guard.CaptureStateForUpdateAsync(f.VendorId, "EGP");
            await seed.CommitAsync();
        }
        await using var tx = await f.Db.Database.BeginTransactionAsync();
        var state = await guard.CaptureStateForUpdateAsync(f.VendorId, "EGP");
        Assert.Equal(new CommerceHubFinancialStateSnapshot("EGP", 0m, 0m, 0m), state);
        await using var other = new NpgsqlConnection(f.ConnectionString);
        await other.OpenAsync();
        var sql = platform
            ? "SELECT \"Id\" FROM \"CommerceHubFinancialGuardLocks\" WHERE \"Scope\"='PlatformCash' FOR UPDATE NOWAIT"
            : "SELECT \"Id\" FROM \"Tenants\" WHERE \"Id\"=@id FOR UPDATE NOWAIT";
        await using (var competing = await other.BeginTransactionAsync())
        {
            await using var command = new NpgsqlCommand(sql, other, competing);
            if (!platform) command.Parameters.AddWithValue("id", f.VendorId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            await competing.RollbackAsync();
        }
        await tx.RollbackAsync();
        await using (var released = new NpgsqlCommand(sql, other))
        {
            if (!platform) released.Parameters.AddWithValue("id", f.VendorId);
            Assert.NotNull(await released.ExecuteScalarAsync());
        }
        Assert.Empty(await f.Db.CommerceHubFinancialTransactions.ToListAsync());
    }

    [Fact]
    public async Task UnknownVendorCannotProduceProtectedSnapshotAndCoordinationInsertRollsBack()
    {
        await using var f = await Fixture.CreateAsync();
        await using (var tx = await f.Db.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new CommerceHubFinancialSafetyGuardService(f.Db).CaptureStateForUpdateAsync(Guid.NewGuid(), "EGP"));
            await tx.RollbackAsync();
        }
        Assert.Empty(await f.Db.Set<PointOfSale.Core.Entities.CommerceHubFinance.CommerceHubFinancialGuardLock>().ToListAsync());
        Assert.Empty(await f.Db.CommerceHubFinancialTransactions.ToListAsync());
    }

    [Theory]
    [InlineData(false, "commit")]
    [InlineData(true, "commit")]
    [InlineData(false, "rollback")]
    [InlineData(true, "rollback")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task FinancialSnapshotAcquisitionUsesActualSharedOwnersAcrossVendorScopesAndCallerOutcomes(bool otherVendor, string outcome)
    {
        await using var f = await Fixture.CreateAsync();
        var contenderVendor = otherVendor ? Guid.NewGuid() : f.VendorId;
        if (otherVendor)
        {
            f.Db.Tenants.Add(new Tenant { Id = contenderVendor, Name = "Fictional second financial-guard tenant", Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
            await f.Db.SaveChangesAsync();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        var guard = new CommerceHubFinancialSafetyGuardService(f.Db);
        await using (var seed = await f.Db.Database.BeginTransactionAsync(ct))
        {
            await guard.CaptureStateForUpdateAsync(f.VendorId, "EGP", ct); await seed.CommitAsync(ct);
        }
        await f.Db.Database.OpenConnectionAsync(ct);
        await using var contender = f.ScopedDb(contenderVendor); await contender.Database.OpenConnectionAsync(ct);
        await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync(ct);
        await using var owner = await f.Db.Database.BeginTransactionAsync(ct);
        Assert.Equal(new CommerceHubFinancialStateSnapshot("EGP", 0m, 0m, 0m), await guard.CaptureStateForUpdateAsync(f.VendorId, "EGP", ct));
        await using var contenderTransaction = await contender.Database.BeginTransactionAsync(ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new CommerceHubFinancialSafetyGuardService(contender).CaptureStateForUpdateAsync(contenderVendor, "EGP", cancellation.Token);
        var ownerEnded = false; var contenderEnded = false;
        try
        {
            var holder = ((NpgsqlConnection)f.Db.Database.GetDbConnection()).ProcessID;
            var waiter = ((NpgsqlConnection)contender.Database.GetDbConnection()).ProcessID;
            var blocked = false; var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(6) && !pending.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", observer);
                probe.Parameters.AddWithValue("holder", holder); probe.Parameters.AddWithValue("waiter", waiter);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                await Task.Delay(20, ct);
            }
            var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
            await File.WriteAllTextAsync(Path.Combine(output, "financial-guard-held-owner-" + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(new { otherVendor, outcome, Holder = holder, Waiter = waiter, ExactBackendBlockerObserved = blocked,
                    ContenderPending = !pending.IsCompleted, Scope = "Actual PostgreSQL zero-valued safety snapshots only; no financial posting, payout, external operation or actor-role approval" }), ct);
            Assert.True(blocked, "Snapshot acquisition must wait on the actual independent financial-safety owner."); Assert.False(pending.IsCompleted);
            if (outcome == "cancel")
            {
                cancellation.Cancel(); var error = await Record.ExceptionAsync(async () => await pending);
                Assert.IsAssignableFrom<OperationCanceledException>(error);
                await contenderTransaction.RollbackAsync(CancellationToken.None); contenderEnded = true;
            }
            if (outcome == "commit") await owner.CommitAsync(ct); else await owner.RollbackAsync(ct);
            ownerEnded = true;
            if (outcome != "cancel")
            {
                Assert.Equal(new CommerceHubFinancialStateSnapshot("EGP", 0m, 0m, 0m), await pending);
                await contenderTransaction.RollbackAsync(ct); contenderEnded = true;
            }
            await contenderTransaction.DisposeAsync();
            Assert.Null(contender.Database.CurrentTransaction);
            await using (var retry = await contender.Database.BeginTransactionAsync(ct))
            {
                Assert.Equal(new CommerceHubFinancialStateSnapshot("EGP", 0m, 0m, 0m),
                    await new CommerceHubFinancialSafetyGuardService(contender).CaptureStateForUpdateAsync(contenderVendor, "EGP", ct));
                await retry.RollbackAsync(ct);
            }
            await using var fresh = f.ScopedDb(f.VendorId);
            Assert.Single(await fresh.Set<PointOfSale.Core.Entities.CommerceHubFinance.CommerceHubFinancialGuardLock>().AsNoTracking().ToListAsync(ct));
            Assert.Empty(await fresh.CommerceHubFinancialTransactions.ToListAsync(ct)); Assert.Empty(await fresh.CommerceHubFinancialOperationAudits.ToListAsync(ct));
            Assert.Empty(await fresh.JournalEntries.IgnoreQueryFilters().ToListAsync(ct)); Assert.Empty(await fresh.LedgerEntries.IgnoreQueryFilters().ToListAsync(ct));
            Assert.Empty(await fresh.OutboxMessages.ToListAsync(ct)); Assert.Empty(await fresh.AccountingOutboxMessages.ToListAsync(ct));
        }
        finally
        {
            if (!ownerEnded) await owner.RollbackAsync(CancellationToken.None);
            try { await pending; } catch { /* Drain owned waiter while preserving original assertion/provider failure. */ }
            if (!contenderEnded) await contenderTransaction.RollbackAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutboxOwnerCannotAdoptAnotherCommittedClaimBetweenCommitAndReload(bool stock)
    {
        await using var f = await Fixture.CreateAsync();
        Guid id;
        if (stock)
        {
            var evt = new ProductVariantStockChangedEvent
            {
                EventId = Guid.NewGuid(), TenantId = f.VendorId, ProductId = Guid.NewGuid(), VariantId = Guid.NewGuid(),
                WarehouseId = Guid.NewGuid(), SKU = "APP17D", OldQuantity = 2m, NewQuantity = 1m, TotalStockQuantity = 1m,
                Reason = "Fixture", CorrelationId = Guid.NewGuid().ToString("N"), OccurredAtUtc = DateTime.UtcNow.AddSeconds(-5), StockUpdatedAtUtc = DateTime.UtcNow
            };
            var message = StockSyncOutboxMessage.Create(evt, JsonSerializer.Serialize(evt));
            id = message.Id; f.Db.StockSyncOutboxMessages.Add(message);
        }
        else
        {
            var evt = new OrderCreatedEvent
            {
                EventId = Guid.NewGuid(), TenantId = f.VendorId, EntityId = Guid.NewGuid(), Version = 1,
                CorrelationId = Guid.NewGuid().ToString("N"), OccurredAtUtc = DateTime.UtcNow.AddSeconds(-5),
                OrderNumber = "APP17D", TotalAmount = 50m, SubtotalAmount = 50m, Currency = "EGP", Items = []
            };
            var message = MarketplaceEventOutboxMessage.Create(evt, JsonSerializer.Serialize(evt));
            id = message.Id; f.Db.Set<MarketplaceEventOutboxMessage>().Add(message);
        }
        await f.Db.SaveChangesAsync();
        var table = f.Db.Model.FindEntityType(stock ? typeof(StockSyncOutboxMessage) : typeof(MarketplaceEventOutboxMessage))!.GetTableName()!;
        var interpose = new ClaimTakeover(f.ConnectionString, table, id);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(f.ConnectionString).AddInterceptors(interpose));
        services.AddScoped(_ => publisher.Object);
        await using var provider = services.BuildServiceProvider();
        using var telemetry = new CommerceReliabilityTelemetry();
        var count = stock
            ? await new StockSyncOutboxProcessor(provider, telemetry, NullLogger<StockSyncOutboxProcessor>.Instance, Options.Create(new StockSyncOutboxOptions())).ProcessPendingMessagesAsync()
            : await new MarketplaceEventOutboxProcessor(provider, NullLogger<MarketplaceEventOutboxProcessor>.Instance, Options.Create(new MarketplaceEventBackboneOptions())).ProcessPendingMessagesAsync();
        Assert.True(interpose.Executed);
        Assert.Equal(0, count);
        publisher.VerifyNoOtherCalls();
        f.Db.ChangeTracker.Clear();
        if (stock)
        {
            var current = await f.Db.StockSyncOutboxMessages.IgnoreQueryFilters().SingleAsync(m => m.Id == id);
            Assert.Null(current.PublishedAtUtc); Assert.NotNull(current.ClaimedUntilUtc); Assert.Equal(0, current.AttemptCount);
        }
        else
        {
            var current = await f.Db.Set<MarketplaceEventOutboxMessage>().SingleAsync(m => m.Id == id);
            Assert.Null(current.PublishedAtUtc); Assert.NotNull(current.ClaimedUntilUtc); Assert.Equal(0, current.AttemptCount);
        }
    }

    private sealed class ClaimTakeover(string connection, string table, Guid id) : DbTransactionInterceptor
    {
        public bool Executed { get; private set; }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Executed) return;
            Executed = true;
            await using var sql = new NpgsqlConnection(connection); await sql.OpenAsync(cancellationToken);
            // Simulate a different committed owner before the old worker reloads.
            await using var command = new NpgsqlCommand($"UPDATE \"{table}\" SET \"ClaimedUntilUtc\"=clock_timestamp()+interval '10 minutes', \"UpdatedAt\"=clock_timestamp() WHERE \"Id\"=@id", sql);
            command.Parameters.AddWithValue("id", id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public ApplicationDbContext Db { get; private set; } = null!;
        public Guid VendorId { get; } = Guid.NewGuid();
        public string ConnectionString { get; private set; } = null!;
        private string Schema { get; } = "app17d_coord_" + Guid.NewGuid().ToString("N");
        private List<object> Evidence { get; } = [];
        public ApplicationDbContext ScopedDb(Guid tenantId) => new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(ConnectionString).Options,
            new PointOfSale.Tests.Infrastructure.TestTenantContext(tenantId));
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("APP17D_POSTGRES") ?? throw new InvalidOperationException("Disposable PostgreSQL18 required")) { SearchPath = f.Schema, Pooling = false };
            if (cs.Host != "app17c-pg" || cs.Database != "app17c_fixture" || cs.Username != "app17c_fixture")
                throw new InvalidOperationException("Refusing nonfixture SQL");
            f.ConnectionString = cs.ConnectionString;
            f.Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
            try
            {
                await f.Db.Database.OpenConnectionAsync();
                Assert.StartsWith("18.", f.Db.Database.GetDbConnection().ServerVersion);
                await using var cmd = f.Db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "CREATE SCHEMA \"" + f.Schema + "\""; await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = f.Db.Database.GenerateCreateScript(); cmd.CommandTimeout = 60; await cmd.ExecuteNonQueryAsync();
                f.Evidence.Add(new { operation = "model-derived-fixture-DDL", sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cmd.CommandText))), productionMigrationExecuted = false });
                f.Db.Tenants.Add(new Tenant { Id = f.VendorId, Name = "APP17D fixture", Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
                await f.Db.SaveChangesAsync();
                await using var plan = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) SELECT \"Id\" FROM \"Tenants\" WHERE \"Id\"=@id", (NpgsqlConnection)f.Db.Database.GetDbConnection());
                plan.Parameters.AddWithValue("id", f.VendorId);
                f.Evidence.Add(new { operation = "vendor-lock-identity-query-plan", sql = plan.CommandText, vendorId = f.VendorId, response = (string)(await plan.ExecuteScalarAsync())! });
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await using var sql = new NpgsqlConnection(ConnectionString); await sql.OpenAsync();
            await using var cmd = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"" + Schema + "\" CASCADE", sql) { CommandTimeout = 60 };
            await cmd.ExecuteNonQueryAsync();
            Evidence.Add(new { operation = "owned-schema-cleanup", schema = Schema, completed = true });
            var directory = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
            await File.WriteAllTextAsync(Path.Combine(directory, Schema + ".json"), JsonSerializer.Serialize(Evidence));
        }
    }
}
