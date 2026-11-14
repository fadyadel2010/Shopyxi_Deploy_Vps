using System.Security.Cryptography;
using System.Text;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal enum GenerationOperationState
{
    Pending, Submitted, OutcomeUnknown, Applied, VisibleAfterRefresh, Acknowledged, ConfirmedRejected, SupersededAfterRefresh
}

/// <summary>Immutable wire identity. Epoch is authorization, revision is source order.</summary>
internal sealed record GenerationOperation(MarketplacePhysicalAdmission.Expected Target, ExternalWriteTicket Ticket, string? Payload)
{
    internal Guid Id => Ticket.OperationId;
    internal string RequestDigest => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        MarketplaceVersionedDocument.BuildUpdate(Target.Journal, Ticket, Payload))));

    internal void Validate()
    {
        MarketplacePhysicalAdmission.Validate(Target);
        if (Id == Guid.Empty || Ticket.CompletionToken == Guid.Empty || Ticket.RunId == Guid.Empty ||
            Ticket.GenerationId != Target.Generation || Ticket.PhysicalUuid != Target.PhysicalUuid || Ticket.Fingerprint != Target.Fingerprint)
            throw new InvalidDataException("Exact immutable operation/target lineage required.");
        _ = RequestDigest; // Validates payload identity, source revision and sealed digest.
    }
}

internal sealed record GenerationOperationSnapshot(GenerationOperation Operation, long Version, GenerationOperationState State);
internal sealed record GenerationBoundaryReceipt(GenerationOperation Operation, GenerationOperationState State, GenerationOperation? Successor = null);

/// <summary>
/// NOT an ordinary OpenSearch client. Implementations must enforce common durable
/// epoch admission and immutable idempotency across ALL requests. A gateway may
/// drain hazardous attempts before epoch advancement instead of requiring a provider
/// multi-document transaction. Absence alone never settles an earlier request.
/// All independent writers must be denied raw provider access; retained document
/// fences and exclusive lifecycle authority are prerequisites of the gateway model.
/// </summary>
internal interface IGenerationAtomicWriteBoundary
{
    Task<GenerationBoundaryReceipt?> LookupAsync(GenerationOperation operation, CancellationToken ct);
    Task<GenerationBoundaryReceipt> SubmitAsync(GenerationOperation operation, CancellationToken ct);
    // Refresh AND exact searchable document/envelope/digest verification, not HTTP 200.
    Task<GenerationBoundaryReceipt> RefreshAndVerifyAsync(GenerationOperation operation, CancellationToken ct);
}

internal interface IGenerationOperationStore
{
    Task<GenerationOperationSnapshot> AdmitAsync(GenerationOperation operation, CancellationToken ct);
    Task<GenerationOperationSnapshot> ReadAsync(Guid operation, CancellationToken ct);
    Task<GenerationOperationSnapshot> AdvanceAsync(GenerationOperationSnapshot expected, GenerationOperationState next, CancellationToken ct);
}

/// <summary>
/// Durable recovery algorithm, deliberately unregistered. SQL persistence and an
/// actually enforced atomic boundary are both mandatory; never fall back to raw HTTP.
/// It does not cancel or acknowledge any legacy delivery_attempts.
/// </summary>
internal sealed class MarketplaceGenerationOperationRecovery(IGenerationOperationStore store, IGenerationAtomicWriteBoundary boundary)
{
    internal async Task<GenerationOperationSnapshot> DeliverAsync(GenerationOperation operation, CancellationToken ct = default)
    {
        operation.Validate();
        var current = await store.AdmitAsync(operation, ct);
        RequireSame(operation, current.Operation);
        return await RecoverAsync(operation.Id, ct);
    }

    internal async Task<GenerationOperationSnapshot> RecoverAsync(Guid operation, CancellationToken ct = default)
        => await RecoverAsync(operation, allowSubmission: true, ct);

    // Ownership recovery must establish provider truth BEFORE a successor epoch.
    // It may append verified outcomes, but can never turn missing evidence into
    // a retry or a rejection, even if an old owner's lease is still current.
    internal Task<GenerationOperationSnapshot> ReconcileAsync(Guid operation, CancellationToken ct = default)
        => RecoverAsync(operation, allowSubmission: false, ct);

