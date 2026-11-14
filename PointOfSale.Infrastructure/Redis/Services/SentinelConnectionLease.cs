using System.Reflection;
using System.Runtime.ExceptionServices;
using StackExchange.Redis;

namespace PointOfSale.Infrastructure.Redis.Services;

/// <summary>
/// The SDK's explicit two-credential Sentinel API does not transfer monitor
/// ownership to the data connection. Forward the public interface and close both
/// resources when cache/SignalR/direct callers release their connection.
/// </summary>
public class SentinelConnectionLease : DispatchProxy
{
    private IConnectionMultiplexer _data = null!;
    private IDisposable _monitor = null!;
    private int _disposed;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public static IConnectionMultiplexer Create(IConnectionMultiplexer data, IDisposable monitor)
    {
        var proxy = Create<IConnectionMultiplexer, SentinelConnectionLease>();
        var lease = (SentinelConnectionLease)proxy;
        lease._data = data;
        lease._monitor = monitor;
        return proxy;
    }

    public void DisposeOwned()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _data.Dispose(); }
        finally { _monitor.Dispose(); }
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null) throw new InvalidOperationException();
        if (method.Name == nameof(IDisposable.Dispose)) { DisposeOwned(); return null; }
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        try
        {
            var result = method.Invoke(_data, args);
            if (method.Name == "CloseAsync" && result is Task task) return CloseAsync(task);
            if (method.Name == "Close") DisposeOwned();
            return result;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            if (method.Name == "Close") DisposeOwned();
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private async Task CloseAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        finally { DisposeOwned(); }
    }
}
