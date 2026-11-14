using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using MassTransit;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.Infrastructure.EventBus;
using PointOfSale.Infrastructure.Eventing;
using SqlBusOutboxMessage = MassTransit.EntityFrameworkCoreIntegration.OutboxMessage;
using PointOfSale.API.Infrastructure.Idempotency;
using PointOfSale.API.Services;
using PointOfSale.API.Services.ProductAuthoring;
using PointOfSale.Core.Application.ProductAuthoring;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Marketplace;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Integration;

public sealed class ProductAuthoringIdempotencyIntegrationTests
{
    [Fact]
    public async Task SameKeySamePayload_PersistsOneProductOneProductCreatedOutboxOneAuditAndReplays()
    {
        await using var harness = await Harness.TryCreateAsync();

        var command = harness.CreateCreateCommand("same-key-same-payload");

        var first = await harness.CreateOrchestrator().AuthorAsync(command);
        harness.Clear();
        var replay = await harness.CreateOrchestrator().AuthorAsync(command);

        Assert.Equal(ProductAuthoringStatus.Accepted, first.Status);
        Assert.Equal(first.Status, replay.Status);
        Assert.Equal(first.ProductId, replay.ProductId);
        Assert.Equal(first.MutationVersion, replay.MutationVersion);
        Assert.Equal(first.PublicationState, replay.PublicationState);
        Assert.Equal(first.CorrelationId, replay.CorrelationId);
        Assert.Empty(replay.ValidationErrors);
        Assert.Single(await harness.CountProductsAsync());
        Assert.Equal(1, await harness.CountProductCreatedOutboxRowsAsync());
        Assert.Equal(1, await harness.CountProductAuthoringAuditRowsAsync());
        Assert.Equal(1, await harness.CountProductIdempotencyRowsAsync("same-key-same-payload"));
        Assert.Equal("idempotent-product", (await harness.CountProductsAsync()).Single().Slug);
    }

    [Fact]
    public async Task SameKeyDifferentPayload_ReturnsConflictAndDoesNotMutateAgain()
    {
        await using var harness = await Harness.TryCreateAsync();

        var firstCommand = harness.CreateCreateCommand("same-key-different-payload");
        var conflictingCommand = firstCommand with
        {
            Content = firstCommand.Content with { Name = "Changed product name" }
        };

        var first = await harness.CreateOrchestrator().AuthorAsync(firstCommand);
        harness.Clear();
        var conflict = await harness.CreateOrchestrator().AuthorAsync(conflictingCommand);

        Assert.Equal(ProductAuthoringStatus.Accepted, first.Status);
        Assert.Equal(ProductAuthoringStatus.ValidationFailed, conflict.Status);
        Assert.Contains(conflict.ValidationErrors, error => error.Code == "PRODUCT_AUTHORING_IDEMPOTENCY_CONFLICT");
        Assert.Single(await harness.CountProductsAsync());
        Assert.Equal(1, await harness.CountProductCreatedOutboxRowsAsync());
        Assert.Equal(1, await harness.CountProductAuthoringAuditRowsAsync());
    }

    [Fact]
    public async Task DifferentKeysSamePayload_CreateIndependentOperations()
    {
        await using var harness = await Harness.TryCreateAsync();

        var firstCommand = harness.CreateCreateCommand("different-key-one");
        var secondCommand = firstCommand with
        {
            Metadata = firstCommand.Metadata with
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                IdempotencyKey = "different-key-two"
            }
        };

        var first = await harness.CreateOrchestrator().AuthorAsync(firstCommand);
        var second = await harness.CreateOrchestrator().AuthorAsync(secondCommand);

