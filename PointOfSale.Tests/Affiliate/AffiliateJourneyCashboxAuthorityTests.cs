using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Treasury;
#if !AFFILIATE_FULL_FOCUSED
using PointOfSale.Tests.Infrastructure;
#endif

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateJourneyCashboxAuthorityTests : IDisposable
{
#if !AFFILIATE_FULL_FOCUSED
    private readonly TestDatabaseScope _scope = TestDatabaseScope.Open();
#endif

    private ApplicationDbContext Open(Guid tenant)
    {
#if AFFILIATE_FULL_FOCUSED
        Assert.Equal("network-none-local-socket", Environment.GetEnvironmentVariable("AFFILIATE_FULL_TEST_ISOLATION"));
        var connection = Environment.GetEnvironmentVariable("AFFILIATE_FULL_TEST_DB")!;
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        Assert.Equal("/tmp", parsed.Host);
        Assert.Equal("affiliate_journey_regression", parsed.Database);
        Assert.Equal("affiliate_fixture", parsed.Username);
        var context = new TenantContext();
        context.SetTenantId(tenant);
        return new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options, context);
#else
        return _scope.CreateDbContext(tenant);
#endif
    }

    public void Dispose()
    {
#if !AFFILIATE_FULL_FOCUSED
        _scope.Dispose();
#endif
    }

    [Fact]
    public async Task DedicatedAffiliatePayoutCashbox_DoesNotStrandOriginalCodCollectionDestination()
    {
        var tenant = Guid.NewGuid();
        await using var db = Open(tenant);
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "ISOLATED Affiliate journey regression", Slug = "qa-" + tenant.ToString("N"),
            Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        await db.SaveChangesAsync();
        await new TenantAccountSeeder(db, NullLogger<TenantAccountSeeder>.Instance).SeedAsync(tenant);
        var original = await ManualCodSettlementAuthority.ResolveMainCashboxAsync(db, tenant, default);
        var cashbox = new Cashbox { Id = Guid.NewGuid(), TenantId = tenant, Name = "ISOLATED Affiliate payout cashbox", IsActive = true };
        db.Cashboxes.Add(cashbox);
        await db.SaveChangesAsync();
        var dedicated = await new CashboxLedgerAccountService(db, NullLogger<CashboxLedgerAccountService>.Instance)
            .EnsureCashboxAccountAsync(cashbox);
        Assert.NotEqual(original.Cashbox.Id, dedicated.AccountId);
        Assert.False(await db.TreasuryAccounts.Where(t => t.AccountId == original.Cashbox.Id).Select(t => t.IsDefault).SingleAsync());
        var after = await ManualCodSettlementAuthority.ResolveAccountsAsync(db, tenant, default);
        Assert.Equal(original.Cashbox.Id, after.Cashbox.Id);
        Assert.Equal("1010", after.Cashbox.AccountCode);
        Assert.Equal("1130", after.Clearing.AccountCode);
        Assert.Equal("EGP", after.Currency);
        Assert.Empty(await db.JournalEntries.ToListAsync());
        Assert.Empty(await db.PaymentSettlementBatches.ToListAsync());
    }

    [Theory]
    [InlineData("mapping-inactive")]
    [InlineData("mapping-deleted")]
    [InlineData("account-inactive")]
    public async Task UnavailableCanonicalMainCashbox_NeverFallsBackToDedicatedPayoutCashbox(string fault)
    {
        var tenant = Guid.NewGuid();
        await using var db = Open(tenant);
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "ISOLATED fail-closed mapping regression", Slug = "qa-" + tenant.ToString("N"),
            Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        await db.SaveChangesAsync();
        await new TenantAccountSeeder(db, NullLogger<TenantAccountSeeder>.Instance).SeedAsync(tenant);
        var main = await db.TreasuryAccounts.Include(t => t.Account).SingleAsync(t => t.Type == TreasuryAccountType.Cashbox);
        var mainAccount = Assert.IsType<Account>(main.Account);
        var cashbox = new Cashbox { Id = Guid.NewGuid(), TenantId = tenant, Name = "ISOLATED dedicated payout", IsActive = true };
        db.Cashboxes.Add(cashbox);
        await db.SaveChangesAsync();
        await new CashboxLedgerAccountService(db, NullLogger<CashboxLedgerAccountService>.Instance).EnsureCashboxAccountAsync(cashbox);
        if (fault == "mapping-inactive") main.IsActive = false;
        if (fault == "mapping-deleted") main.IsDeleted = true;
        if (fault == "account-inactive") mainAccount.IsActive = false;
        await db.SaveChangesAsync();
        var failure = await Assert.ThrowsAsync<ManualCodSettlementException>(() =>
            ManualCodSettlementAuthority.ResolveAccountsAsync(db, tenant, default));
        Assert.Equal("COD_MANUAL_CASHBOX_REQUIRED", failure.Code);
        Assert.Empty(await db.JournalEntries.ToListAsync());
        Assert.Empty(await db.PaymentSettlementBatches.ToListAsync());
    }
}
