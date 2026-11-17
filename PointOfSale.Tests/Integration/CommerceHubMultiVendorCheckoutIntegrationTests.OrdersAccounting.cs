using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.API.Configuration;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.Services.CommerceHub.Finance;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Treasury;

namespace PointOfSale.Tests.Integration;

public sealed partial class CommerceHubMultiVendorCheckoutIntegrationTests
{
    // Exercise actual GL posting and reconciliation before outward money movement.
    // Do not mock readiness or fabricate a "healthy" reconciliation report.
    private static async Task PostAndReconcileOrdersFixtureAsync(ApplicationDbContext db)
    {
        var seeder = new TenantAccountSeeder(db, NullLogger<TenantAccountSeeder>.Instance);
        foreach (var tenantId in await db.Tenants.Select(t => t.Id).ToListAsync())
            await seeder.SeedAccountsAsync(tenantId);

        var messages = await db.AccountingOutboxMessages.AsNoTracking()
            .Where(m => m.EventType == "CommerceHubLedgerPosting" && m.ProcessedAtUtc == null)
            .Select(m => m.Id).ToListAsync();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(db.Database.GetConnectionString()));
        services.AddScoped<IAccountResolver>(p => new AccountResolver(
            p.GetRequiredService<ApplicationDbContext>(), NullLogger<AccountResolver>.Instance));
        services.AddScoped<IFinancialPostingService>(p => new FinancialPostingService(
            p.GetRequiredService<ApplicationDbContext>(), NullLogger<FinancialPostingService>.Instance));
        services.AddScoped<ICashboxLedgerAccountService>(p => new CashboxLedgerAccountService(
            p.GetRequiredService<ApplicationDbContext>(), NullLogger<CashboxLedgerAccountService>.Instance));
        services.AddScoped<ICommerceAccountingService>(p => new CommerceAccountingService(
            p.GetRequiredService<IFinancialPostingService>(), p.GetRequiredService<IAccountResolver>(),
            p.GetRequiredService<ICashboxLedgerAccountService>(), NullLogger<CommerceAccountingService>.Instance,
            p.GetRequiredService<ApplicationDbContext>()));
        using (var provider = services.BuildServiceProvider())
        {
            var processor = new AccountingOutboxProcessor(provider, NullLogger<AccountingOutboxProcessor>.Instance);
            var posted = await processor.ProcessExactMessagesAsync(messages);
            var errors = await db.AccountingOutboxMessages.AsNoTracking()
                .Where(m => messages.Contains(m.Id) && m.ProcessedAtUtc == null)
                .Select(m => m.LastError).ToListAsync();
            Assert.True(posted == messages.Count, "Isolated accounting pipeline: " + string.Join(" | ", errors));
        }
        db.ChangeTracker.Clear();
        Assert.False(await db.AccountingOutboxMessages.AnyAsync(m =>
            m.EventType == "CommerceHubLedgerPosting" && (m.ProcessedAtUtc == null || m.IsFailed)));
        var aggregate = new CommerceHubDailyFinancialAggregateService(
            db, NullLogger<CommerceHubDailyFinancialAggregateService>.Instance);
        await aggregate.RefreshWindowAsync(DateTime.UtcNow.Date.AddDays(-29), DateTime.UtcNow.Date);
        var read = new CommerceHubFinanceReconciliationReadService(db,
            new CommerceHubVendorDebtService(db, Options.Create(new CommerceHubVendorDebtOptions())));
        var reconciliation = new FinancialReconciliationService(db, read, aggregate,
            new CommerceHubFinancialAuditService(db), new NoOpFinancialReconciliationAlertHook(),
            NullLogger<FinancialReconciliationService>.Instance);
        var result = await reconciliation.RunAsync();
        Assert.True(result.Report.Status != "Critical", "Isolated reconciliation: " + result.Report.DetailsJson);
    }
}
