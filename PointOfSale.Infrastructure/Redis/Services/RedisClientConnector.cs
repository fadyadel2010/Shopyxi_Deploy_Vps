using PointOfSale.Infrastructure.Redis.Configuration;
using StackExchange.Redis;

namespace PointOfSale.Infrastructure.Redis.Services;

/// <summary>
/// All three API Redis paths use this topology-aware connector. Each caller owns its
/// data multiplexer; the connector keeps its independent Sentinel monitor alive and
/// disposes monitor/data pairs at shutdown. No credentials or SDK diagnostics logged.
/// </summary>
public sealed class RedisClientConnector : IDisposable
{
    private readonly object _gate = new();
    private readonly List<SentinelConnectionLease> _leases = [];
    private readonly List<(RedisConnectionPlan Plan, IConnectionMultiplexer Connection)> _connections = [];
    private bool _disposed;

    public IConnectionMultiplexer Connect(RedisSettings settings)
    {
        var plan = RedisConnectionPlan.Create(settings);
        try
        {
            var connection = plan.Sentinel is null ? ConnectionMultiplexer.Connect(plan.Data) :
                Attach(ConnectionMultiplexer.SentinelConnect(plan.Sentinel), plan.Data);
            return Own(plan, connection);
        }
        catch { plan.Dispose(); throw new InvalidOperationException("Redis TLS connection unavailable; details redacted."); }
    }

    public async Task<IConnectionMultiplexer> ConnectAsync(RedisSettings settings, RedisChannel? channelPrefix = null)
    {
        var plan = RedisConnectionPlan.Create(settings, channelPrefix);
        try
        {
            var connection = plan.Sentinel is null ? await ConnectionMultiplexer.ConnectAsync(plan.Data).ConfigureAwait(false) :
                Attach(await ConnectionMultiplexer.SentinelConnectAsync(plan.Sentinel).ConfigureAwait(false), plan.Data);
            return Own(plan, connection);
        }
        catch { plan.Dispose(); throw new InvalidOperationException("Redis TLS connection unavailable; details redacted."); }
    }

    private IConnectionMultiplexer Own(RedisConnectionPlan plan, IConnectionMultiplexer connection)
    {
        lock (_gate)
        {
            if (_disposed) { connection.Dispose(); throw new ObjectDisposedException(nameof(RedisClientConnector)); }
            _connections.Add((plan, connection));
            return connection;
        }
    }

    private IConnectionMultiplexer Attach(ConnectionMultiplexer monitor, ConfigurationOptions dataOptions)
    {
        ConnectionMultiplexer? data = null;
        try
        {
            data = monitor.GetSentinelMasterConnection(dataOptions);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _leases.RemoveAll(lease => lease.IsDisposed);
                var lease = SentinelConnectionLease.Create(data, monitor);
                _leases.Add((SentinelConnectionLease)lease);
                return lease;
            }
        }
        catch
        {
            data?.Dispose();
            monitor.Dispose();
            throw new InvalidOperationException("Redis Sentinel connection unavailable; connection details redacted.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var lease in _leases) lease.DisposeOwned();
            _leases.Clear();
            foreach (var (plan, connection) in _connections) { connection.Dispose(); plan.Dispose(); }
            _connections.Clear();
        }
    }
}
