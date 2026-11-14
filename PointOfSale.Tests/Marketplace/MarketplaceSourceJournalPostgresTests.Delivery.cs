using Npgsql;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

public sealed partial class MarketplaceSourceJournalPostgresTests
{
    // PostgreSQL dispatch ownership only. No HTTP collaborator, remote write,
    // search visibility, remote role or whole-generation readiness is inferred.
    [Fact]
    public async Task DurableDispatchClaimCannotBeReissuedAfterReconnectOrOwnerEpochLoss()
    {
        await using var f = await Fixture.Create(applicationSchema: true);
        var seed = await SeedProjection(f);
        var (lease, build, _, frozen) = await SnapshotProjection(f);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        var ticket = await MarketplaceExternalWriteBarrier.AdmitAsync(f.Db, lease, run.RunId, seed.Second, Guid.NewGuid());
        var expected = new MarketplacePhysicalAdmission.Expected(lease.JournalId, build.GenerationId, "fixture-cluster", ticket.PhysicalUuid, ticket.Fingerprint);
        await using (var insert = new NpgsqlCommand("INSERT INTO marketplace_generation.provider_bindings VALUES(@generation,@journal,@cluster,@uuid,@fingerprint)", f.OwnerDb))
        {
            insert.Parameters.AddWithValue("generation", expected.Generation); insert.Parameters.AddWithValue("journal", expected.Journal);
            insert.Parameters.AddWithValue("cluster", expected.ClusterUuid); insert.Parameters.AddWithValue("uuid", expected.PhysicalUuid);
            insert.Parameters.AddWithValue("fingerprint", expected.Fingerprint); Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }
        var payload = await MarketplaceGenerationDeliveryProtocol.ClaimDispatchAsync(f.Db, lease, ticket, expected);
        Assert.NotNull(payload);
        await using (var reconnect = await f.Open())
            await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationDeliveryProtocol.ClaimDispatchAsync(reconnect, lease, ticket, expected));
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.dispatch_claims"));
        Assert.Equal("outstanding", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.write_intents"));
        await MarketplaceGenerationRegistry.ReleaseAsync(f.Db, lease);
        var successor = (await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromMinutes(2)))!;
        Assert.True(successor.Epoch > lease.Epoch);
        Assert.False(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, successor, build.GenerationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationDeliveryProtocol.ClaimDispatchAsync(f.Db, successor, ticket, expected));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.refresh_observations"));
        foreach (var sql in new[] { "UPDATE marketplace_generation.dispatch_claims SET claimed_at=clock_timestamp()", "DELETE FROM marketplace_generation.dispatch_claims" })
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => f.Exec(sql))).SqlState);
    }

    [Theory]
    [InlineData("binding-missing")]
    [InlineData("wrong-cluster")]
    [InlineData("wrong-ticket")]
    public async Task DispatchRefusesUnboundOrForgedProviderIdentityWithoutCommittingAClaim(string defect)
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var (lease, build, _, frozen) = await SnapshotProjection(f);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        var ticket = await MarketplaceExternalWriteBarrier.AdmitAsync(f.Db, lease, run.RunId, seed.Second, Guid.NewGuid());
        var expected = new MarketplacePhysicalAdmission.Expected(lease.JournalId, build.GenerationId, "fixture-cluster", ticket.PhysicalUuid, ticket.Fingerprint);
        if (defect != "binding-missing")
        {
            await using var binding = new NpgsqlCommand("INSERT INTO marketplace_generation.provider_bindings VALUES(@generation,@journal,@cluster,@uuid,@fingerprint)", f.OwnerDb);
            binding.Parameters.AddWithValue("generation", expected.Generation); binding.Parameters.AddWithValue("journal", expected.Journal);
            binding.Parameters.AddWithValue("cluster", defect == "wrong-cluster" ? "other-cluster" : expected.ClusterUuid);
            binding.Parameters.AddWithValue("uuid", expected.PhysicalUuid); binding.Parameters.AddWithValue("fingerprint", expected.Fingerprint);
            await binding.ExecuteNonQueryAsync();
        }
        if (defect == "wrong-ticket") ticket = ticket with { CompletionToken = Guid.NewGuid() };
        if (defect == "wrong-ticket")
            await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationDeliveryProtocol.ClaimDispatchAsync(f.Db, lease, ticket, expected));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => MarketplaceGenerationDeliveryProtocol.ClaimDispatchAsync(f.Db, lease, ticket, expected));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.dispatch_claims"));
        Assert.Equal("outstanding", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.write_intents"));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.refresh_observations"));
    }
}
