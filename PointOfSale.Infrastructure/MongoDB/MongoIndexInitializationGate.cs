namespace PointOfSale.Infrastructure.MongoDB;

/// <summary>Publish completion only after all required index commands succeed.</summary>
internal sealed class MongoIndexInitializationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _complete;

    internal async Task EnsureAsync(Func<CancellationToken, Task> initialize, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_complete) return;
            await initialize(cancellationToken);
            _complete = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
