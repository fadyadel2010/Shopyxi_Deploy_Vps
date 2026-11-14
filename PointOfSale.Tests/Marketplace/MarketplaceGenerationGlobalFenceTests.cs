using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

public sealed class MarketplaceGenerationGlobalFenceTests
{
    [Fact]
    public async Task SimulatedServerFenceRejectsDelayedOldEpochOnPreviouslyMissingDocument()
    {
        var f = new MarketplaceGenerationOperationRecoveryTests.Fixture(); var old = f.Operation();
        f.Provider.Processing = true;
        await Assert.ThrowsAsync<TimeoutException>(() => f.Engine.DeliverAsync(old));
        f.Provider.Epoch = 2; f.Provider.Processing = false;
        f.Provider.CompleteProcessing(old);
        Assert.Equal(GenerationOperationState.ConfirmedRejected, (await f.Restart().RecoverAsync(old.Id)).State);
        Assert.Empty(f.Provider.Documents); Assert.Equal(0, f.Provider.Mutations);
    }

    [Fact]
    public async Task SimulatedIndependentCoordinatorsCannotRestoreOldEpochAfterRestart()
    {
        var f = new MarketplaceGenerationOperationRecoveryTests.Fixture(); f.Provider.Epoch = 2;
        var stale = f.Operation();
        Assert.Equal(GenerationOperationState.ConfirmedRejected, (await f.Restart().DeliverAsync(stale)).State);
        Assert.Equal(GenerationOperationState.Acknowledged, (await f.Restart().DeliverAsync(f.Operation(epoch: 2))).State);
        Assert.Equal(GenerationOperationState.ConfirmedRejected, (await f.Restart().DeliverAsync(f.Operation())).State);
        Assert.Equal(1, f.Provider.Mutations);
    }

    [Theory]
    [InlineData("POST", "/marketplace_services_g_00000000000000000000000000000000/_update/id")]
    [InlineData("POST", "/_bulk")]
    [InlineData("POST", "/alias/_update/id")]
    [InlineData("POST", "/_reindex")]
    [InlineData("PUT", "/physical/_doc/id")]
    [InlineData("DELETE", "/physical")]
    [InlineData("POST", "/physical/_refresh")]
    public void ProductionRawMutationsFailClosedBeforeDispatch(string method, string path)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MarketplaceGenerationTransport.RequireRawOperationAllowed(false, new HttpMethod(method), path));
        Assert.Equal("GENERATION_ATOMIC_WRITE_BOUNDARY_NOT_INSTALLED", error.Message);
    }

    [Fact]
    public void PhysicalReadVerificationDoesNotConferMutationAuthority()
    {
        MarketplaceGenerationTransport.RequireRawOperationAllowed(false, HttpMethod.Get, "/physical");
        MarketplaceGenerationTransport.RequireRawOperationAllowed(false, HttpMethod.Post, "/physical/_mget?realtime=false");
        MarketplaceGenerationTransport.RequireRawOperationAllowed(false, HttpMethod.Post, "/physical/_count");
        Assert.Throws<InvalidOperationException>(() => MarketplaceGenerationTransport.RequireRawOperationAllowed(false, HttpMethod.Post, "/physical/_update/id"));
    }

    [Fact]
    public void DurableRecoverySchemaPreservesImmutableEvidenceAndRejectsUnknownAcknowledgementShortcut()
    {
        var sql = MarketplaceGenerationOperationSchema.Sql;
        Assert.Contains("UNIQUE(generation_id,service_id,source_revision)", sql, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", sql, StringComparison.Ordinal);
        Assert.Contains("immutable_recovery_identity_truncate", sql, StringComparison.Ordinal);
        Assert.Contains("immutable_recovery_event_truncate", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("prior_state='OutcomeUnknown' AND NEW.state='Acknowledged'", sql, StringComparison.Ordinal);
    }
}
