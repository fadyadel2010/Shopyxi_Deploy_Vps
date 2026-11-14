namespace PointOfSale.Infrastructure.Accounting;

/// <summary>Trusted canonical workflow write authority, never derived from a request header.</summary>
public sealed class InventoryWorkflowMutationScope : IDisposable
{
    private static readonly AsyncLocal<int> Depth = new();
    public static bool IsAuthorized => Depth.Value > 0;
    private bool disposed;
    private InventoryWorkflowMutationScope() => Depth.Value++;
    public static IDisposable Authorize() => new InventoryWorkflowMutationScope();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Depth.Value--;
    }
}
