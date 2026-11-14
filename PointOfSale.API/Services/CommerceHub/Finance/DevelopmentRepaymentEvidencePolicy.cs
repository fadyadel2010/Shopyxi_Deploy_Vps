namespace PointOfSale.API.Services.CommerceHub.Finance;

public static class DevelopmentRepaymentEvidencePolicy
{
    public static bool CanAccept(string? environment, bool enabled, string vendorName, string? reference) =>
        environment == Environments.Development && enabled &&
        vendorName.StartsWith("P520D-20261028-E2E01-", StringComparison.Ordinal) &&
        reference?.StartsWith("DEV-SIM:P520D-20261028-E2E01-", StringComparison.Ordinal) == true;
}
