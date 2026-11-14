using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenSearch.Client;
using OpenSearch.Net;
using PointOfSale.Core.DTOs.Marketplace;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Marketplace;

public sealed partial class MarketplaceSourceJournalPostgresTests
{
    [Fact]
    public async Task SqlActualSearchServicePinsAllPagesToOriginalGenerationAcrossPointerCutover()
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var first = await SqlLifecycleInput(f); await ActivateSqlInput(f, first, 0, null);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.WriterConnection).Options);
        var reads = new SqlMarketplaceGenerationReads(db, new FixedGenerationReaderIdentity(),
            Options.Create(new MarketplaceGenerationOptions { GenerationReadersEnabled = true }));
        var connection = new EmptyGenerationSearchConnection();
        var client = new OpenSearchClient(new ConnectionSettings(new SingleNodeConnectionPool(new Uri("https://fictional.invalid")), connection)
            .MaximumRetries(0).UseSystemTextJson(false));
        var search = new MarketplaceSearchService(client, db, new TestTenantContext(seed.Tenant), Mock.Of<IProviderTrustService>(MockBehavior.Strict),
            NullLogger<MarketplaceSearchService>.Instance, reads);
        var page1 = await search.SearchServicesAsync(new MarketplaceSearchRequest());
        Assert.NotNull(page1.GenerationContinuation);
        var second = await SqlLifecycleInput(f, first.Lease); await ActivateSqlInput(f, second, 1, first.Physical.Generation);
        var page2 = await search.SearchServicesAsync(new MarketplaceSearchRequest { Page = 2, GenerationContinuation = page1.GenerationContinuation });
        Assert.Equal(page1.GenerationContinuation, page2.GenerationContinuation);
        Assert.All(connection.Paths, path => Assert.Equal("/" + first.Physical.PhysicalName + "/_search", path));
        Assert.Equal(2L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.reader_operations WHERE completed_at IS NOT NULL AND deadline IS NOT NULL"));
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.reader_pins WHERE released_at IS NULL"));
    }

    private sealed class FixedGenerationReaderIdentity : IMarketplaceGenerationReaderIdentity
    {
        private readonly Guid reader = Guid.NewGuid();
        public Guid RequireAuthenticatedReader() => reader;
    }
    private sealed class EmptyGenerationSearchConnection : InMemoryConnection
    {
        internal List<string> Paths = [];
        public override Task<TResponse> RequestAsync<TResponse>(RequestData data, CancellationToken ct)
        {
            Paths.Add(data.Uri.AbsolutePath);
            return new InMemoryConnection(System.Text.Encoding.UTF8.GetBytes("{\"took\":1,\"timed_out\":false,\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0},\"hits\":{\"total\":{\"value\":0,\"relation\":\"eq\"},\"hits\":[]}}"), 200)
                .RequestAsync<TResponse>(data, ct);
        }
    }

    [Fact]
    public async Task SqlUnresolvedIndividualAttemptPreventsDrainDespiteTerminalIntent()
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var (lease, build, _, frozen) = await SnapshotProjection(f);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        var ticket = await MarketplaceExternalWriteBarrier.AdmitAsync(f.Db, lease, run.RunId, seed.Second, Guid.NewGuid());
        var attempt = Guid.NewGuid();
        await using (var tx = await f.Db.BeginTransactionAsync())
        {
            await MarketplaceGenerationAttemptOutcomes.AdmitAsync(f.Db, tx, ticket.OperationId, attempt, "fictional-reviewed-request", lease.Epoch, default);
            await tx.CommitAsync();
        }
        // SQL inputs, not a claimed remote outcome: proves the barrier's separate
        // accounting even when one transport has recorded the intent as terminal.
        await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(f.Db, ticket, ExternalDefiniteOutcome.Acknowledged);
        Assert.False(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, lease, build.GenerationId));
        await MarketplaceGenerationAttemptOutcomes.CompleteAsync(f.Db, attempt, true, default);
        await MarketplaceGenerationAttemptOutcomes.CompleteAsync(f.Db, attempt, true, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationAttemptOutcomes.CompleteAsync(f.Db, attempt, false, default));
        Assert.True(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, lease, build.GenerationId));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => f.Exec("DELETE FROM marketplace_generation.delivery_attempt_outcomes"))).SqlState);
    }

    [Fact]
    public async Task SqlBoundedAbandonedReaderExpiresWithoutForgettingAnUnboundedAdmission()
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        var proof = await SqlLifecycleInput(f); await ActivateSqlInput(f, proof, 0, null);
        var pin = await MarketplaceGenerationReaderPins.AcquireAsync(f.Db, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), TimeSpan.FromMinutes(1));
        var bounded = await MarketplaceGenerationReaderPins.AdmitAsync(f.Db, pin, pin.Tenant, pin.Reader, pin.QueryHash);
        var unbounded = await MarketplaceGenerationReaderPins.AdmitAsync(f.Db, pin, pin.Tenant, pin.Reader, pin.QueryHash);
        await f.Exec($"UPDATE marketplace_generation.reader_operations SET deadline=clock_timestamp()-interval '1 second' WHERE operation_id='{bounded}'");
        await MarketplaceGenerationReaderPins.ReleaseAsync(f.Db, pin);
        await f.Setup("UPDATE marketplace_generation.active_pointer SET active_generation=NULL; UPDATE marketplace_generation.lifecycle SET state='retained',retain_until=clock_timestamp()-interval '1 second'");
        var retirement = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationRetirement.PrepareAsync(f.Db, proof.Lease, retirement, proof.Physical));
        await MarketplaceGenerationReaderPins.CompleteAsync(f.Db, pin, unbounded);
        await MarketplaceGenerationRetirement.PrepareAsync(f.Db, proof.Lease, retirement, proof.Physical);
        Assert.Equal("retiring", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.lifecycle"));
    }

    [Fact]
    public async Task SqlActivePointerRoutesIdempotentDeliverySignalsWithoutLegacyProviderAuthority()
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var proof = await SqlLifecycleInput(f); await ActivateSqlInput(f, proof, 0, null);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.WriterConnection).Options);
        var router = new MarketplaceGenerationMutationRouter(db); var operation = Guid.NewGuid();
        Assert.True(await router.TryRouteAsync(operation, "service", seed.Second, default));
        Assert.True(await router.TryRouteAsync(operation, "service", seed.Second, default));
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.delivery_signals"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.TryRouteAsync(operation, "provider", seed.Second, default));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => f.Exec("UPDATE marketplace_generation.delivery_signals SET source_revision=0"))).SqlState);
    }

    [Fact]
    public async Task SqlFullRunOrchestrationRecordsRefreshOnlyAfterEveryDeliveredDocumentIsVisible()
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        var input = await RecordingRun(f);
        using var transport = new MarketplaceGenerationTransport(new Uri("https://fictional-generation.invalid/"), "fictional", "not-real", input.Provider);
        var delivery = new MarketplaceGenerationRunDelivery();
        await delivery.DeliverAsync(f.Db, input.Lease, input.Run, input.Provider.Expected, transport);
        await delivery.DeliverAsync(f.Db, input.Lease, input.Run, input.Provider.Expected, transport);
        Assert.Equal(2, input.Provider.Writes);
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.refresh_observations"));
        Assert.Equal(2L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.delivery_attempt_outcomes"));
        Assert.True(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, input.Lease, input.Provider.Expected.Generation));
    }

    [Fact]
    public async Task SqlRollbackReplaysNewWritesAndTombstonesBeforeAtomicActivationAndRepeatedOutcome()
    {
        // Actual PostgreSQL + recording provider. NOT real OpenSearch certification.
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var first = await RecordingRun(f);
        using var transport1 = new MarketplaceGenerationTransport(new Uri("https://fictional-generation.invalid/"), "fictional", "not-real", first.Provider);
        var delivery = new MarketplaceGenerationRunDelivery(); var transition = new MarketplaceGenerationTransition();
        await delivery.DeliverAsync(f.Db, first.Lease, first.Run, first.Provider.Expected, transport1);
        Assert.True(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, first.Lease, first.Provider.Expected.Generation));
        await transition.ExecuteAsync(f.Db, first.Lease, Guid.NewGuid(), first.Run, false, 0, null, first.Provider.Expected, transport1);
        var second = await RecordingRun(f, first.Lease);
        using var transport2 = new MarketplaceGenerationTransport(new Uri("https://fictional-generation.invalid/"), "fictional", "not-real", second.Provider);
        await delivery.DeliverAsync(f.Db, first.Lease, second.Run, second.Provider.Expected, transport2);
        Assert.True(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, first.Lease, second.Provider.Expected.Generation));
        await transition.ExecuteAsync(f.Db, first.Lease, Guid.NewGuid(), second.Run, false, 1, first.Provider.Expected.Generation, second.Provider.Expected, transport2);
        await f.Exec($"UPDATE public.\"Services\" SET \"Title\"='after cutover' WHERE \"Id\"='{seed.Second}'");
        await f.Exec($"UPDATE public.\"Services\" SET \"IsDeleted\"=true WHERE \"Id\"='{seed.First}'");
        await MarketplaceGenerationRegistry.ReleaseAsync(f.Db, first.Lease);
        var owner = (await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromMinutes(2)))!;
        var rollback = new MarketplaceGenerationRollback(delivery, transition); var operation = Guid.NewGuid();
        await rollback.ExecuteAsync(f.Db, owner, operation, 2, second.Provider.Expected.Generation, first.Provider.Expected, transport1);
        await rollback.ExecuteAsync(f.Db, owner, operation, 2, second.Provider.Expected.Generation, first.Provider.Expected, transport1);
        Assert.Equal(3L, await Fixture.Scalar(f.Db, "SELECT version FROM marketplace_generation.active_pointer"));
        Assert.Equal(first.Provider.Expected.Generation, await Fixture.Scalar(f.Db, "SELECT active_generation FROM marketplace_generation.active_pointer"));
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.catchup_intents WHERE state='completed'"));
        Assert.True(first.Provider.Writes > 2);
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.write_intents i JOIN marketplace_generation.output_rows o ON o.run_id=i.run_id AND o.service_id=i.service_id WHERE i.run_id=(SELECT run_id FROM marketplace_generation.catchup_intents WHERE state='completed') AND o.payload IS NULL AND i.state='acknowledged'"));
    }

    private static async Task<(GenerationLease Lease, Guid Run, RecordingGenerationProvider Provider)> RecordingRun(Fixture f, GenerationLease? existing = null)
    {
        var lease = existing ?? (await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromMinutes(2)))!;
        var build = await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db, lease, Guid.NewGuid());
        var provider = RecordingGenerationProvider.Physical(lease.JournalId, build.GenerationId);
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db, lease, build.GenerationId, provider.Expected.PhysicalUuid, provider.Expected.Fingerprint);
        var checkpoint = await MarketplaceGenerationRegistry.SnapshotAsync(f.Db, lease, build.GenerationId);
        var frozen = await MarketplaceGenerationInputExport.FreezeAsync(f.Db, lease, build.GenerationId, Guid.NewGuid(), checkpoint);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        return (lease, run.RunId, provider);
    }
}
