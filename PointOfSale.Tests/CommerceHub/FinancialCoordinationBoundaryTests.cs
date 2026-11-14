using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Services.CommerceHub.Finance;
using PointOfSale.Core.Financial;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.CommerceHub;

public sealed class FinancialCoordinationBoundaryTests
{
    [Fact]
    public async Task RelationalCaptureRequiresCallerTransactionBeforeOpeningConnection()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CommerceHubFinancialSafetyGuardService(db).CaptureStateForUpdateAsync(Guid.NewGuid(), "EGP"));
        Assert.Contains("active transaction", error.Message, StringComparison.Ordinal);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task CurrencyAdmissionStillPrecedesTransactionAndSql()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        await Assert.ThrowsAsync<FinancialCurrencyMissingException>(() =>
            new CommerceHubFinancialSafetyGuardService(db).CaptureStateForUpdateAsync(Guid.NewGuid(), " "));
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }
}
