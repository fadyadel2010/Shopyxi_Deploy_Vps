namespace PointOfSale.Infrastructure.Diagnostics;

/// <summary>Safe persisted failure category; exception text is not a storage contract.</summary>
public static class SafeFailureDiagnostic
{
    public static string Describe(Exception exception) => exception switch
    {
        System.Security.Authentication.AuthenticationException => "TLS authentication failed.",
        System.Security.Cryptography.CryptographicException => "Certificate or cryptographic operation failed.",
        System.Data.Common.DbException => "Database operation failed.",
        System.Net.Http.HttpRequestException => "HTTP dependency request failed.",
        TimeoutException => "Dependency operation timed out.",
        OperationCanceledException => "Operation was cancelled.",
        UnauthorizedAccessException => "Resource access was denied.",
        System.IO.IOException => "Storage or transport operation failed.",
        _ => "Dependency operation failed; inspect the correlated event category and retry state."
    };
}
