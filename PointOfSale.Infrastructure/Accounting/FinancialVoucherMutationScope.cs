namespace PointOfSale.Infrastructure.Accounting;

public sealed class FinancialVoucherMutationScope : IDisposable
{
    private static readonly AsyncLocal<int> Depth = new();
    private bool disposed;
    public static bool IsAuthorized => Depth.Value > 0;
    private FinancialVoucherMutationScope() => Depth.Value++;
    public static IDisposable Authorize() => new FinancialVoucherMutationScope();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Depth.Value--;
    }
}
