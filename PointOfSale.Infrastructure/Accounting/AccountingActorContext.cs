namespace PointOfSale.Infrastructure.Accounting;

/// <summary>
/// Scoped server authority, populated only after SQL Admin authorization or from
/// a durable accounting outbox envelope. Never derives identity from headers,
/// request DTOs, an invoice's customer, or the worker's own identity.
/// </summary>
public sealed class AccountingActorContext
{
    private (Guid TenantId, Guid? UserId)? _actor;
    private DateTime? _sourceDate;

    public IDisposable UseActor(Guid tenantId, Guid? userId, DateTime? sourceDate = null)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
            throw new ArgumentException("Accounting actor identifiers must not be empty.");
        var previous = _actor;
        var previousDate = _sourceDate;
        if (previous.HasValue && previous.Value.TenantId != tenantId)
            throw new InvalidOperationException("Accounting actor cannot cross tenant boundaries.");
        _actor = (tenantId, userId);
        _sourceDate = sourceDate;
        return new RestoreScope(() => { _actor = previous; _sourceDate = previousDate; });
    }

    // Clock-based adapters must retain the durable event date on worker replay.
    public DateTime ResolvePostingDate() => _sourceDate ?? DateTime.UtcNow;

    public Guid? ResolveUserId(Guid tenantId, Guid? sourceUserId = null)
    {
        if (_actor.HasValue)
        {
            if (_actor.Value.TenantId != tenantId)
                throw new InvalidOperationException("Accounting actor tenant does not match the posting tenant.");
            return _actor.Value.UserId;
        }
        return sourceUserId == Guid.Empty ? null : sourceUserId;
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
