using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Exceptions.Affiliate;

namespace PointOfSale.API.Controllers;

internal sealed record AffiliateFinancialError(string Code, string Message, int StatusCode);

internal static class AffiliateFinancialErrorContract
{
    public static AffiliateFinancialError From(Exception exception)
    {
        if (exception is DbUpdateConcurrencyException)
        {
            return new(
                "AFFILIATE_CONCURRENCY_CONFLICT",
                "The affiliate financial state changed concurrently. Refresh and retry.",
                StatusCodes.Status409Conflict);
        }

        var domainCode = ExtractDomainCode(exception.Message);
        if (domainCode is not null)
        {
            var guidance = domainCode switch
            {
                "AFFILIATE_PAYOUT_WALLET_REQUIRED" => "Register a payout wallet in Affiliate Settings before requesting a withdrawal. Your commission balance has not changed.",
                "AFFILIATE_PAYOUT_WALLET_CHANGED_AFTER_REQUEST" => "The payout wallet changed after this request. Ask an administrator to reject it, then submit a new request for the updated destination.",
                "AFFILIATE_PAYOUT_WALLET_SNAPSHOT_REQUIRED" => "This request has no approved payout destination snapshot. Ask an administrator to reject it, then submit a new request.",
                "AFFILIATE_PAYOUT_INSUFFICIENT_ELIGIBLE_FUNDS" => "The available commission changed or is reserved by another withdrawal. Refresh the balance and withdrawal history before retrying.",
                "AFFILIATE_PAYOUT_STORE_REQUIRED" => "Eligible commission exists in more than one store. Select a store before requesting a withdrawal.",
                "AFFILIATE_PAYOUT_BELOW_MINIMUM" => "The requested amount is below the withdrawal minimum.",
                "AFFILIATE_PAYOUT_INVALID_PRECISION" => "Use the currency's supported decimal places for the withdrawal amount.",
                "AFFILIATE_PAYOUT_ACCOUNT_INACTIVE" => "This affiliate account is not active. Contact support before requesting a withdrawal.",
                "AFFILIATE_PAYOUT_REQUEST_STATUS_INVALID" => "This withdrawal is no longer in the required state. Refresh its history before continuing.",
                "AFFILIATE_PAYOUT_EXTERNAL_CONFIRMATION_REQUIRED" => "Commission approval is not payment confirmation. Use an approved withdrawal and verified external transfer evidence before marking it paid.",
                "AFFILIATE_PAYOUT_VERIFIED_EXTERNAL_EVIDENCE_REQUIRED" => "A payment reference alone is not confirmation. Trusted bank or provider reconciliation evidence is required before marking this withdrawal paid.",
                _ => null
            };
            if (guidance is not null)
                return new(domainCode, guidance, StatusCodes.Status409Conflict);

            if (domainCode.Contains("NOT_FOUND", StringComparison.Ordinal))
            {
                return new(domainCode, "The requested affiliate financial record was not found.", StatusCodes.Status404NotFound);
            }

            if (domainCode.Contains("ALREADY", StringComparison.Ordinal) ||
                domainCode.Contains("DUPLICATE", StringComparison.Ordinal) ||
                domainCode.Contains("IDEMPOT", StringComparison.Ordinal))
            {
                return new(domainCode, "This affiliate financial operation was already applied or conflicts with an earlier request.", StatusCodes.Status409Conflict);
            }

            if (domainCode.Contains("REFUND", StringComparison.Ordinal) ||
                domainCode.Contains("REVERS", StringComparison.Ordinal))
            {
                return new(domainCode, "The commission operation conflicts with authoritative refund or reversal state.", StatusCodes.Status409Conflict);
            }

            if (domainCode.Contains("PAYOUT", StringComparison.Ordinal) ||
                domainCode.Contains("PROVIDER", StringComparison.Ordinal) ||
                domainCode.Contains("EXTERNAL_EVIDENCE", StringComparison.Ordinal) ||
                domainCode.Contains("WALLET", StringComparison.Ordinal))
            {
                return new(domainCode, "The payout operation could not be completed from the current authoritative evidence.", StatusCodes.Status409Conflict);
            }

            return new(domainCode, "The commission operation conflicts with the current authoritative financial state.", StatusCodes.Status409Conflict);
        }

        if (exception is ArgumentException)
        {
            return new(
                "AFFILIATE_VALIDATION_FAILED",
                "The affiliate financial request is invalid.",
                StatusCodes.Status400BadRequest);
        }

        if (exception is AffiliatePayoutAuthorityException)
        {
            return new(
                "AFFILIATE_PAYOUT_AUTHORITY_CONFLICT",
                "The payout is blocked because its store or currency authority cannot be proven.",
                StatusCodes.Status409Conflict);
        }

        if (exception is DbUpdateException)
        {
            return new(
                "AFFILIATE_FINANCIAL_CONFLICT",
                "The affiliate financial operation conflicts with persisted ledger state.",
                StatusCodes.Status409Conflict);
        }

        return new(
            "AFFILIATE_OPERATION_FAILED",
            "The affiliate financial operation could not be completed.",
            StatusCodes.Status500InternalServerError);
    }

    private static string? ExtractDomainCode(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var separator = message.IndexOf(':');
        var candidate = (separator >= 0 ? message[..separator] : message).Trim();
        if (!candidate.StartsWith("AFFILIATE_", StringComparison.Ordinal) || candidate.Length > 96)
        {
            return null;
        }

        return candidate.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? candidate
            : null;
    }
}
