using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Services.Affiliate;

/// <summary>Read-only resolution of an existing dedicated physical cashbox. Never provisions or falls back.</summary>
public static class AffiliateManualCashSource
{
    public static string Fingerprint(Guid cashboxId, TreasuryAccount treasury) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            CashboxId = cashboxId, treasury.TenantId, TreasuryAccountId = treasury.Id,
            treasury.AccountId, treasury.ReferenceCode, treasury.Type,
            treasury.Account!.AccountCode, treasury.Account.ParentAccountId,
            treasury.Account.AccountType, treasury.Account.NormalBalance, treasury.Account.PreventNegative
        }))));

    public static async Task<TreasuryAccount?> ResolveAsync(ApplicationDbContext db, Guid store, Guid cashboxId,
        CancellationToken ct = default)
    {
        if (!await db.Cashboxes.IgnoreQueryFilters().AsNoTracking().AnyAsync(c => c.Id == cashboxId &&
                c.TenantId == store && c.IsActive && !c.IsDeleted, ct)) return null;
        var reference = cashboxId.ToString("N");
        var mappings = await db.TreasuryAccounts.IgnoreQueryFilters().AsNoTracking().Include(t => t.Account)
            .Where(t => t.TenantId == store && t.Type == TreasuryAccountType.Cashbox &&
                t.ReferenceCode == reference && !t.IsDeleted).ToListAsync(ct);
        if (mappings.Count != 1) return null;
        var treasury = mappings[0];
        var account = treasury.Account;
        // Match the canonical provisioning contract AND exclusive persisted ownership, not a code prefix alone.
        if (!treasury.IsActive || account == null || account.TenantId != store || !account.IsActive || account.IsDeleted ||
            account.IsClearing || account.AccountType != AccountType.Asset || account.NormalBalance != NormalBalance.Debit ||
            !account.PreventNegative || account.AccountCode != $"{AccountCodes.CashboxMain}-{reference[24..].ToUpperInvariant()}" ||
            !await db.LedgerAccounts.IgnoreQueryFilters().AsNoTracking().AnyAsync(a => a.Id == account.ParentAccountId &&
                a.TenantId == store && a.AccountCode == AccountCodes.CashboxMain && !a.IsDeleted, ct) ||
            await db.TreasuryAccounts.IgnoreQueryFilters().AsNoTracking().AnyAsync(t => t.AccountId == account.Id &&
                t.Id != treasury.Id, ct)) return null;
        return treasury;
    }
}
