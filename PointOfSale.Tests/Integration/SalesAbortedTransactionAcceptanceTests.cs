#if SHOPIXY_QA_SALE_ABORT
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;
using Tender = PointOfSale.Core.Enums.PaymentMethod;

namespace PointOfSale.Tests.Integration;

public sealed partial class PosLoopbackHttpAcceptanceTests
{
    [Theory]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    public async Task PosKnownAbort_RealPostgresFailure_RollsBackCartSaleMoneyAndIdempotency_ExplicitExactReplayConverges(string state)
    {
        using var database = TestDatabaseScope.Open(); var f = await Seed(database);
        var abort = new AbortFirstSaleSave(state);
        await using var host = await Host.Start(database, f, builder => builder.Services.AddScoped(_ =>
        {
            using var original = database.CreateDbContext(f.Tenant);
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(original.Database.GetConnectionString()).AddInterceptors(abort).Options;
            return new ApplicationDbContext(options, new TestTenantContext(f.Tenant));
        }));
        try
        {
            var cart = await host.Cart(f.Cashier);
            var payload = Sale(f, Tender.Cash);
            var paymentAttempt = Prefix + cart.CartId.ToString("N");
            using var denied = await host.Send(HttpMethod.Post, "/api/sales", f.Cashier, payload, cart.CartId);
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            var denial = await denied.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("SALE_CONCURRENCY_CONFLICT", denial.GetProperty("errorCode").GetString());
            Assert.Equal(paymentAttempt, Assert.Single(denied.Headers.GetValues("X-Payment-Attempt-Id")));
            Assert.Equal(1, abort.Fired); Assert.Equal(state, abort.ObservedState);
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                Assert.Empty(await db.SalesInvoices.ToListAsync()); Assert.Empty(await db.Returns.ToListAsync());
                Assert.Empty(await db.SalesRequestIdempotencyRecords.ToListAsync());
                Assert.Empty(await db.JournalEntries.ToListAsync()); Assert.Empty(await db.AccountingOutboxMessages.ToListAsync());
                Assert.Empty(await db.CustomerTransactions.ToListAsync()); Assert.Empty(await db.AccountLedgers.ToListAsync());
                Assert.All(await db.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
                Assert.Equal(0, (await db.Customers.SingleAsync()).Balance);
                var stock = await db.ProductVariantStocks.SingleAsync(); Assert.Equal(1, stock.Quantity); Assert.Equal(0, stock.ReservedQuantity);
                var retained = await db.PosCarts.SingleAsync(); Assert.Equal(PosCartStatus.Active, retained.Status); Assert.Null(retained.CompletedSaleId);
            }
            // Explicit caller action only: same cart/payment-attempt/body, never a
            // new payment or automatic retry after the rollback response.
            using var retryResponse = await host.Send(HttpMethod.Post, "/api/sales", f.Cashier, payload, cart.CartId);
            var sale = await Success<SaleResponse>(retryResponse);
            Assert.Equal(paymentAttempt, Assert.Single(retryResponse.Headers.GetValues("X-Payment-Attempt-Id")));
            using var replayResponse = await host.Send(HttpMethod.Post, "/api/sales", f.Cashier, payload, cart.CartId);
            var replay = await Success<SaleResponse>(replayResponse);
            Assert.Equal(sale.InvoiceId, replay.InvoiceId); Assert.Equal(sale.InvoiceNumber, replay.InvoiceNumber);
            Assert.Equal(1, abort.Fired);
            await AssertSingleSale(database, f, sale.InvoiceId);
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                var journals = await db.JournalEntries.Include(x => x.LedgerEntries).ToListAsync();
                Assert.Equal(2, journals.Count);
                Assert.All(journals, x => { Assert.True(x.TotalDebits > 0); Assert.True(x.LedgerEntries.Count >= 2); Assert.Equal(x.TotalDebits, x.TotalCredits); });
            }
            var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR")!;
            await using var file = new FileStream(Path.Combine(directory, $"pos-known-abort-{state}-{f.Tenant:N}.json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(file, new
            {
                localOnly = true, production = false, browser = false, fixture = f, injectedState = state, observedState = abort.ObservedState,
                actualPostgresAbort = true, failureAt = "first sale SaveChanges inside accounting enqueue and explicit transaction",
                attemptsBeforeExplicitRetry = 1, rollbackVerifiedBeforeRetry = true, cartId = cart.CartId, paymentAttempt,
                explicitRetry = true, exactBodyCartAndPaymentAttempt = true, canonicalCartIdempotency = true,
                compatibilityHeader = "Idempotency-Key is not canonical POS create authority; no claim it was preserved by Host.Send",
                sale.InvoiceId, replayInvoiceId = replay.InvoiceId, sale.InvoiceNumber,
                scope = "Actual PostgreSQL controlled transaction error, not a naturally scheduled deadlock; no direct SQL business-data repair"
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        finally { await host.CompensateAll(); await host.AssertCompensatedAndWriteReceipt("known-abort-" + state); }
    }

    private sealed class AbortFirstSaleSave(string state) : SaveChangesInterceptor
    {
        public int Fired;
        public string? ObservedState;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            if (db.ChangeTracker.Entries<SalesInvoice>().Any(x => x.State == EntityState.Added) && Interlocked.CompareExchange(ref Fired, 1, 0) == 0)
            {
                Assert.NotNull(db.Database.CurrentTransaction);
                var command = state switch
                {
                    PostgresErrorCodes.SerializationFailure => "DO $$ BEGIN RAISE EXCEPTION USING ERRCODE='40001', MESSAGE='QA controlled sale transaction abort'; END $$;",
                    PostgresErrorCodes.DeadlockDetected => "DO $$ BEGIN RAISE EXCEPTION USING ERRCODE='40P01', MESSAGE='QA controlled sale transaction abort'; END $$;",
                    _ => throw new InvalidOperationException("Only known local QA abort codes are permitted.")
                };
                try { await db.Database.ExecuteSqlRawAsync(command, cancellationToken); }
                catch (PostgresException error) { ObservedState = error.SqlState; throw; }
                throw new InvalidOperationException("PostgreSQL fault injection unexpectedly succeeded.");
            }
            return result;
        }
    }
}
#endif
