using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Admin;

// InMemory is ONLY a read-only DTO projection check, not PostgreSQL proof or a
// banking operation. Fictional rows are seeded directly; no ledger/provider runs.
public sealed class BankStatementPresentationContractTests
{
    [Theory]
    [InlineData(null, null, null, "", "")]
    [InlineData("ref", "description", "notes", "ref", "description")]
    [InlineData(null, null, "notes", "", "notes")]
    [InlineData("", "", "notes", "", "")]
    [InlineData("ref", null, null, "ref", "")]
    [InlineData("fictional-ref", null, "", "fictional-ref", "")]
    public async Task ReadOnlyStatementMaintainsNonNullDisplayContractWithoutInventingIdentifiers(
        string? reference, string? description, string? notes, string expectedReference, string expectedDescription)
    {
        var tenantId = Guid.NewGuid(); var bankId = Guid.NewGuid();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, new TestTenantContext(tenantId));
        db.Banks.Add(new Bank { Id = bankId, TenantId = tenantId, BankName = "Fictional read-only bank", IsActive = true });
        db.BankTransactions.Add(new BankTransaction
        {
            Id = Guid.NewGuid(), TenantId = tenantId, BankId = bankId, Amount = 7m, TransactionType = 1,
            TransactionDate = DateTime.UtcNow, ReferenceNumber = reference, Description = description, Notes = notes
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var controller = new BanksController(db, new TestTenantContext(tenantId), NullLogger<BanksController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var action = await controller.GetStatement(new BankStatementRequestDto { BankId = bankId });
        var response = Assert.IsType<BankStatementResponseDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        var row = Assert.Single(response.Items);
        Assert.Equal(expectedReference, row.ReferenceNumber); Assert.Equal(expectedDescription, row.Description);
        Assert.Equal(7m, row.DebitAmount); Assert.Equal(0m, row.CreditAmount); Assert.Equal(7m, row.RunningBalance);
        Assert.Equal(7m, response.Summary.EndingBalance);
        Assert.False(db.ChangeTracker.HasChanges());
        var persisted = await db.BankTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(reference, persisted.ReferenceNumber); Assert.Equal(description, persisted.Description); Assert.Equal(notes, persisted.Notes);
    }
}
