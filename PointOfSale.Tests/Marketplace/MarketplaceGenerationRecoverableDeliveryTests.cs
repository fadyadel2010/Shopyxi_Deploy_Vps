using System.Text.Json;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

/// <summary>Real gateway/recovery/orchestration with deterministic durable/provider substitutes; NOT live-provider proof.</summary>
public sealed class MarketplaceGenerationRecoverableDeliveryTests
{
    [Fact]
    public async Task LostSuccessResponseReconcilesExactOriginalOperationThroughFinalDeliveryAcknowledgement()
    {
        var f = new Fixture(); var op = f.Operation(); f.Provider.DropAfterApply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Deliver(op));
        Assert.Equal(GenerationOperationState.OutcomeUnknown, f.Store.Current(op).State);
        Assert.False(f.Delivery.Acknowledged); Assert.False(f.Journal.Drained);
        Assert.Equal(GenerationOperationState.Acknowledged, (await f.Deliver(op)).State);
        Assert.True(f.Delivery.Acknowledged); Assert.True(f.Journal.Drained);
        Assert.Equal(1, f.Provider.Submissions); Assert.Equal(1, f.Provider.Mutations);
    }

    [Fact]
    public async Task ProcessingTimeoutCannotAcknowledgeOrAdvanceEpochAndIdempotentRetryMakesDelayedOriginalHarmless()
    {
        var f = new Fixture(); var op = f.Operation(); f.Provider.Timeout = true;
        await Assert.ThrowsAsync<TimeoutException>(() => f.Deliver(op));
        Assert.Throws<InvalidOperationException>(() => f.Journal.AdvanceEpoch(2));
        await Assert.ThrowsAsync<TimeoutException>(() => f.Deliver(op));
        Assert.False(f.Delivery.Acknowledged); Assert.False(f.Journal.Drained);
        f.Provider.Timeout = false;
        await f.Deliver(op); f.Journal.AdvanceEpoch(2);
        f.Provider.Apply(op); // Arbitrarily late original; must be a no-op, not a write.
        Assert.Equal(1, f.Provider.Mutations); Assert.Equal(2, f.Journal.Epoch);
    }

    [Fact]
    public async Task CrashAfterProviderSuccessBeforeAppliedStateDoesNotResubmitOrLoseOriginalAttempt()
    {
        var f = new Fixture(); var op = f.Operation(); f.Store.FailNext = GenerationOperationState.Applied;
        await Assert.ThrowsAsync<IOException>(() => f.Deliver(op));
        Assert.Equal(GenerationOperationState.Submitted, f.Store.Current(op).State);
        await f.Deliver(op);
        Assert.True(f.Delivery.Acknowledged); Assert.Equal(1, f.Provider.Submissions);
    }

    [Theory]
    [InlineData("VisibleAfterRefresh")]
    [InlineData("Acknowledged")]
    public async Task RefreshThenRecoveryTransactionFailureReverifiesBeforeAcknowledgement(string state)
    {
        var f = new Fixture(); var op = f.Operation(); f.Store.FailNext = Enum.Parse<GenerationOperationState>(state);
        await Assert.ThrowsAsync<IOException>(() => f.Deliver(op));
        Assert.False(f.Delivery.Acknowledged);
        var before = f.Provider.Refreshes; await f.Deliver(op);
        Assert.True(f.Provider.Refreshes > before); Assert.True(f.Delivery.Acknowledged);
        Assert.Equal(1, f.Provider.Mutations);
    }

    [Fact]
    public async Task FinalDeliveryAcknowledgementTransactionFailureReverifiesEvenAfterRecoveryWasAcknowledged()
    {
        var f = new Fixture(); var op = f.Operation(); f.Delivery.FailAcknowledgement = true;
        await Assert.ThrowsAsync<IOException>(() => f.Deliver(op));
        Assert.Equal(GenerationOperationState.Acknowledged, f.Store.Current(op).State);
        Assert.False(f.Delivery.Acknowledged); var before = f.Provider.Refreshes;
        await f.Deliver(op);
        Assert.Equal(before + 1, f.Provider.Refreshes); Assert.True(f.Delivery.Acknowledged);
    }

    [Fact]
    public async Task NewerTombstoneAfterFinalSqlFailureResolvesAsSupersededWithoutHistoricalAcknowledgement()
    {
        var f = new Fixture(); var op = f.Operation(); f.Delivery.FailAcknowledgement = true;
        await Assert.ThrowsAsync<IOException>(() => f.Deliver(op));
        var newer = f.Operation(op.Ticket.ServiceId, 2, true); await f.Store.AdmitAsync(newer, default); f.Provider.Apply(newer);
        Assert.Equal(GenerationOperationState.SupersededAfterRefresh, (await f.Deliver(op)).State);
        Assert.True(f.Delivery.Superseded); Assert.False(f.Delivery.Acknowledged); Assert.Equal(newer, f.Provider.Documents[op.Ticket.ServiceId]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerRevisionOrTombstoneCannotBeOverwrittenOrAcknowledgedAsOlderUnknown(bool deleted)
    {
        var f = new Fixture(); var op = f.Operation(); f.Provider.DropBeforeApply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Deliver(op));
        var newer = f.Operation(op.Ticket.ServiceId, 2, deleted);
        await f.Store.AdmitAsync(newer, default);
        f.Provider.Apply(newer);
        Assert.Equal(GenerationOperationState.SupersededAfterRefresh, (await f.Deliver(op)).State);
        Assert.Equal(newer, f.Provider.Documents[op.Ticket.ServiceId]);
        Assert.False(f.Delivery.Acknowledged); Assert.Equal(1, f.Provider.Mutations);
        Assert.True(f.Delivery.Superseded); Assert.True(f.Journal.Drained);
        f.Journal.AdvanceEpoch(2); f.Provider.Apply(op);
        Assert.Equal(1, f.Provider.Mutations);
    }

    [Fact]
    public async Task SameIdentitySameDigestIsIdempotentAndDifferentDigestIsRejectedBeforeProviderAccess()
    {
        var f = new Fixture(); var op = f.Operation(); await f.Deliver(op); await f.Deliver(op);
        Assert.Equal(1, f.Provider.Submissions); Assert.Equal(1, f.Provider.Mutations);
        var payload = JsonSerializer.Serialize(new { serviceId = op.Ticket.ServiceId, title = "conflict" });
        var conflict = op with { Payload = payload, Ticket = op.Ticket with { Digest = MarketplaceFrozenOutput.RowDigest(op.Ticket.ServiceId, payload) } };
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Deliver(conflict));
        Assert.Equal(1, f.Provider.Submissions);
    }

    [Fact]
    public async Task AlreadyFinalAcknowledgedReplayPreservesHistoricalOutcomeAfterANewerRevision()
    {
        var f = new Fixture(); var op = f.Operation(); await f.Deliver(op);
        var newer = f.Operation(op.Ticket.ServiceId, 2, true); f.Provider.Apply(newer);
        var refreshes = f.Provider.Refreshes;
        Assert.Equal(GenerationOperationState.Acknowledged, (await f.Deliver(op)).State);
        Assert.Equal(refreshes, f.Provider.Refreshes); Assert.Equal(newer, f.Provider.Documents[op.Ticket.ServiceId]);
        Assert.False(f.Delivery.Superseded);
    }

    [Fact]
    public async Task StaleEpochUnknownReconciliationCannotDispatchOrAcknowledgeMissingContent()
    {
        var f = new Fixture(); var op = f.Operation(); f.Provider.DropBeforeApply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Deliver(op));
        // An external force is deliberately NOT a legal AdvanceEpoch; demonstrate
        // stale submission denial even under a corrupted/foreign authority value.
        f.Journal.Epoch = 2;
        Assert.Equal(GenerationOperationState.OutcomeUnknown, (await f.Deliver(op)).State);
        Assert.Equal(1, f.Provider.Submissions); Assert.Empty(f.Provider.Documents);
        Assert.False(f.Delivery.Acknowledged); Assert.False(f.Journal.Drained);
    }

    [Fact]
    public async Task SharedDurableBoundaryBlocksEpochWhileRequestToUntouchedDocumentIsPausedAfterAdmission()
    {
        var f = new Fixture(); var op = f.Operation();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Provider.BeforeWrite = async () => { admitted.SetResult(); await release.Task; };
        var first = f.Deliver(op); await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Provider.Documents); Assert.False(f.Journal.Drained);
        Assert.Throws<InvalidOperationException>(() => f.Journal.AdvanceEpoch(2));
        release.SetResult(); await first; f.Journal.AdvanceEpoch(2);
        var untouched = f.Operation(epoch: 1);
        await f.Store.AdmitAsync(untouched, default);
        var independent = new MarketplaceGenerationGateway(f.Journal, f.Provider);
        Assert.Equal(GenerationOperationState.ConfirmedRejected, (await independent.SubmitAsync(untouched, default)).State);
        Assert.False(f.Provider.Documents.ContainsKey(untouched.Ticket.ServiceId));
        f.Provider.Apply(op); Assert.Equal(1, f.Provider.Mutations);
    }

    [Fact]
    public async Task LostOwnershipRejectsNewAdmissionAndUnresolvedAttemptsSurviveGatewayRestart()
    {
        var f = new Fixture(); var op = f.Operation(); f.Provider.DropBeforeApply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Deliver(op));
        f.Journal.OwnerAlive = false;
        var restarted = new MarketplaceGenerationGateway(f.Journal, f.Provider);
        Assert.Equal(GenerationOperationState.OutcomeUnknown, (await restarted.SubmitAsync(op, default)).State);
        Assert.Throws<InvalidOperationException>(() => f.Journal.AdvanceEpoch(2));
        Assert.Equal(1, f.Provider.Submissions); Assert.False(f.Journal.Drained);
    }

    [Fact]
    public async Task RestartAfterLeaseExpiryReconcilesAppliedUnknownWithoutAcquiringANewEpochOrSendingAgain()
    {
        var f = new Fixture(); var op = f.Operation(); f.Provider.DropAfterApply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Deliver(op)); f.Journal.OwnerAlive = false;
        var restarted = new MarketplaceGenerationRecoverableDelivery(f.Delivery, f.Store, new MarketplaceGenerationGateway(f.Journal, f.Provider));
        Assert.Equal(GenerationOperationState.Acknowledged, (await restarted.RecoverAsync(op.Id)).State);
        Assert.True(f.Delivery.Acknowledged); Assert.True(f.Journal.Drained); Assert.Equal(1, f.Provider.Submissions);
        f.Journal.AdvanceEpoch(2);
    }

    [Fact]
    public async Task ExpiredOwnerWithStillMissingUnknownRemainsReconcileableRatherThanInventingARejection()
    {
        var f = new Fixture(); var op = f.Operation(); f.Provider.DropBeforeApply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Deliver(op)); f.Journal.OwnerAlive = false;
        var restarted = new MarketplaceGenerationRecoverableDelivery(f.Delivery, f.Store, new MarketplaceGenerationGateway(f.Journal, f.Provider));
        Assert.Equal(GenerationOperationState.OutcomeUnknown, (await restarted.RecoverAsync(op.Id)).State);
        Assert.False(f.Delivery.Acknowledged); Assert.False(f.Journal.Drained);
        f.Provider.Apply(op); // Original finally finishes; no new stale submission was admitted.
        Assert.Equal(GenerationOperationState.Acknowledged, (await restarted.RecoverAsync(op.Id)).State);
        Assert.Equal(1, f.Provider.Submissions); Assert.True(f.Delivery.Acknowledged);
    }

    [Fact]
    public async Task PreparedButNeverDispatchedStaleOperationCanResolveNotSentWithoutAcknowledgingContent()
    {
        var f = new Fixture(); var op = f.Operation(); await f.Store.AdmitAsync(op, default);
        f.Journal.AdvanceEpoch(2); // No raw attempt exists; this transition is safe.
        Assert.Equal(GenerationOperationState.ConfirmedRejected, (await f.Deliver(op)).State);
        Assert.True(f.Delivery.Rejected); Assert.False(f.Delivery.Acknowledged);
        Assert.Equal(0, f.Provider.Submissions); Assert.Empty(f.Provider.Documents);
    }

    [Fact]
    public async Task RejectedVisibilityOrForeignTargetNeverAcknowledgesDelivery()
    {
        var f = new Fixture(); var op = f.Operation(); f.Provider.Invisible = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Deliver(op));
        Assert.False(f.Delivery.Acknowledged);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Deliver(op with { Target = op.Target with { PhysicalUuid = "foreign" } }));
        Assert.False(f.Delivery.Acknowledged);
    }

    [Fact]
    public void SqlBoundaryGuardsDirectEpochAndPointerChangesAndPreservesAttemptsAcrossSessionLoss()
    {
        var sql = MarketplaceGenerationGatewaySchema.Sql;
        Assert.Contains("gateway_epoch BEFORE UPDATE ON marketplace_generation.control", sql);
        Assert.Contains("gateway_pointer BEFORE UPDATE ON marketplace_generation.active_pointer", sql);
        Assert.Contains("PERFORM 1 FROM marketplace_generation.control WHERE singleton FOR UPDATE", sql);
        Assert.Contains("gateway_admission BEFORE INSERT", sql);
        Assert.Contains("immutable_gateway_attempt_truncate", sql);
        Assert.Contains("immutable_gateway_settlement_truncate", sql);
        Assert.DoesNotContain("ON DELETE CASCADE", sql);
        Assert.Contains("REVOKE ALL", sql);
        Assert.Contains("guard_gateway_supersession", sql);
        Assert.Contains("w.identity_json::jsonb->'Target'=r.identity_json::jsonb->'Target'", sql);
        Assert.Contains("r.identity_json::jsonb#>>'{Ticket,Epoch}'", sql);
        Assert.Contains("set_config('synchronous_commit','on',true)", sql);
        var root = Environment.GetEnvironmentVariable("TEST_SOLUTION_ROOT")!;
        var run = File.ReadAllText(Path.Combine(root, "PointOfSale.Infrastructure/Marketplace/Generation/MarketplaceGenerationRunDelivery.cs"));
        Assert.Contains("i.state IN ('outstanding','acknowledged','superseded','not-sent')", run);
        var barrier = File.ReadAllText(Path.Combine(root, "PointOfSale.Infrastructure/Marketplace/Generation/MarketplaceExternalWriteBarrier.cs"));
        Assert.Contains("state IN ('acknowledged','superseded') AND source_revision>=@revision", barrier);
    }

    [Fact]
    public void GatewaySecurityReviewPlanIsExactTargetUpdateOnlyWithoutLifecycleOrDeleteAuthority()
    {
        var f = new Fixture(); using var role = JsonDocument.Parse(MarketplaceGenerationSecurityPlan.GatewayRole(f.Target));
        var permission = role.RootElement.GetProperty("index_permissions")[0];
        Assert.Equal(new[] { f.Target.PhysicalName }, permission.GetProperty("index_patterns").EnumerateArray().Select(p => p.GetString()));
        var actions = permission.GetProperty("allowed_actions").EnumerateArray().Select(a => a.GetString()!).ToArray();
        Assert.Contains("indices:data/write/update", actions);
        Assert.DoesNotContain(actions, a => a.Contains("delete", StringComparison.Ordinal) || a.Contains("bulk", StringComparison.Ordinal) ||
            a.Contains("alias", StringComparison.Ordinal) || a.Contains("mapping", StringComparison.Ordinal) || a.Contains("settings", StringComparison.Ordinal) || a.Contains("create", StringComparison.Ordinal));
    }

    private sealed class Fixture
    {
        internal readonly MarketplaceGenerationOperationRecoveryTests.MemoryStore Store = new();
        internal readonly Journal Journal = new();
        internal readonly Provider Provider = new();
        internal readonly Delivery Delivery;
        internal readonly MarketplacePhysicalAdmission.Expected Target = new(Guid.NewGuid(), Guid.NewGuid(), "cluster", "physical", new string('a', 64));
        internal Fixture() { Delivery = new(Store); Provider.BeforeSubmit = op => Assert.Equal(GenerationOperationState.Submitted, Store.Current(op).State); }
        internal GenerationOperation Operation(Guid? service = null, long revision = 1, bool deleted = false, long epoch = 1)
        {
            var id = service ?? Guid.NewGuid(); var payload = deleted ? null : JsonSerializer.Serialize(new { serviceId = id, title = "immutable" });
            return new(Target, new(Guid.NewGuid(), Guid.NewGuid(), Target.Generation, Guid.NewGuid(), id, revision,
                Target.PhysicalUuid, Target.Fingerprint, MarketplaceFrozenOutput.RowDigest(id, payload), epoch), payload);
        }
        internal Task<GenerationOperationSnapshot> Deliver(GenerationOperation op)
        {
            Delivery.Input = op;
            return new MarketplaceGenerationRecoverableDelivery(Delivery, Store, new MarketplaceGenerationGateway(Journal, Provider))
                .DeliverAsync(new(Target.Journal, Guid.NewGuid(), Journal.Epoch, DateTime.UtcNow.AddMinutes(1)), op.Ticket, op.Target);
        }
    }

    private sealed class Delivery(MarketplaceGenerationOperationRecoveryTests.MemoryStore store) : IGenerationDeliveryJournal
    {
        internal GenerationOperation Input = null!;
        internal bool Acknowledged, FailAcknowledgement, Superseded, Rejected;
        public async Task<GenerationOperation> PrepareAsync(GenerationLease lease, ExternalWriteTicket ticket, MarketplacePhysicalAdmission.Expected target, CancellationToken ct)
        { await store.AdmitAsync(Input, ct); return Input; }
        public Task AcknowledgeAsync(GenerationOperation op, CancellationToken ct)
        {
            Assert.Equal(GenerationOperationState.Acknowledged, store.Current(op).State);
            if (FailAcknowledgement) { FailAcknowledgement = false; throw new IOException("Injected final acknowledgement rollback."); }
            Acknowledged = true; return Task.CompletedTask;
        }
        public Task ResolveSupersededAsync(GenerationOperation op, CancellationToken ct)
        {
            Assert.Equal(GenerationOperationState.SupersededAfterRefresh, store.Current(op).State);
            Superseded = true; return Task.CompletedTask;
        }
        public Task<bool> IsAcknowledgedAsync(GenerationOperation op, CancellationToken ct) => Task.FromResult(Acknowledged);
        public Task ResolveRejectedAsync(GenerationOperation op, CancellationToken ct)
        { Assert.Equal(GenerationOperationState.ConfirmedRejected, store.Current(op).State); Rejected = true; return Task.CompletedTask; }
    }

    private sealed class Journal : IGenerationGatewayJournal
    {
        internal long Epoch = 1;
        internal bool OwnerAlive = true;
        private readonly Dictionary<Guid, GenerationOperation> attempts = new();
        private readonly HashSet<Guid> settled = new();
        internal bool Drained => attempts.Keys.All(settled.Contains);
        public Task<GenerationDispatchPermit?> AdmitAsync(GenerationOperation op, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (op.Ticket.Epoch != Epoch || !OwnerAlive) return Task.FromResult<GenerationDispatchPermit?>(null);
            var id = Guid.NewGuid(); attempts.Add(id, op); return Task.FromResult<GenerationDispatchPermit?>(new(id, op));
        }
        public Task SettleAsync(GenerationOperation op, CancellationToken ct, GenerationOperation? successor = null)
        { foreach (var row in attempts.Where(a => a.Value == op)) settled.Add(row.Key); return Task.CompletedTask; }
        public Task<bool> HasOutstandingAsync(GenerationOperation op, CancellationToken ct) =>
            Task.FromResult(attempts.Any(a => a.Value == op && !settled.Contains(a.Key)));
        internal void AdvanceEpoch(long next)
        { if (!Drained || next <= Epoch) throw new InvalidOperationException("Undrained or non-monotone epoch."); Epoch = next; }
    }

    private sealed class Provider : IGenerationGatewayProvider
    {
        internal bool DropBeforeApply, DropAfterApply, Timeout, Invisible;
        internal int Submissions, Mutations, Refreshes;
        internal Action<GenerationOperation>? BeforeSubmit;
        internal Func<Task>? BeforeWrite;
        internal readonly Dictionary<Guid, GenerationOperation> Documents = new();
        public async Task WriteAsync(GenerationDispatchPermit permit, CancellationToken ct)
        {
            BeforeSubmit?.Invoke(permit.Operation); Submissions++;
            if (BeforeWrite is not null) await BeforeWrite();
            if (Timeout) throw new TimeoutException("Still processing, request may be delayed.");
            if (DropBeforeApply) { DropBeforeApply = false; throw new IOException("Request delayed before apply."); }
            Apply(permit.Operation);
            if (DropAfterApply) { DropAfterApply = false; throw new IOException("Response lost after apply."); }
        }
        internal void Apply(GenerationOperation op)
        {
            if (Documents.TryGetValue(op.Ticket.ServiceId, out var prior))
            {
                if (prior.Ticket.Epoch > op.Ticket.Epoch) throw new InvalidDataException("Stale document epoch.");
                if (prior.Ticket.SourceRevision > op.Ticket.SourceRevision) return;
                if (prior.Ticket.SourceRevision == op.Ticket.SourceRevision)
                {
                    if (prior.Ticket.Digest != op.Ticket.Digest) throw new InvalidDataException("Revision digest conflict.");
                    if (prior.Ticket.Epoch == op.Ticket.Epoch) return;
                }
            }
            Documents[op.Ticket.ServiceId] = op; Mutations++;
        }
        public Task<bool> VerifyAsync(GenerationOperation op, bool refresh, CancellationToken ct)
        {
            if (refresh) { Refreshes++; if (Invisible) throw new InvalidDataException("Not searchable."); }
            if (!Documents.TryGetValue(op.Ticket.ServiceId, out var current)) return Task.FromResult(false);
            if (current != op) throw new InvalidDataException("Exact operation was superseded or conflicted.");
            return Task.FromResult(true);
        }
        public Task<GenerationOperation?> FindVisibleSuccessorAsync(GenerationOperation op, CancellationToken ct)
        {
            if (!Documents.TryGetValue(op.Ticket.ServiceId, out var current) || current.Ticket.SourceRevision <= op.Ticket.SourceRevision)
                return Task.FromResult<GenerationOperation?>(null);
            if (Invisible) throw new InvalidDataException("Successor not searchable.");
            Refreshes++; MarketplaceGenerationOperationRecovery.RequireSuccessor(op, current);
            return Task.FromResult<GenerationOperation?>(current);
        }
    }
}
