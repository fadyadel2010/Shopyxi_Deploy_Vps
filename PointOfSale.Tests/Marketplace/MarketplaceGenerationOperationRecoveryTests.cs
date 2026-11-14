using System.Text.Json;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

/// <summary>Deterministic boundary SIMULATION. No PostgreSQL/OpenSearch client or certification.</summary>
public sealed class MarketplaceGenerationOperationRecoveryTests
{
    [Fact]
    public async Task AppliedThenConnectionDropsReconcilesOriginalReceiptWithoutResubmission()
    {
        var f = new Fixture(); var operation = f.Operation(); f.Provider.DropAfterApply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Engine.DeliverAsync(operation));
        Assert.Equal(GenerationOperationState.OutcomeUnknown, f.Store.Current(operation).State);
        Assert.Equal(1, f.Provider.Mutations);
        var recovered = await f.Restart().RecoverAsync(operation.Id);
        Assert.Equal(GenerationOperationState.Acknowledged, recovered.State);
        Assert.Equal(1, f.Provider.Submissions); Assert.Equal(1, f.Provider.Refreshes);
    }

    [Fact]
    public async Task TimeoutDuringProcessingRemainsUnknownUntilAtomicBoundaryCompletes()
    {
        var f = new Fixture(); var operation = f.Operation(); f.Provider.Processing = true;
        await Assert.ThrowsAsync<TimeoutException>(() => f.Engine.DeliverAsync(operation));
        Assert.Equal(GenerationOperationState.OutcomeUnknown, (await f.Restart().RecoverAsync(operation.Id)).State);
        Assert.Equal(1, f.Provider.Submissions); Assert.Equal(0, f.Provider.Refreshes);
        f.Provider.Processing = false; f.Provider.CompleteProcessing(operation);
        Assert.Equal(GenerationOperationState.Acknowledged, (await f.Restart().RecoverAsync(operation.Id)).State);
    }

    [Fact]
    public async Task CrashAfterSuccessBeforeDurableAppliedEventUsesOriginalOperationReceipt()
    {
        var f = new Fixture(); var operation = f.Operation(); f.Store.FailNext = GenerationOperationState.Applied;
        await Assert.ThrowsAsync<IOException>(() => f.Engine.DeliverAsync(operation));
        Assert.Equal(GenerationOperationState.Submitted, f.Store.Current(operation).State);
        Assert.Equal(GenerationOperationState.Acknowledged, (await f.Restart().RecoverAsync(operation.Id)).State);
        Assert.Equal(1, f.Provider.Mutations); Assert.Equal(1, f.Provider.Submissions);
    }

    [Theory]
    [InlineData("VisibleAfterRefresh")]
    [InlineData("Acknowledged")]
    public async Task RefreshSuccessButSqlEventFailureRequiresVisibilityReverification(string failedEvent)
    {
        var f = new Fixture(); var operation = f.Operation(); f.Store.FailNext = Enum.Parse<GenerationOperationState>(failedEvent);
        await Assert.ThrowsAsync<IOException>(() => f.Engine.DeliverAsync(operation));
        Assert.NotEqual(GenerationOperationState.Acknowledged, f.Store.Current(operation).State);
        Assert.Equal(GenerationOperationState.Acknowledged, (await f.Restart().RecoverAsync(operation.Id)).State);
        Assert.Equal(2, f.Provider.Refreshes); Assert.Equal(1, f.Provider.Mutations);
    }

    [Fact]
    public async Task UnknownNotAppliedRetriesSameIdentityOnlyThroughAtomicIdempotencyBoundary()
    {
        var f = new Fixture(); var operation = f.Operation(); f.Provider.DropBeforeApply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Engine.DeliverAsync(operation));
        Assert.Equal(GenerationOperationState.Acknowledged, (await f.Restart().RecoverAsync(operation.Id)).State);
        Assert.Equal(2, f.Provider.Submissions); Assert.Equal(1, f.Provider.Mutations);
        // A delayed duplicate original cannot diverge or apply a second time.
        Assert.Equal(GenerationOperationState.Applied, (await f.Provider.SubmitAsync(operation, default)).State);
        Assert.Equal(1, f.Provider.Mutations);
    }

    [Fact]
    public async Task DuplicateSameDigestUsesDurableAcknowledgementWithoutAnotherWrite()
    {
        var f = new Fixture(); var operation = f.Operation();
        await f.Engine.DeliverAsync(operation); await f.Restart().DeliverAsync(operation);
        Assert.Equal(1, f.Provider.Submissions); Assert.Equal(1, f.Provider.Mutations);
        Assert.Equal(new[] { GenerationOperationState.Submitted, GenerationOperationState.Applied,
            GenerationOperationState.VisibleAfterRefresh, GenerationOperationState.Acknowledged }, f.Store.Events);
    }

    [Fact]
    public async Task SameOperationDifferentDigestIsRejectedBeforeAnyExternalCall()
    {
        var f = new Fixture(); var operation = f.Operation(); await f.Engine.DeliverAsync(operation);
        var payload = JsonSerializer.Serialize(new { serviceId = operation.Ticket.ServiceId, title = "conflicting" });
        var changed = operation with { Payload = payload, Ticket = operation.Ticket with
            { Digest = MarketplaceFrozenOutput.RowDigest(operation.Ticket.ServiceId, payload) } };
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Restart().DeliverAsync(changed));
        Assert.Equal(1, f.Provider.Submissions);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Provider.SubmitAsync(changed, default));
    }

    [Fact]
    public async Task NewerTombstoneBeforeOlderUnknownRecoveryCannotRestoreStaleContent()
    {
        var f = new Fixture(); var older = f.Operation(); f.Provider.DropBeforeApply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Engine.DeliverAsync(older));
        var newer = f.Operation(older.Ticket.ServiceId, revision: 2, deleted: true);
        await f.Engine.DeliverAsync(newer);
        Assert.Equal(GenerationOperationState.ConfirmedRejected, (await f.Restart().RecoverAsync(older.Id)).State);
        Assert.Null(f.Provider.Documents[older.Ticket.ServiceId].Payload);
        Assert.Equal(2, f.Provider.Documents[older.Ticket.ServiceId].Ticket.SourceRevision);
        Assert.Equal(1, f.Provider.Mutations);
    }

    [Fact]
    public async Task AppliedButSupersededUnknownDoesNotInventHistoricalSearchVisibility()
    {
        var f = new Fixture(); var older = f.Operation(); f.Provider.DropAfterApply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Engine.DeliverAsync(older));
        await f.Engine.DeliverAsync(f.Operation(older.Ticket.ServiceId, revision: 2));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Restart().RecoverAsync(older.Id));
        Assert.Equal(GenerationOperationState.Applied, f.Store.Current(older).State);
    }

    [Fact]
    public async Task ConflictingSourceRevisionWithDifferentOperationCannotEnterStore()
    {
        var f = new Fixture(); var first = f.Operation(); await f.Engine.DeliverAsync(first);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Engine.DeliverAsync(f.Operation(first.Ticket.ServiceId)));
        Assert.Equal(1, f.Provider.Submissions);
    }

    [Theory]
    [InlineData("cluster")]
    [InlineData("uuid")]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("revision")]
    [InlineData("digest")]
    public async Task ForeignProviderReceiptCannotAcknowledge(string field)
    {
        var f = new Fixture(); var operation = f.Operation();
        f.Provider.AlterReceipt = op => field switch
        {
            "cluster" => op with { Target = op.Target with { ClusterUuid = "foreign" } },
            "uuid" => op with { Target = op.Target with { PhysicalUuid = "foreign" } },
            "generation" => op with { Target = op.Target with { Generation = Guid.NewGuid() } },
            "epoch" => op with { Ticket = op.Ticket with { Epoch = 2 } },
            "revision" => op with { Ticket = op.Ticket with { SourceRevision = 2 } },
            _ => op with { Ticket = op.Ticket with { Digest = new string('f', 64) } }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Engine.DeliverAsync(operation));
        Assert.NotEqual(GenerationOperationState.Acknowledged, f.Store.Current(operation).State);
        Assert.Equal(0, f.Provider.Refreshes);
    }

    [Fact]
    public async Task SuccessReplyAndRefreshAloneAreNotSearchableVisibility()
    {
        var f = new Fixture(); var operation = f.Operation(); f.Provider.Invisible = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Engine.DeliverAsync(operation));
        Assert.Equal(GenerationOperationState.Applied, f.Store.Current(operation).State);
        f.Provider.Invisible = false;
        Assert.Equal(GenerationOperationState.Acknowledged, (await f.Restart().RecoverAsync(operation.Id)).State);
        Assert.Equal(1, f.Provider.Mutations);
    }

    [Fact]
    public async Task DurableSubmittedStatePrecedesEverySubmission()
    {
        var f = new Fixture(); var operation = f.Operation();
        f.Provider.BeforeSubmit = op => Assert.Equal(GenerationOperationState.Submitted, f.Store.Current(op).State);
        await f.Engine.DeliverAsync(operation);
    }

    [Fact]
    public void OperationSerializationPreservesPayloadAndInt64Identity()
    {
        var f = new Fixture(); var operation = f.Operation(revision: long.MaxValue);
        var restored = JsonSerializer.Deserialize<GenerationOperation>(JsonSerializer.Serialize(operation))!;
        MarketplaceGenerationOperationRecovery.RequireSame(operation, restored);
        Assert.Equal(long.MaxValue, restored.Ticket.SourceRevision);
    }

    [Theory]
    [InlineData("Pending", "Acknowledged")]
    [InlineData("Submitted", "Acknowledged")]
    [InlineData("OutcomeUnknown", "Acknowledged")]
    [InlineData("Applied", "Acknowledged")]
    [InlineData("Acknowledged", "Submitted")]
    [InlineData("ConfirmedRejected", "Submitted")]
    public void IllegalShortcutsAndTerminalReopenAreRejected(string from, string to) =>
        Assert.Throws<InvalidOperationException>(() => MarketplaceGenerationOperationRecovery.RequireTransition(
            Enum.Parse<GenerationOperationState>(from), Enum.Parse<GenerationOperationState>(to)));

    internal sealed class Fixture
    {
        internal readonly MemoryStore Store = new();
        internal readonly AtomicBoundarySimulation Provider = new();
        internal readonly MarketplacePhysicalAdmission.Expected Target = new(Guid.NewGuid(), Guid.NewGuid(), "cluster", "physical", new string('a', 64));
        internal MarketplaceGenerationOperationRecovery Engine => Restart();
        internal MarketplaceGenerationOperationRecovery Restart() => new(Store, Provider);
        internal GenerationOperation Operation(Guid? service = null, long revision = 1, bool deleted = false, long epoch = 1)
        {
            var id = service ?? Guid.NewGuid();
            var payload = deleted ? null : JsonSerializer.Serialize(new { serviceId = id, title = "immutable" });
            return new(Target, new(Guid.NewGuid(), Guid.NewGuid(), Target.Generation, Guid.NewGuid(), id, revision,
                Target.PhysicalUuid, Target.Fingerprint, MarketplaceFrozenOutput.RowDigest(id, payload), epoch), payload);
        }
    }

    internal sealed class MemoryStore : IGenerationOperationStore
    {
        private readonly Dictionary<Guid, GenerationOperationSnapshot> rows = new();
        internal readonly List<GenerationOperationState> Events = new();
        internal GenerationOperationState? FailNext;
        internal GenerationOperationSnapshot Current(GenerationOperation operation) => rows[operation.Id];
        public Task<GenerationOperationSnapshot> AdmitAsync(GenerationOperation operation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); operation.Validate();
            if (rows.TryGetValue(operation.Id, out var existing))
            { MarketplaceGenerationOperationRecovery.RequireSame(existing.Operation, operation); return Task.FromResult(existing); }
            if (rows.Values.Any(row => row.Operation.Target.Generation == operation.Target.Generation &&
                row.Operation.Ticket.ServiceId == operation.Ticket.ServiceId && row.Operation.Ticket.SourceRevision == operation.Ticket.SourceRevision))
                throw new InvalidDataException("Source identity/revision already bound to another operation.");
            var copy = JsonSerializer.Deserialize<GenerationOperation>(JsonSerializer.Serialize(operation))!;
            var result = new GenerationOperationSnapshot(copy, 0, GenerationOperationState.Pending); rows.Add(operation.Id, result);
            return Task.FromResult(result);
        }
        public Task<GenerationOperationSnapshot> ReadAsync(Guid operation, CancellationToken ct) => Task.FromResult(rows[operation]);
        public Task<GenerationOperationSnapshot> AdvanceAsync(GenerationOperationSnapshot expected, GenerationOperationState next, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailNext == next) { FailNext = null; throw new IOException("Injected durable transaction failure."); }
            MarketplaceGenerationOperationRecovery.RequireTransition(expected.State, next);
            if (rows[expected.Operation.Id] != expected) throw new InvalidOperationException("CAS conflict.");
            var result = expected with { Version = expected.Version + 1, State = next };
            rows[expected.Operation.Id] = result; Events.Add(next); return Task.FromResult(result);
        }
    }

    /// <summary>Models a hypothetical server-side atomic ledger. NOT stock OpenSearch.</summary>
    internal sealed class AtomicBoundarySimulation : IGenerationAtomicWriteBoundary
    {
        internal long Epoch = 1;
        internal bool DropAfterApply, DropBeforeApply, Processing, Invisible;
        internal int Submissions, Mutations, Refreshes;
        internal Func<GenerationOperation, GenerationOperation>? AlterReceipt;
        internal Action<GenerationOperation>? BeforeSubmit;
        internal readonly Dictionary<Guid, GenerationOperation> Documents = new();
        private readonly Dictionary<Guid, GenerationBoundaryReceipt> receipts = new();
        public Task<GenerationBoundaryReceipt?> LookupAsync(GenerationOperation operation, CancellationToken ct)
        {
            if (!receipts.TryGetValue(operation.Id, out var receipt)) return Task.FromResult<GenerationBoundaryReceipt?>(null);
            MarketplaceGenerationOperationRecovery.RequireSame(receipt.Operation, operation);
            return Task.FromResult<GenerationBoundaryReceipt?>(receipt);
        }
        public Task<GenerationBoundaryReceipt> SubmitAsync(GenerationOperation operation, CancellationToken ct)
        {
            BeforeSubmit?.Invoke(operation); Submissions++;
            if (receipts.TryGetValue(operation.Id, out var prior))
            { MarketplaceGenerationOperationRecovery.RequireSame(prior.Operation, operation); return Task.FromResult(prior); }
            if (DropBeforeApply) { DropBeforeApply = false; throw new IOException("Dropped before remote apply."); }
            if (Processing)
            {
                receipts.Add(operation.Id, new(operation, GenerationOperationState.OutcomeUnknown));
                throw new TimeoutException("Provider is still processing; not a rejection.");
            }
            var receipt = Apply(operation);
            if (DropAfterApply) { DropAfterApply = false; throw new IOException("Applied, response lost."); }
            return Task.FromResult(AlterReceipt is null ? receipt : receipt with { Operation = AlterReceipt(operation) });
        }
        internal void CompleteProcessing(GenerationOperation operation) => Apply(operation);
        private GenerationBoundaryReceipt Apply(GenerationOperation operation)
        {
            if (receipts.TryGetValue(operation.Id, out var prior) && prior.State != GenerationOperationState.OutcomeUnknown)
                return prior;
            var state = GenerationOperationState.Applied;
            if (operation.Ticket.Epoch != Epoch) state = GenerationOperationState.ConfirmedRejected;
            else if (Documents.TryGetValue(operation.Ticket.ServiceId, out var current))
            {
                if (current.Ticket.SourceRevision > operation.Ticket.SourceRevision) state = GenerationOperationState.ConfirmedRejected;
                else if (current.Ticket.SourceRevision == operation.Ticket.SourceRevision)
                {
                    if (current.Ticket.Digest != operation.Ticket.Digest) throw new InvalidDataException("Revision digest conflict.");
                    var duplicate = new GenerationBoundaryReceipt(operation, state); receipts[operation.Id] = duplicate; return duplicate;
                }
            }
            if (state == GenerationOperationState.Applied) { Documents[operation.Ticket.ServiceId] = operation; Mutations++; }
            var result = new GenerationBoundaryReceipt(operation, state); receipts[operation.Id] = result; return result;
        }
        public Task<GenerationBoundaryReceipt> RefreshAndVerifyAsync(GenerationOperation operation, CancellationToken ct)
        {
            Refreshes++;
            if (Invisible || !Documents.TryGetValue(operation.Ticket.ServiceId, out var current) || current != operation)
                throw new InvalidDataException("Refresh alone did not prove exact searchable identity/digest/revision.");
            return Task.FromResult(new GenerationBoundaryReceipt(operation, GenerationOperationState.VisibleAfterRefresh));
        }
    }
}