        Assert.Equal(ProductAuthoringStatus.Accepted, first.Status);
        Assert.Equal(ProductAuthoringStatus.Accepted, second.Status);
        Assert.NotEqual(first.ProductId, second.ProductId);
        Assert.Equal(2, (await harness.CountProductsAsync()).Count);
        Assert.Equal(2, await harness.CountProductCreatedOutboxRowsAsync());
        Assert.Equal(2, await harness.CountProductAuthoringAuditRowsAsync());
    }

    [Fact]
    public async Task LostHttpResponseAfterCommit_RetryReplaysWithoutDuplicateMutation()
    {
        var observer = new ThrowAfterCommitObserver();
        await using var harness = await Harness.TryCreateAsync(observer);

        var command = harness.CreateCreateCommand("lost-response-after-commit");

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateOrchestrator().AuthorAsync(command));
        observer.Disable();
        harness.Clear();

        var replay = await harness.CreateOrchestrator().AuthorAsync(command);

        Assert.Equal(ProductAuthoringStatus.Accepted, replay.Status);
        Assert.Single(await harness.CountProductsAsync());
        Assert.Equal(1, await harness.CountProductCreatedOutboxRowsAsync());
        Assert.Equal(1, await harness.CountProductAuthoringAuditRowsAsync());
    }

    [Fact]
    public async Task ConcurrentRetries_WithSameKeySamePayload_CreateExactlyOneMutation()
    {
        await using var harness = await Harness.TryCreateAsync();

        var command = harness.CreateCreateCommand("concurrent-retry-key");

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Task.Run(async () =>
            {
                await using var scopedHarness = harness.CreateSiblingHarness();
                return await scopedHarness.CreateOrchestrator().AuthorAsync(command);
            })));

        Assert.Contains(results, result => result.Status == ProductAuthoringStatus.Accepted);
        Assert.All(results, result => Assert.Contains(
            result.Status,
            new[] { ProductAuthoringStatus.Accepted, ProductAuthoringStatus.ConcurrencyConflict }));
        Assert.Single(await harness.CountProductsAsync());
        Assert.Equal(1, await harness.CountProductCreatedOutboxRowsAsync());
        Assert.Equal(1, await harness.CountProductAuthoringAuditRowsAsync());

        harness.Clear();
        var replay = await harness.CreateOrchestrator().AuthorAsync(command);
        Assert.Equal(ProductAuthoringStatus.Accepted, replay.Status);
    }

    [Fact]
    public async Task ConcurrentCreates_WithDifferentKeysAndSameBaseSlug_PersistDistinctSlugs()
    {
        await using var harness = await Harness.TryCreateAsync();

        var firstCommand = harness.CreateCreateCommand("concurrent-slug-one");
        var secondCommand = firstCommand with
        {
            Metadata = firstCommand.Metadata with
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                IdempotencyKey = "concurrent-slug-two"
            }
        };

        var results = await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var scopedHarness = harness.CreateSiblingHarness();
                return await scopedHarness.CreateOrchestrator().AuthorAsync(firstCommand);
            }),
            Task.Run(async () =>
            {
                await using var scopedHarness = harness.CreateSiblingHarness();
                return await scopedHarness.CreateOrchestrator().AuthorAsync(secondCommand);
            }));

        Assert.All(results, result => Assert.Equal(ProductAuthoringStatus.Accepted, result.Status));
        var slugs = (await harness.CountProductsAsync())
            .Select(product => Assert.IsType<string>(product.Slug))
            .OrderBy(slug => slug, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["idempotent-product", "idempotent-product-2"], slugs);
    }

    [Fact]
    public async Task Create_WhenHistoricalSlugDiffersOnlyByCase_AppendsCollisionSuffix()
    {
        await using var harness = await Harness.TryCreateAsync();

        await harness.SeedHistoricalProductAsync("Idempotent-Product");

        var result = await harness.CreateOrchestrator().AuthorAsync(
            harness.CreateCreateCommand("mixed-case-slug"));

        Assert.Equal(ProductAuthoringStatus.Accepted, result.Status);
        var created = (await harness.CountProductsAsync()).Single(product => product.Id == result.ProductId);
        Assert.Equal("idempotent-product-2", created.Slug);
    }

    [Fact]
    public async Task Create_SameSlugInDifferentTenants_DoesNotCreateCrossTenantCollision()
    {
        await using var firstTenant = await Harness.TryCreateAsync();
        await using var secondTenant = await firstTenant.CreateAdditionalTenantHarnessAsync(Guid.NewGuid());

        var first = await firstTenant.CreateOrchestrator().AuthorAsync(
            firstTenant.CreateCreateCommand("tenant-one-slug"));
        var second = await secondTenant.CreateOrchestrator().AuthorAsync(
            secondTenant.CreateCreateCommand("tenant-two-slug"));

        Assert.Equal(ProductAuthoringStatus.Accepted, first.Status);
        Assert.Equal(ProductAuthoringStatus.Accepted, second.Status);
        Assert.Equal("idempotent-product", (await firstTenant.CountProductsAsync()).Single().Slug);
        Assert.Equal("idempotent-product", (await secondTenant.CountProductsAsync()).Single().Slug);
    }

    [Fact]
    public async Task Update_WithBlankSlugAndChangedName_PreservesStoredSlug()
    {
        await using var harness = await Harness.TryCreateAsync();

        var createCommand = harness.CreateCreateCommand("stable-slug-create");
        var createResult = await harness.CreateOrchestrator().AuthorAsync(createCommand);
        Assert.Equal(ProductAuthoringStatus.Accepted, createResult.Status);

        harness.Clear();
        var stored = (await harness.CountProductsAsync()).Single();
        var updateCommand = createCommand with
        {
            Operation = ProductAuthoringOperation.Update,
            Identity = createCommand.Identity with { ProductId = stored.Id },
            Content = createCommand.Content with { Name = "Renamed product" },
            Metadata = createCommand.Metadata with
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                IdempotencyKey = "stable-slug-update",
                ExpectedVersion = stored.RowVersion
            }
        };

        var updateResult = await harness.CreateOrchestrator().AuthorAsync(updateCommand);

        Assert.Equal(ProductAuthoringStatus.Accepted, updateResult.Status);
        harness.Clear();
        var updated = (await harness.CountProductsAsync()).Single();
        Assert.Equal("Renamed product", updated.Name);
        Assert.Equal("idempotent-product", updated.Slug);
    }

    [Fact]
    public async Task MissingIdempotencyKey_IsRejectedBeforeMutation()
    {
        await using var harness = await Harness.TryCreateAsync();

        var command = harness.CreateCreateCommand(null);

        var result = await harness.CreateOrchestrator().AuthorAsync(command);

        Assert.Equal(ProductAuthoringStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Code == "PRODUCT_AUTHORING_IDEMPOTENCY_KEY_REQUIRED");
        Assert.Empty(await harness.CountProductsAsync());
        Assert.Equal(0, await harness.CountProductCreatedOutboxRowsAsync());
        Assert.Equal(0, await harness.CountProductAuthoringAuditRowsAsync());
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("cancel-wait")]
    public async Task ActualAuthoringSlugOwnerBlocksTheExactCompetingBackendUntilCommitRollbackOrCanceledAdmission(string outcome)
    {
        var pause = new HeldCommitObserver(outcome == "rollback");
        await using var harness = await Harness.TryCreateAsync(pause);
        await using var contender = harness.CreateSiblingHarness(NoOpProductAuthoringTransactionObserver.Instance);
        await using var observer = harness.CreateSiblingHarness(NoOpProductAuthoringTransactionObserver.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await harness.DatabaseContext.Database.OpenConnectionAsync(ct); await contender.DatabaseContext.Database.OpenConnectionAsync(ct);
        await observer.DatabaseContext.Database.OpenConnectionAsync(ct);
        var firstCommand = harness.CreateCreateCommand("held-slug-first");
        var secondCommand = contender.CreateCreateCommand("held-slug-second");
        var first = harness.CreateOrchestrator().AuthorAsync(firstCommand, ct);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<ProductAuthoringResult>? second = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
            var holder = ((NpgsqlConnection)harness.DatabaseContext.Database.GetDbConnection()).ProcessID;
            var waiter = ((NpgsqlConnection)contender.DatabaseContext.Database.GetDbConnection()).ProcessID;
            second = contender.CreateOrchestrator().AuthorAsync(secondCommand, waitCancellation.Token);
            var blocked = false; var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8) && !second.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))",
                    (NpgsqlConnection)observer.DatabaseContext.Database.GetDbConnection());
                probe.Parameters.AddWithValue("holder", holder); probe.Parameters.AddWithValue("waiter", waiter);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                await Task.Delay(20, ct);
            }
            var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
            await File.WriteAllTextAsync(Path.Combine(output, "product-authoring-held-slug-" + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(new { outcome, Holder = holder, Waiter = waiter, ExactBackendBlockerObserved = blocked,
                    FirstPending = !first.IsCompleted, SecondPending = !second.IsCompleted,
                    Scope = "Actual AuthorAsync tenant advisory lock and local SQL audit/recorded outbox; not canonical publisher, deployed role or delivery proof" }), ct);
            Assert.True(blocked, "The competing actual AuthorAsync must wait on the exact admitted PostgreSQL backend.");
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
            Assert.Empty(await observer.CountProductsAsync()); Assert.Equal(0, await observer.CountProductCreatedOutboxRowsAsync());
            Assert.Equal(0, await observer.CountProductAuthoringAuditRowsAsync());
            if (outcome == "cancel-wait")
            {
                waitCancellation.Cancel(); var cancellation = await Record.ExceptionAsync(async () => await second);
                Assert.IsAssignableFrom<OperationCanceledException>(cancellation); Assert.Null(contender.DatabaseContext.Database.CurrentTransaction);
                Assert.Empty(await observer.CountProductsAsync()); Assert.Equal(0, await observer.CountProductIdempotencyRowsAsync("held-slug-second"));
            }
            pause.Release.TrySetResult();
            if (outcome == "rollback") Assert.IsType<InvalidOperationException>(await Record.ExceptionAsync(async () => await first));
            else Assert.Equal(ProductAuthoringStatus.Accepted, (await first).Status);
            var secondResult = outcome == "cancel-wait"
                ? await contender.CreateOrchestrator().AuthorAsync(secondCommand, ct)
                : await second;
            Assert.Equal(ProductAuthoringStatus.Accepted, secondResult.Status);
            var products = await observer.CountProductsAsync(); var expected = outcome == "rollback" ? 1 : 2;
            Assert.Equal(expected, products.Count); Assert.Equal(expected, await observer.CountProductCreatedOutboxRowsAsync());
            Assert.Equal(expected, await observer.CountProductAuthoringAuditRowsAsync());
            Assert.Equal(outcome == "rollback" ? 0 : 1, await observer.CountProductIdempotencyRowsAsync("held-slug-first"));
            Assert.Equal(1, await observer.CountProductIdempotencyRowsAsync("held-slug-second"));
            var slugs = products.Select(p => Assert.IsType<string>(p.Slug)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(outcome == "rollback" ? ["idempotent-product"] : new[] { "idempotent-product", "idempotent-product-2" }, slugs);
            Assert.Null(harness.DatabaseContext.Database.CurrentTransaction); Assert.Null(contender.DatabaseContext.Database.CurrentTransaction);
            Assert.Empty(await observer.DatabaseContext.JournalEntries.IgnoreQueryFilters().ToListAsync(ct));
            Assert.Empty(await observer.DatabaseContext.LedgerEntries.IgnoreQueryFilters().ToListAsync(ct));
            Assert.Empty(await observer.DatabaseContext.AccountingOutboxMessages.ToListAsync(ct));
            await File.WriteAllTextAsync(Path.Combine(output, "product-authoring-held-slug-result-" + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(new { outcome, ProductCount = products.Count, Slugs = slugs, AuditCount = await observer.CountProductAuthoringAuditRowsAsync(),
                    RecordedProductCreatedOutboxCount = await observer.CountProductCreatedOutboxRowsAsync(), CanceledAdmissionDisposed = contender.DatabaseContext.Database.CurrentTransaction == null }), ct);
        }
        finally
        {
            pause.Release.TrySetResult(); try { await first; } catch { /* Retain original failure while draining owned command. */ }
            if (second != null) { try { await second; } catch { /* Drain exact owned contender. */ } }
        }
    }

    private sealed class HeldCommitObserver(bool abort) : IProductAuthoringTransactionObserver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task AfterUnitSynchronizationAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AfterVariantSynchronizationAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AfterCommitAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task BeforeCommitAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            if (abort) throw new InvalidOperationException("Fictional authoring observer abort before SQL save/commit.");
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("observer-abort")]
    [InlineData("sql-abort")]
    [InlineData("lost-response")]
    public async Task CanonicalBusAndMarketplaceOutboxesShareAuthoringCommitRollbackAndFreshScopeReplay(string outcome)
    {
        var fault = new CanonicalOutboxSqlFailure(outcome == "sql-abort");
        var observer = new CanonicalCommitObserver(outcome);
        await using var harness = await Harness.TryCreateAsync(observer, fault);
        await using var read = harness.CreateSiblingHarness(NoOpProductAuthoringTransactionObserver.Instance);
        harness.EnableCanonicalPublisher();
        observer.BeforeProbe = async () =>
        {
            Assert.Empty(await read.CountProductsAsync()); Assert.Equal(0, await read.CountProductAuthoringAuditRowsAsync());
            Assert.Empty(await read.DatabaseContext.Set<SqlBusOutboxMessage>().AsNoTracking().ToListAsync());
            Assert.Empty(await read.DatabaseContext.Set<MarketplaceEventOutboxMessage>().AsNoTracking().ToListAsync());
        };
        var command = harness.CreateCreateCommand("canonical-authoring-outboxes");
        ProductAuthoringResult? first = null;
        var error = await Record.ExceptionAsync(async () => first = await harness.CreateOrchestrator().AuthorAsync(command));
        var beforeProducts = await read.CountProductsAsync();
        var beforeBus = await read.DatabaseContext.Set<SqlBusOutboxMessage>().AsNoTracking().ToListAsync();
        var beforeMarketplace = await read.DatabaseContext.Set<MarketplaceEventOutboxMessage>().AsNoTracking().ToListAsync();
        var beforeAudits = await read.CountProductAuthoringAuditRowsAsync();
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
        await File.WriteAllTextAsync(Path.Combine(output, "product-authoring-canonical-before-retry-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new { outcome, Error = error?.GetType().Name, Result = first?.Status.ToString(), observer.Probed, fault.SqlState, fault.Attempts,
                ProductCount = beforeProducts.Count, BusOutboxCount = beforeBus.Count, MarketplaceOutboxCount = beforeMarketplace.Count, AuditCount = beforeAudits,
                Scope = "Actual MassTransit EF bus outbox and production publishers, no bus start/host/remote delivery/financial posting" }));
        var committed = outcome is "success" or "lost-response";
        if (outcome == "success") { Assert.Null(error); Assert.Equal(ProductAuthoringStatus.Accepted, first!.Status); }
        else Assert.NotNull(error);
        if (outcome == "sql-abort") { Assert.Equal("P0001", fault.SqlState); Assert.True(fault.Attempts > 0); }
        else Assert.True(observer.Probed);
        Assert.Equal(committed ? 1 : 0, beforeProducts.Count); Assert.Equal(committed ? 1 : 0, beforeAudits);
        if (committed) { Assert.NotEmpty(beforeBus); Assert.Single(beforeMarketplace); }
        else { Assert.Empty(beforeBus); Assert.Empty(beforeMarketplace); }
        fault.Disable();
        await using var retry = harness.CreateSiblingHarness(NoOpProductAuthoringTransactionObserver.Instance); retry.EnableCanonicalPublisher();
        var replay = await retry.CreateOrchestrator().AuthorAsync(command);
        Assert.Equal(ProductAuthoringStatus.Accepted, replay.Status);
        var products = await read.CountProductsAsync(); var product = Assert.Single(products);
        var bus = await read.DatabaseContext.Set<SqlBusOutboxMessage>().AsNoTracking().ToListAsync();
        var marketplace = await read.DatabaseContext.Set<MarketplaceEventOutboxMessage>().AsNoTracking().ToListAsync();
        Assert.NotEmpty(bus); Assert.Single(marketplace); Assert.Equal(1, await read.CountProductAuthoringAuditRowsAsync());
        Assert.Equal(1, await read.CountProductIdempotencyRowsAsync("canonical-authoring-outboxes"));
        var created = Assert.Single(bus, message => message.MessageType.Contains(nameof(ProductCreatedEvent), StringComparison.Ordinal));
        Assert.Contains(product.Id.ToString(), created.Body); Assert.Contains(command.Metadata.CorrelationId, created.Body);
        if (committed)
        {
            Assert.Equal(beforeBus.Select(m => m.MessageId).OrderBy(id => id).ToArray(), bus.Select(m => m.MessageId).OrderBy(id => id).ToArray());
            Assert.Equal(Assert.Single(beforeMarketplace).Id, Assert.Single(marketplace).Id);
        }
        Assert.Empty(await read.DatabaseContext.OutboxMessages.AsNoTracking().ToListAsync());
        Assert.Empty(await read.DatabaseContext.JournalEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await read.DatabaseContext.LedgerEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await read.DatabaseContext.AccountingOutboxMessages.ToListAsync());
        await File.WriteAllTextAsync(Path.Combine(output, "product-authoring-canonical-result-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new { outcome, ProductId = product.Id, ProductCount = products.Count, AuditCount = await read.CountProductAuthoringAuditRowsAsync(),
                BusMessages = bus.Select(m => new { m.MessageId, m.MessageType, m.OutboxId }), MarketplaceOutboxCount = marketplace.Count,
                RetainedCommittedMessageIds = committed, Publisher = nameof(MassTransitEventPublisher), BusStarted = false, RemoteDeliveryCertified = false }));
    }

    private sealed class CanonicalOutboxSqlFailure(bool enabled) : SaveChangesInterceptor
    {
        private bool _enabled = enabled;
        public int Attempts { get; private set; }
        public string? SqlState { get; private set; }
        public void Disable() => _enabled = false;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_enabled && data.Context!.ChangeTracker.Entries<SqlBusOutboxMessage>().Any(e => e.State == EntityState.Added))
            {
                Attempts++;
                try { await data.Context.Database.ExecuteSqlRawAsync("DO $$ BEGIN RAISE EXCEPTION USING ERRCODE='P0001', MESSAGE='APP2 fictional authoring bus outbox SQL abort'; END $$;", cancellationToken); }
                catch (PostgresException ex) { SqlState = ex.SqlState; throw; }
                throw new InvalidOperationException("Actual PostgreSQL outbox abort must not succeed.");
            }
            return result;
        }
    }

    private sealed class CanonicalCommitObserver(string outcome) : IProductAuthoringTransactionObserver
    {
        public Func<Task>? BeforeProbe { get; set; }
        public bool Probed { get; private set; }
        public Task AfterUnitSynchronizationAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AfterVariantSynchronizationAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task BeforeCommitAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken)
        {
            await (BeforeProbe?.Invoke() ?? Task.CompletedTask); Probed = true;
            if (outcome == "observer-abort") throw new InvalidOperationException("Fictional canonical authoring observer abort before commit.");
        }
        public Task AfterCommitAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken)
        {
            if (outcome == "lost-response") throw new InvalidOperationException("Fictional lost canonical authoring response after commit.");
            return Task.CompletedTask;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly PostgresScope? _scope;
        private readonly DbContextOptions<ApplicationDbContext> _options;
        private readonly IProductAuthoringTransactionObserver _observer;
        private ServiceProvider? _canonicalProvider;
        private AsyncServiceScope? _canonicalScope;
        private IEventPublisher? _canonicalPublisher;

        private Harness(
            PostgresScope? scope,
            DbContextOptions<ApplicationDbContext> options,
            ApplicationDbContext context,
            TestTenantContext tenantContext,
            Guid categoryId,
            Guid unitId,
            IProductAuthoringTransactionObserver observer)
        {
            _scope = scope;
            _options = options;
            Context = context;
            TenantContext = tenantContext;
            CategoryId = categoryId;
            UnitId = unitId;
            _observer = observer;
        }

        private ApplicationDbContext Context { get; }
        public ApplicationDbContext DatabaseContext => Context;
        private TestTenantContext TenantContext { get; }
        private Guid CategoryId { get; }
        private Guid UnitId { get; }

        public static async Task<Harness> TryCreateAsync(IProductAuthoringTransactionObserver? observer = null, SaveChangesInterceptor? interceptor = null)
        {
            var scope = await PostgresScope.TryCreateAsync();
            var tenantId = scope.TenantId;
            var tenantContext = new TestTenantContext(tenantId);
            var options = interceptor == null ? scope.Options : new DbContextOptionsBuilder<ApplicationDbContext>(scope.Options).AddInterceptors(interceptor).Options;
            var context = new ApplicationDbContext(options, tenantContext);
            var category = new Category
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Idempotency"
            };
            var unit = new PointOfSale.Core.Entities.Unit
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Piece",
                Symbol = "pc",
                IsActive = true
            };

            context.Categories.Add(category);
            context.Units.Add(unit);
            await context.SaveChangesAsync();

            return new Harness(
                scope,
                options,
                context,
                tenantContext,
                category.Id,
                unit.Id,
                observer ?? NoOpProductAuthoringTransactionObserver.Instance);
        }

        public Harness CreateSiblingHarness(IProductAuthoringTransactionObserver? observer = null)
        {
            if (_scope is null)
            {
                throw new InvalidOperationException("A persisted test scope is required.");
            }

            return new Harness(
                null,
                _options,
                new ApplicationDbContext(_options, TenantContext),
                TenantContext,
                CategoryId,
                UnitId,
                observer ?? _observer);
        }

        public async Task<Harness> CreateAdditionalTenantHarnessAsync(Guid tenantId)
        {
            var tenantContext = new TestTenantContext(tenantId);
            var context = new ApplicationDbContext(_options, tenantContext);
            context.Tenants.Add(new Tenant { Id = tenantId, Name = "Disposable additional authoring tenant",
                Status = TenantStatus.Active, IsActive = true, Currency = "EGP" });
            var category = new Category
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Additional tenant"
            };
            var unit = new PointOfSale.Core.Entities.Unit
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Piece",
                Symbol = "pc",
                IsActive = true
            };
            context.Categories.Add(category);
            context.Units.Add(unit);
            await context.SaveChangesAsync();

            return new Harness(
                null,
                _options,
                context,
                tenantContext,
                category.Id,
                unit.Id,
                _observer);
        }

        public ProductAuthoringOrchestrator CreateOrchestrator()
        {
            IEventPublisher eventPublisher = _canonicalPublisher ?? new PersistedOutboxEventPublisher(Context);
            return new ProductAuthoringOrchestrator(
                new ProductAuthoringPolicy(new ProductAuthoringValidator()),
                Context,
                TenantContext,
                new ProductVariantService(Context, eventPublisher, NullLogger<ProductVariantService>.Instance),
                eventPublisher,
                _canonicalPublisher != null ? new MarketplaceEventOutboxService(Context, NullLogger<MarketplaceEventOutboxService>.Instance) : new NoOpMarketplaceEventOutbox(),
                NullLogger<ProductAuthoringOrchestrator>.Instance,
                new HttpContextAccessor(),
                new ProductMutationIdempotencyService(Context),
                _observer,
                new ProductAuthoringReconstructionService(Context, TenantContext));
        }

        public void EnableCanonicalPublisher()
        {
            if (_canonicalProvider != null) throw new InvalidOperationException("Canonical test publisher already configured.");
            var services = new ServiceCollection(); services.AddLogging();
            services.AddSingleton(Context); services.AddSingleton<ITenantContext>(TenantContext);
            services.AddMassTransit(x =>
            {
                x.AddEntityFrameworkOutbox<ApplicationDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); });
                x.UsingInMemory();
            });
            _canonicalProvider = services.BuildServiceProvider(); _canonicalScope = _canonicalProvider.CreateAsyncScope();
            var scoped = _canonicalScope.Value.ServiceProvider;
            _canonicalPublisher = new MassTransitEventPublisher(Context, scoped.GetRequiredService<IBus>(), scoped.GetRequiredService<IPublishEndpoint>(),
                NullLogger<MassTransitEventPublisher>.Instance, TenantContext);
            // Resolve registration only. Never start IBus/IHostedService or a
            // delivery loop: these cases certify SQL persistence, not delivery.
        }

        public ProductAuthoringCommand CreateCreateCommand(string? idempotencyKey) =>
            new(
                ProductAuthoringOperation.Create,
                new ProductAuthoringIdentity(TenantContext.TenantId!.Value, null, CategoryId, null, null),
                new ProductAuthoringContent("Idempotent product", null, null, null, null, null, [], [], []),
                new ProductAuthoringPricing(5m, 12m, 12m, false, 0m),
                new ProductAuthoringPublication(PublishStatus.Published, true, true, true, false),
                new ProductAuthoringSeo(null, null, null, null),
                new ProductAuthoringShippingProfile(0m, 0m, 0m, 0m, "kg", "cm"),
                new ProductAuthoringInventoryProfile("Piece", 0m, true, false, 10, false),
                new ProductAuthoringUnits(
                    UnitId,
                    UnitId,
                    [new ProductAuthoringUnit(null, UnitId, 1m, 5m, 12m, null, true, true)]),
                [],
                new ProductAuthoringMetadata(
                    false,
                    null,
                    null,
                    "default",
                    Guid.NewGuid().ToString("N"),
                    idempotencyKey,
                    null,
                    new Dictionary<string, string>
                    {
                        ["productUnitsSubmitted"] = "true",
                        ["variantsSubmitted"] = "true"
                    }));

        public void Clear() => Context.ChangeTracker.Clear();

        public async Task<List<Product>> CountProductsAsync() =>
            await Context.Products.IgnoreQueryFilters()
                .Where(product => product.TenantId == TenantContext.TenantId)
                .ToListAsync();

        public async Task<int> CountProductCreatedOutboxRowsAsync() =>
            await Context.OutboxMessages
                .CountAsync(message =>
                    message.TenantId == TenantContext.TenantId &&
                    message.EventType.EndsWith(nameof(ProductCreatedEvent)));

        public async Task<int> CountProductAuthoringAuditRowsAsync() =>
            await Context.AuditLogs
                .CountAsync(audit =>
                    audit.TenantId == TenantContext.TenantId &&
                    audit.Source == "ProductAuthoring");

        public async Task<int> CountProductIdempotencyRowsAsync(string key) =>
            await Context.ProductRequestIdempotencyRecords
                .IgnoreQueryFilters()
                .CountAsync(record =>
                    record.TenantId == TenantContext.TenantId &&
                    record.Key == $"authoring:{key}");

        public async Task SeedHistoricalProductAsync(string slug)
        {
            Context.Products.Add(new Product
            {
                Id = Guid.NewGuid(),
                TenantId = TenantContext.TenantId!.Value,
                Name = "Historical product",
                CategoryId = CategoryId,
                Unit = "Piece",
                Slug = slug,
                PublishStatus = PublishStatus.Draft,
                CreatedAt = DateTime.UtcNow
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            if (_canonicalScope.HasValue) await _canonicalScope.Value.DisposeAsync();
            if (_canonicalProvider != null) await _canonicalProvider.DisposeAsync();
            await Context.DisposeAsync();
            if (_scope is not null)
            {
                await _scope.DisposeAsync();
            }
        }
    }

    private sealed class PostgresScope : IAsyncDisposable
    {
        private readonly ProductionCoordinationAuthorityTests.Fixture _fixture;
        private PostgresScope(ProductionCoordinationAuthorityTests.Fixture fixture)
        {
            _fixture = fixture;
            Options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        }
        public Guid TenantId => _fixture.VendorId;
        public DbContextOptions<ApplicationDbContext> Options { get; }
        // Required guarded PostgreSQL18 and the current production model. Never
        // discover shared settings or treat missing infrastructure as success.
        public static async Task<PostgresScope> TryCreateAsync() => new(await ProductionCoordinationAuthorityTests.Fixture.CreateAsync());
        public ApplicationDbContext CreateDbContext(TestTenantContext tenantContext) => new(Options, tenantContext);
        public ValueTask DisposeAsync() => _fixture.DisposeAsync();
    }

    private sealed class ProductAuthoringIdempotencyDbContext : ApplicationDbContext
    {
        public ProductAuthoringIdempotencyDbContext(
            DbContextOptions<ApplicationDbContext> options,
            ITenantContext tenantContext)
            : base(options, tenantContext)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var keepTypes = new HashSet<Type>
            {
                typeof(Category),
                typeof(Product),
                typeof(PointOfSale.Core.Entities.Unit),
                typeof(ProductUnit),
                typeof(ProductVariant),
                typeof(ProductVariantAttribute),
                typeof(AuditLog),
                typeof(PointOfSale.Core.Entities.OutboxMessage),
                typeof(ProductRequestIdempotency)
            };

            var discoveredEntityTypes = typeof(ApplicationDbContext)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property =>
                    property.PropertyType.IsGenericType &&
                    property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(property => property.PropertyType.GetGenericArguments()[0])
                .Where(entityType => !keepTypes.Contains(entityType))
                .Distinct()
                .ToArray();

            foreach (var entityType in discoveredEntityTypes)
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Category>(entity =>
            {
                entity.ToTable("Categories");
                entity.HasKey(category => category.Id);
                entity.Property(category => category.Name).IsRequired();
                entity.Ignore(category => category.ParentCategory);
                entity.Ignore(category => category.SubCategories);
                entity.Ignore(category => category.Products);
            });

            modelBuilder.Entity<PointOfSale.Core.Entities.Unit>(entity =>
            {
                entity.ToTable("Units");
                entity.HasKey(unit => unit.Id);
                entity.Property(unit => unit.Name).IsRequired();
                entity.Property(unit => unit.Symbol).IsRequired();
            });

            modelBuilder.Entity<Product>(entity =>
            {
                entity.ToTable("Products");
                entity.HasKey(product => product.Id);
                entity.Property(product => product.Name).IsRequired();
                entity.Property(product => product.Unit).IsRequired();
                entity.Property(product => product.WeightUnit).IsRequired();
                entity.Property(product => product.DimensionUnit).IsRequired();
                entity.Property(product => product.RowVersion)
                    .IsRowVersion()
                    .HasColumnName("xmin")
                    .HasColumnType("xid")
                    .ValueGeneratedOnAddOrUpdate();
                entity.HasOne(product => product.Category)
                    .WithMany()
                    .HasForeignKey(product => product.CategoryId)
                    .OnDelete(DeleteBehavior.Restrict);
                entity.Ignore(product => product.Media);
                entity.Ignore(product => product.WarehouseStocks);
            });

            modelBuilder.Entity<ProductUnit>(entity =>
            {
                entity.ToTable("ProductUnits");
                entity.HasKey(productUnit => productUnit.Id);
                entity.HasOne(productUnit => productUnit.Product)
                    .WithMany(product => product.ProductUnits)
                    .HasForeignKey(productUnit => productUnit.ProductId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(productUnit => productUnit.Unit)
                    .WithMany()
                    .HasForeignKey(productUnit => productUnit.UnitId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<ProductVariant>(entity =>
            {
                entity.ToTable("ProductVariants");
                entity.HasKey(variant => variant.Id);
                entity.HasOne(variant => variant.Product)
                    .WithMany(product => product.Variants)
                    .HasForeignKey(variant => variant.ProductId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.Ignore(variant => variant.Stocks);
                entity.Ignore(variant => variant.Media);
            });

            modelBuilder.Entity<ProductVariantAttribute>(entity =>
            {
                entity.ToTable("ProductVariantAttributes");
                entity.HasKey(attribute => attribute.Id);
                entity.HasOne(attribute => attribute.Variant)
                    .WithMany(variant => variant.Attributes)
                    .HasForeignKey(attribute => attribute.VariantId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<AuditLog>(entity =>
            {
                entity.ToTable("AuditLogs");
                entity.HasKey(audit => audit.LogId);
                entity.Property(audit => audit.LogId).ValueGeneratedOnAdd();
            });

            modelBuilder.Entity<PointOfSale.Core.Entities.OutboxMessage>(entity =>
            {
                entity.ToTable("OutboxMessages");
                entity.HasKey(message => message.Id);
                entity.Property(message => message.EventType).IsRequired();
                entity.Property(message => message.EventPayload).IsRequired();
            });

            modelBuilder.Entity<ProductRequestIdempotency>(entity =>
            {
                entity.ToTable("ProductRequestIdempotency");
                entity.HasKey(record => record.Id);
                entity.HasIndex(record => new { record.TenantId, record.Key, record.Operation }).IsUnique();
                entity.HasIndex(record => new { record.TenantId, record.ProductId });
                entity.HasIndex(record => new { record.TenantId, record.ProductVariantId });
                entity.Property(record => record.Key).HasMaxLength(200).IsRequired();
                entity.Property(record => record.Operation).HasMaxLength(32).IsRequired();
                entity.Property(record => record.RequestFingerprint).HasMaxLength(64).IsRequired();
                entity.Property(record => record.ResponseJson).HasColumnType("text");
            });
        }
    }

    private sealed class ThrowAfterCommitObserver : IProductAuthoringTransactionObserver
    {
        private bool _enabled = true;

        public void Disable() => _enabled = false;

        public Task AfterUnitSynchronizationAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AfterVariantSynchronizationAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BeforeCommitAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AfterCommitAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken)
        {
            if (_enabled)
            {
                throw new InvalidOperationException("Simulated lost HTTP response after product authoring commit.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class PersistedOutboxEventPublisher(ApplicationDbContext context) : IEventPublisher
    {
        public Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default)
            where T : IntegrationEvent
        {
            Persist(@event);
            return Task.CompletedTask;
        }

        public Task PublishBatchAsync<T>(IEnumerable<T> events, CancellationToken cancellationToken = default)
            where T : IntegrationEvent
        {
            foreach (var @event in events)
            {
                Persist(@event);
            }

            return Task.CompletedTask;
        }

        public Task PublishSensitiveAsync<T>(T @event, CancellationToken cancellationToken = default)
            where T : IntegrationEvent, ISensitiveIntegrationEvent =>
            PublishAsync(@event, cancellationToken);

        private void Persist(IntegrationEvent @event)
        {
            var type = @event.GetType();
            context.OutboxMessages.Add(new PointOfSale.Core.Entities.OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventType = type.FullName ?? type.Name,
                EventPayload = JsonSerializer.Serialize(@event, type),
                TenantId = ReadGuid(@event, "TenantId"),
                AggregateId = ReadGuid(@event, "ProductId") ?? ReadGuid(@event, "VariantId"),
                EventVersion = ReadInt(@event, "EventVersion"),
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        private static Guid? ReadGuid(object value, string propertyName) =>
            value.GetType().GetProperty(propertyName)?.GetValue(value) is Guid guid ? guid : null;

        private static int ReadInt(object value, string propertyName) =>
            value.GetType().GetProperty(propertyName)?.GetValue(value) is int intValue ? intValue : 0;
    }

    private sealed class NoOpMarketplaceEventOutbox : IMarketplaceEventOutboxService
    {
        public Task EnqueueAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : class, PointOfSale.Core.Events.Marketplace.IMarketplaceDomainEvent =>
            Task.CompletedTask;
    }

    private sealed class NoOpProductAuthoringTransactionObserver : IProductAuthoringTransactionObserver
    {
        public static NoOpProductAuthoringTransactionObserver Instance { get; } = new();

        public Task AfterUnitSynchronizationAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AfterVariantSynchronizationAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BeforeCommitAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AfterCommitAsync(ProductAuthoringCommand command, Product product, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