    private async Task<GenerationOperationSnapshot> RecoverAsync(Guid operation, bool allowSubmission, CancellationToken ct)
    {
        var current = await store.ReadAsync(operation, ct);
        current.Operation.Validate();
        if (current.State is GenerationOperationState.Acknowledged or GenerationOperationState.ConfirmedRejected or GenerationOperationState.SupersededAfterRefresh) return current;
        if (current.State == GenerationOperationState.Submitted)
            current = await store.AdvanceAsync(current, GenerationOperationState.OutcomeUnknown, ct); // Restart/crash, not a rejection.
        if (!allowSubmission && current.State == GenerationOperationState.Pending) return current;
        if (current.State is GenerationOperationState.Pending or GenerationOperationState.OutcomeUnknown)
        {
            var receipt = current.State == GenerationOperationState.OutcomeUnknown
                ? await boundary.LookupAsync(current.Operation, ct) : null;
            if (receipt is null)
            {
                if (!allowSubmission) return current; // Absence is not a definite transport outcome.
                current = await store.AdvanceAsync(current, GenerationOperationState.Submitted, ct); // Commit BEFORE external submission.
                try { receipt = await boundary.SubmitAsync(current.Operation, ct); }
                catch (Exception)
                {
                    // Cancellation does not erase an external request. Failure to persist
                    // Unknown leaves Submitted, which restart always treats as Unknown.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await store.AdvanceAsync(current, GenerationOperationState.OutcomeUnknown, cleanup.Token);
                    throw;
                }
            }
            RequireSame(current.Operation, receipt.Operation);
            if (receipt.State == GenerationOperationState.SupersededAfterRefresh)
            {
                RequireSuccessor(current.Operation, receipt.Successor);
                return await store.AdvanceAsync(current, GenerationOperationState.SupersededAfterRefresh, ct);
            }
            if (receipt.State == GenerationOperationState.OutcomeUnknown)
                return current.State == GenerationOperationState.OutcomeUnknown ? current :
                    await store.AdvanceAsync(current, GenerationOperationState.OutcomeUnknown, ct);
            if (receipt.State is not (GenerationOperationState.Applied or GenerationOperationState.ConfirmedRejected))
                throw new InvalidDataException("A write response cannot assert searchable visibility or acknowledgement.");
            current = await store.AdvanceAsync(current, receipt.State, ct);
        }
        if (current.State == GenerationOperationState.ConfirmedRejected) return current;
        // Even a previously persisted visibility observation is rechecked after restart:
        // never acknowledge based only on a prior refresh or success response.
        var visible = await boundary.RefreshAndVerifyAsync(current.Operation, ct);
        RequireSame(current.Operation, visible.Operation);
        if (visible.State == GenerationOperationState.SupersededAfterRefresh)
        {
            RequireSuccessor(current.Operation, visible.Successor);
            return await store.AdvanceAsync(current, GenerationOperationState.SupersededAfterRefresh, ct);
        }
        if (visible.State != GenerationOperationState.VisibleAfterRefresh)
            throw new InvalidDataException("Exact refreshed visibility not established; acknowledgement refused.");
        if (current.State == GenerationOperationState.Applied)
            current = await store.AdvanceAsync(current, GenerationOperationState.VisibleAfterRefresh, ct);
        return await store.AdvanceAsync(current, GenerationOperationState.Acknowledged, ct);
    }

    internal static void RequireSame(GenerationOperation expected, GenerationOperation actual)
    {
        if (expected != actual || expected.RequestDigest != actual.RequestDigest)
            throw new InvalidDataException("Operation identity, physical target, revision, epoch or immutable digest conflict.");
    }

    internal static void RequireTransition(GenerationOperationState from, GenerationOperationState to)
    {
        if (to == GenerationOperationState.SupersededAfterRefresh && from is
            (GenerationOperationState.Submitted or GenerationOperationState.OutcomeUnknown or GenerationOperationState.Applied or GenerationOperationState.VisibleAfterRefresh or GenerationOperationState.Acknowledged)) return;
        if ((from, to) is not (
            (GenerationOperationState.Pending, GenerationOperationState.Submitted) or
            (GenerationOperationState.Submitted, GenerationOperationState.OutcomeUnknown) or
            (GenerationOperationState.Submitted, GenerationOperationState.Applied) or
            (GenerationOperationState.Submitted, GenerationOperationState.ConfirmedRejected) or
            (GenerationOperationState.OutcomeUnknown, GenerationOperationState.Submitted) or
            (GenerationOperationState.OutcomeUnknown, GenerationOperationState.Applied) or
            (GenerationOperationState.OutcomeUnknown, GenerationOperationState.ConfirmedRejected) or
            (GenerationOperationState.Applied, GenerationOperationState.VisibleAfterRefresh) or
            (GenerationOperationState.VisibleAfterRefresh, GenerationOperationState.Acknowledged)))
            throw new InvalidOperationException("Illegal durable operation transition.");
    }

    internal static void RequireSuccessor(GenerationOperation operation, GenerationOperation? successor)
    {
        if (successor is null) throw new InvalidDataException("Refreshed successor identity required.");
        successor.Validate();
        if (successor.Target != operation.Target || successor.Ticket.ServiceId != operation.Ticket.ServiceId ||
            successor.Ticket.SourceRevision <= operation.Ticket.SourceRevision || successor.Ticket.Epoch < operation.Ticket.Epoch)
            throw new InvalidDataException("Only an exact retained higher source revision can supersede an operation.");
    }
}
