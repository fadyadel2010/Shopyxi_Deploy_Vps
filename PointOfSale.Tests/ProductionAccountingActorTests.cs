using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Core.Models.Refunds;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PaymentMethod = PointOfSale.Core.Enums.PaymentMethod;

namespace PointOfSale.Tests;

public sealed class ProductionAccountingActorTests
{
    [Fact]
    public void Context_IsTenantBoundAndRestoresOnDispose()
    {
        var context = new AccountingActorContext();
        var tenant = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        Assert.Null(context.ResolveUserId(tenant));
        using (context.UseActor(tenant, first))
        {
            Assert.Equal(first, context.ResolveUserId(tenant, second));
            Assert.Throws<InvalidOperationException>(() => context.ResolveUserId(Guid.NewGuid()));
            Assert.Throws<InvalidOperationException>(() => context.UseActor(Guid.NewGuid(), first));
            using (context.UseActor(tenant, second)) Assert.Equal(second, context.ResolveUserId(tenant));
            Assert.Equal(first, context.ResolveUserId(tenant));
        }
        Assert.Null(context.ResolveUserId(tenant));
        Assert.Throws<ArgumentException>(() => context.UseActor(tenant, Guid.Empty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurchaseSaleAndReturns_AttributeEveryRevenueCostAndTaxJournal(bool trustedScope)
    {
        var tenant = Guid.NewGuid(); var original = Guid.NewGuid(); var executor = Guid.NewGuid();
        var context = new AccountingActorContext();
        using var scope = trustedScope ? context.UseActor(tenant, executor) : null;
        var captured = new List<PostingRequest>();
        var service = CreateService(context, captured);
        Assert.True((await service.PostPosSaleAsync(new SalesInvoice
        {
            Id = Guid.NewGuid(), TenantId = tenant, CreatedBy = original,
            InvoiceNumber = "ACTOR-SALE", InvoiceDate = DateTime.UtcNow,
            PaymentMethod = PaymentMethod.Credit, TotalAmount = 11m, TaxAmount = 1m,
            Items = [new SalesInvoiceItem { CostAmountSnapshot = 5m }]
        })).Success);
        Assert.True((await service.PostPosPurchaseAsync(new PurchaseInvoice
        {
            Id = Guid.NewGuid(), TenantId = tenant, CreatedBy = original,
            InvoiceNumber = "ACTOR-PURCHASE", InvoiceDate = DateTime.UtcNow,
            PaymentMethod = PaymentMethod.Credit, TotalAmount = 16.5m, TaxAmount = 1.5m
        })).Success);
        Assert.True((await service.PostPosRefundAsync(new Return
        {
            Id = Guid.NewGuid(), TenantId = tenant, CreatedBy = original,
            ReturnType = ReturnType.SalesReturn, ReturnNumber = "ACTOR-REFUND", ReturnDate = DateTime.UtcNow,
            TotalAmount = 11m, TaxAmount = 1m, Items = [new ReturnItem { CostAmountSnapshot = 5m }]
        }, new RefundAllocation(RefundMethod.AccountCredit, 0m, 0m))).Success);
        Assert.True((await service.PostPurchaseReturnAsync(new Return
        {
            Id = Guid.NewGuid(), TenantId = tenant, CreatedBy = original,
            ReturnType = ReturnType.PurchaseReturn, ReturnNumber = "ACTOR-PURCHASE-RETURN",
            ReturnDate = DateTime.UtcNow, TotalAmount = 16.5m, TaxAmount = 1.5m
        })).Success);
        Assert.Equal(6, captured.Count);
        Assert.All(captured, request =>
        {
            Assert.Equal(trustedScope ? executor : original, request.UserId);
            Assert.Equal(request.Lines.Sum(l => l.DebitAmount), request.Lines.Sum(l => l.CreditAmount));
        });
    }

    [Fact]
    public async Task Mutation_UsesUpdaterWhileInitialPostingUsesCreator()
    {
        var captured = new List<PostingRequest>();
        var service = CreateService(new AccountingActorContext(), captured);
        var invoice = new SalesInvoice
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), CreatedBy = Guid.NewGuid(), UpdatedBy = Guid.NewGuid(),
            InvoiceNumber = "ACTOR-MUTATION", InvoiceDate = DateTime.UtcNow,
            PaymentMethod = PaymentMethod.Credit, TotalAmount = 10m
        };
        Assert.True((await service.PostPosSaleAsync(invoice)).Success);
        Assert.True((await service.PostPosSaleAsync(invoice, operationKey: "edit-1")).Success);
        Assert.Equal(invoice.CreatedBy, captured[0].UserId);
        Assert.Equal(invoice.UpdatedBy, captured[1].UserId);
        Assert.NotEqual(captured[0].IdempotencyKey, captured[1].IdempotencyKey);
    }

    [Fact]
    public async Task Enqueue_CapturesTrustedActorInSourceAndEnvelope_WithoutRewritingHistoryOnReplay()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid(); var owner = Guid.NewGuid(); var laterUser = Guid.NewGuid();
        var actor = new AccountingActorContext();
        var correlation = new Mock<ICorrelationIdAccessor>();
        correlation.Setup(x => x.GetCorrelationId()).Returns("actor-regression");
        var service = new AccountingOutboxService(db, correlation.Object,
            NullLogger<AccountingOutboxService>.Instance, actor);
        var supplier = new Supplier { Id = Guid.NewGuid(), TenantId = tenant, Name = "Actor regression supplier" };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = tenant, Name = "Actor regression warehouse", IsActive = true };
        db.Suppliers.Add(supplier); db.Warehouses.Add(warehouse);
        var invoice = new PurchaseInvoice
        {
            Id = Guid.NewGuid(), TenantId = tenant, InvoiceNumber = "ACTOR-ENQUEUE",
            SupplierId = supplier.Id, WarehouseId = warehouse.Id
        };
        db.PurchaseInvoices.Add(invoice);
        using (actor.UseActor(tenant, owner)) await service.EnqueuePosPurchaseAsync(tenant, invoice.Id, 0);
        var message = await db.AccountingOutboxMessages.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(owner, invoice.CreatedBy);
        Assert.Equal(owner, message.CreatedBy);
        using (actor.UseActor(tenant, laterUser)) await service.EnqueuePosPurchaseAsync(tenant, invoice.Id, 0);
        Assert.Equal(owner, invoice.CreatedBy);
        Assert.Equal(owner, message.CreatedBy);
        Assert.Single(await db.AccountingOutboxMessages.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task BackgroundRetry_UsesFrozenEnvelopeActor_NotLaterSourceEditor()
    {
        var tenant = Guid.NewGuid(); var originalActor = Guid.NewGuid(); var laterEditor = Guid.NewGuid();
        Guid? observedActor = null;
        var services = new ServiceCollection();
        var database = Guid.NewGuid().ToString();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(database)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<AccountingActorContext>();
        services.AddScoped(_ => Mock.Of<IFinancialPostingService>());
        services.AddScoped<ICommerceAccountingService>(sp =>
        {
            var mock = new Mock<ICommerceAccountingService>();
            mock.Setup(x => x.PostPosSaleAsync(It.IsAny<SalesInvoice>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .Callback<SalesInvoice, CancellationToken, string?>((source, _, _) =>
                    observedActor = sp.GetRequiredService<AccountingActorContext>().ResolveUserId(source.TenantId, source.UpdatedBy))
                .ReturnsAsync(PostingResult.Succeeded(Guid.NewGuid(), "ACTOR-RETRY"));
            return mock.Object;
        });
        await using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<ApplicationDbContext>();
        var warehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = tenant, Name = "Actor retry warehouse", IsActive = true };
        db.Warehouses.Add(warehouse);
        var invoice = new SalesInvoice
        {
            Id = Guid.NewGuid(), TenantId = tenant, WarehouseId = warehouse.Id, CreatedBy = laterEditor, UpdatedBy = laterEditor,
            InvoiceNumber = "ACTOR-RETRY", AccountingMutationVersion = 0
        };
        db.SalesInvoices.Add(invoice);
        var message = AccountingOutboxMessage.Create(tenant, "PosSale", invoice.Id, "PosSale",
            System.Text.Json.JsonSerializer.Serialize(new { InvoiceId = invoice.Id, MutationVersion = 0 }),
            $"SALE:{tenant}:{invoice.Id}", aggregateVersion: 0);
        message.CreatedBy = originalActor;
        db.AccountingOutboxMessages.Add(message); await db.SaveChangesAsync();
        var processor = new AccountingOutboxProcessor(provider, NullLogger<AccountingOutboxProcessor>.Instance);
        Assert.Equal(1, await processor.ProcessPendingMessagesAsync());
        Assert.Equal(originalActor, observedActor);
        await db.Entry(message).ReloadAsync();
        Assert.Equal(originalActor, message.CreatedBy);
        Assert.NotNull(message.ProcessedAtUtc);
    }

    private static CommerceAccountingService CreateService(AccountingActorContext actor, List<PostingRequest> captured)
    {
        var accounts = new Dictionary<string, Account>();
        Account Resolve(Guid tenant, string code)
        {
            if (!accounts.TryGetValue(code, out var account))
                accounts[code] = account = Account.Create(tenant, code, code, AccountType.Asset);
            return account;
        }
        var resolver = new Mock<IAccountResolver>();
        resolver.Setup(x => x.GetByCodesAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid tenant, IEnumerable<string> codes, CancellationToken _) => codes.ToDictionary(c => c, c => Resolve(tenant, c)));
        resolver.Setup(x => x.GetByCodeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid tenant, string code, CancellationToken _) => Resolve(tenant, code));
        var posting = new Mock<IFinancialPostingService>();
        posting.Setup(x => x.PostJournalAsync(It.IsAny<PostingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PostingRequest request, CancellationToken _) =>
            {
                captured.Add(request);
                return JournalEntry.Create(request.TenantId, "ACTOR-JOURNAL", request.JournalDate, request.SourceType,
                    request.SourceId, request.SourceReference, request.Description, request.IdempotencyKey, request.UserId);
            });
        return new CommerceAccountingService(posting.Object, resolver.Object, Mock.Of<ICashboxLedgerAccountService>(),
            NullLogger<CommerceAccountingService>.Instance, actor: actor);
    }
}
