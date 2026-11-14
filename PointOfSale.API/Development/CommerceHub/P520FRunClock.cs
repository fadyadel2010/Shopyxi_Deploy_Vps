using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PointOfSale.Core.Entities;

namespace PointOfSale.API.Development.CommerceHub;

/// <summary>
/// Not a global TimeProvider override. Only orders enrolled at canonical checkout
/// in this process can observe virtual time. Restart/disabled configuration loses
/// execution authority; committed audit provenance remains readable.
/// </summary>
public sealed class P520FRunClock(IWebHostEnvironment environment, IConfiguration configuration, IHttpContextAccessor http)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _advanceGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _orders = new();
    private readonly ConcurrentDictionary<Guid, Guid> _subOrders = new();
    private DateTime _now = new(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly string _runId = configuration["CommerceHub:DevelopmentAcceptance:Clock:RunId"] ?? "";
    public string RunId => _runId;
    private int MaximumAdvanceDays => RunId.StartsWith("P520G-", StringComparison.Ordinal) ? 31 : 30;
    public bool Enabled => configuration["CommerceHub:DevelopmentAcceptance:Clock:RunId"] == _runId && IsAdmitted(environment.EnvironmentName, RunId,
        configuration["CommerceHub:DevelopmentAcceptance:Clock:OptIn"]);
    public static bool IsAdmitted(string? environment, string? run, string? optIn) =>
        environment == Environments.Development && run != null &&
        Regex.IsMatch(run, @"\A(?:P520F-20261028|P520G-20261029)-[A-Z0-9]{8,32}\z") && optIn == "ALLOW-SYNTHETIC-TIME:" + run;
    public bool Owns(Guid orderId) => Enabled && _orders.ContainsKey(orderId);
    public void EnsureExecutionScope(CommerceHubMarketplaceOrder order)
    {
        if ((order.Notes?.StartsWith("P520F-", StringComparison.Ordinal) == true ||
             order.Notes?.StartsWith("P520G-", StringComparison.Ordinal) == true) && !Owns(order.Id))
            throw new PointOfSale.API.Services.CommerceHub.CommerceHubCheckoutException("P520F_CLOCK_EXECUTION_SCOPE_REQUIRED",
                "This simulated-time fixture cannot execute outside its original opted-in process/run.", 409);
    }
    public DateTime ForOrder(Guid orderId) => Owns(orderId) ? VirtualUtc : DateTime.UtcNow;
    public DateTime ForSubOrder(Guid id) => _subOrders.TryGetValue(id, out var parent) ? ForOrder(parent) : DateTime.UtcNow;
    public DateTime VirtualUtc { get { lock (_gate) return _now; } }
    public DateTime CreationUtc(string? notes) => CanEnroll(notes) ? VirtualUtc : DateTime.UtcNow;
    private bool CanEnroll(string? notes) => Enabled &&
        http.HttpContext?.Request.Headers["X-CommerceHub-Acceptance-Run"].ToString() == RunId &&
        notes?.StartsWith(RunId + " | ", StringComparison.Ordinal) == true;
    public bool EnrollmentRequested(string? notes) => CanEnroll(notes);

    public bool Enroll(CommerceHubMarketplaceOrder order, IEnumerable<string> vendorNames, string? customerEmail)
    {
        if (!CanEnroll(order.Notes)) return false;
        if (order.Id == Guid.Empty || order.Currency != "EGP" || order.TotalAmount <= 0 ||
            customerEmail?.StartsWith("p520d-20261028-e2e01-", StringComparison.OrdinalIgnoreCase) != true ||
            !customerEmail.EndsWith("@example.test", StringComparison.OrdinalIgnoreCase) ||
            !vendorNames.Any() || vendorNames.Any(n => !n.StartsWith("P520D-20261028-E2E01-", StringComparison.Ordinal)))
            throw new InvalidOperationException("P520F clock enrollment ownership is invalid.");
        if (!_orders.TryAdd(order.Id, 0)) throw new InvalidOperationException("Clock enrollment is immutable.");
        foreach (var subOrder in order.SubOrders) _subOrders.TryAdd(subOrder.Id, order.Id);
        return true;
    }

    public object Advance(string runId, DateTime expectedUtc, DateTime targetUtc)
    {
        if (!Enabled || runId != RunId) throw new InvalidOperationException("P520F clock is disabled or run scope is invalid.");
        lock (_gate)
        {
            if (expectedUtc != _now || targetUtc.Kind != DateTimeKind.Utc || targetUtc < _now || targetUtc > _started.AddDays(MaximumAdvanceDays))
                throw new InvalidOperationException("Clock advance is stale, non-monotonic or exceeds the run's bounded test duration.");
            _now = targetUtc;
            return Provenance();
        }
    }
    public async Task<object> AdvanceAuditedAsync(string runId, DateTime expectedUtc, DateTime targetUtc,
        Func<object, Task> persistAudit, CancellationToken ct)
    {
        await _advanceGate.WaitAsync(ct);
        try
        {
            if (!Enabled || runId != RunId || expectedUtc != VirtualUtc || targetUtc.Kind != DateTimeKind.Utc ||
                targetUtc < expectedUtc || targetUtc > _started.AddDays(MaximumAdvanceDays))
                throw new InvalidOperationException("Clock advance is disabled, foreign, stale, non-monotonic or out of bounds.");
            var evidence = new { RunId, IsSimulatedTime = true, VirtualUtc = targetUtc, RealUtc = DateTime.UtcNow,
                ExternalMoneyMovementConfirmed = false, EnrolledOrderIds = _orders.Keys.Order().ToArray() };
            // Failure to durably record evidence must not advance execution time.
            await persistAudit(evidence);
            return Advance(runId, expectedUtc, targetUtc);
        }
        finally { _advanceGate.Release(); }
    }
    public object Provenance() => new { RunId, IsSimulatedTime = true, VirtualUtc, RealUtc = DateTime.UtcNow,
        ExternalMoneyMovementConfirmed = false, EnrolledOrderIds = _orders.Keys.Order().ToArray() };

    // Only the authenticated resume command may restore a durable enrollment.
    // It validates immutable checkout/audit lineage before calling this method.
    internal void RestoreAuditedEnrollment(string run, CommerceHubMarketplaceOrder order, DateTime auditedUtc)
    {
        if (!Enabled || run != RunId || order.Notes?.StartsWith(run + " | ", StringComparison.Ordinal) != true ||
            auditedUtc.Kind !=DateTimeKind.Utc || auditedUtc < VirtualUtc || auditedUtc > _started.AddDays(MaximumAdvanceDays))
            throw new InvalidOperationException("Audited clock enrollment cannot be restored.");
        lock (_gate)
        {
            _now = auditedUtc;
            _orders.TryAdd(order.Id, 0);
            foreach (var sub in order.SubOrders) _subOrders.TryAdd(sub.Id, order.Id);
        }
    }
}
