#if SHOPIXY_QA_STOREFRONT_CONTINUATION
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Checkout;
using PointOfSale.API.Services.Storefront;
using PointOfSale.API.Services.Checkout;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Consumers;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Tests.Infrastructure;
using Tender = PointOfSale.Core.Enums.PaymentMethod;

namespace PointOfSale.Tests.Integration;

public sealed partial class PosLoopbackHttpAcceptanceTests
{
    [Fact]
    public void StorefrontContinuation_SaleConflictPolicy_OnlyKnownAbortsAndNoUncertainOutcomeOrFailedRollback()
    {
        var policy = typeof(SalesController).GetMethod("IsKnownAbortedSaleTransaction", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Known(Exception exception) => (bool)policy.Invoke(null, [exception])!;
        Exception Sql(string state) => new InvalidOperationException("provider wrapper",
            new DbUpdateException("save wrapper", new PostgresException("synthetic SQL abort", "ERROR", "ERROR", state)));
        Assert.True(Known(Sql(PostgresErrorCodes.DeadlockDetected)));
        Assert.True(Known(Sql(PostgresErrorCodes.SerializationFailure)));
        Assert.True(Known(new DbUpdateConcurrencyException("optimistic conflict")));
        foreach (var state in new[] { PostgresErrorCodes.UniqueViolation, PostgresErrorCodes.ForeignKeyViolation, "08006", "23514" })
            Assert.False(Known(Sql(state)));
        Assert.False(Known(new NpgsqlException("unknown connection/commit outcome")));
        Assert.False(Known(new InvalidOperationException("business failure")));
        Assert.False(Known(new AggregateException(Sql(PostgresErrorCodes.DeadlockDetected), new Exception("rollback failed"))));
        Assert.False(Known(new OperationCanceledException("cancelled", Sql(PostgresErrorCodes.SerializationFailure))));
    }

    [Fact]
    public async Task StorefrontContinuation_ActualPublishedOrderConsumer_DuplicateDeliveryAndReplayRetainCanonicalSession()
    {
        using var database = TestDatabaseScope.Open(); var f = await Seed(database);
        await using var runtime = await StorefrontRuntime.Create(database, f, true, false, 1);
        try
        {
            var request = await runtime.Prepare();
            var order = await runtime.Checkout(request);
            // Use the exact event emitted by public checkout, not a hand-built
            // event that might omit a production cart/source/tenant linkage.
            var publisher = Mock.Get(runtime.Services.GetRequiredService<IEventPublisher>());
            var emitted = Assert.Single(publisher.Invocations.SelectMany(x => x.Arguments).OfType<OrderCreatedEvent>());
            Assert.Equal(order.OrderId, emitted.OrderId); Assert.Equal(f.Tenant, emitted.TenantId);
            Assert.Equal(runtime.Cart.CartId, emitted.CartId); Assert.Equal(OrderSource.Ecommerce, emitted.Source);
            var delivery = new Mock<ConsumeContext<OrderCreatedEvent>>(MockBehavior.Strict);
            delivery.SetupGet(x => x.Message).Returns(emitted);
            delivery.SetupGet(x => x.CancellationToken).Returns(CancellationToken.None);
            DateTime? convertedAt = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var scope = runtime.Services.CreateAsyncScope();
                var consumer = new CheckoutSessionOrderConversionConsumer(scope.ServiceProvider.GetRequiredService<CheckoutSessionService>(),
                    NullLogger<CheckoutSessionOrderConversionConsumer>.Instance);
                await consumer.Consume(delivery.Object);
                await using var db = database.CreateDbContext(f.Tenant);
                var session = Assert.Single(await db.CheckoutSessions.AsNoTracking().ToListAsync());
                Assert.Equal(request.CheckoutSessionId, session.Id); Assert.Equal(CheckoutSessionStatus.ConvertedToOrder, session.Status);
                Assert.Equal(order.OrderId, session.ConvertedOrderId); Assert.Equal(order.OrderNumber, session.ConvertedOrderNumber);
                Assert.Equal(runtime.Cart.CartId, session.CartId); Assert.NotNull(session.ConvertedAtUtc);
                if (attempt == 0) convertedAt = session.ConvertedAtUtc;
                else Assert.Equal(convertedAt, session.ConvertedAtUtc);
                Assert.Single(await db.Orders.ToListAsync()); Assert.Single(await db.InventoryReservations.ToListAsync());
                Assert.Empty(await db.JournalEntries.ToListAsync()); Assert.Empty(await db.PaymentIntents.ToListAsync());
                Assert.Equal(1, (await db.ProductVariantStocks.SingleAsync()).ReservedQuantity);
            }
            runtime.CartReadable = false;
            var replay = await runtime.Checkout(request);
            Assert.Equal(order.OrderId, replay.OrderId); Assert.Equal(order.OrderNumber, replay.OrderNumber);
            Assert.Single(publisher.Invocations.SelectMany(x => x.Arguments).OfType<CheckoutSessionConvertedEvent>());
            await WriteStorefrontContinuationReceipt("consumer-duplicate-delivery", new
            {
                fixture = f, actualCheckoutEvent = true, deliveries = 2, freshConsumerScopes = 2,
                emitted.OrderId, emitted.CartId, source = emitted.Source, request.CheckoutSessionId,
                convertedAt, exactReplayOrderId = replay.OrderId, conversionEvents = 1,
                boundary = "Actual emitted event + real consumer/session service; transport delivery and ConsumeContext substituted; not broker retry certification"
            });
        }
        finally { await runtime.CancelAndVerify("consumer-duplicate-delivery"); }
    }

    [Fact]
    public async Task StorefrontContinuation_ConcurrentPosAndPublicCheckout_LastUnitHasExactlyOneWinner()
    {
        using var database = TestDatabaseScope.Open(); var f = await Seed(database);
        await using var runtime = await StorefrontRuntime.Create(database, f, true, false, 1);
        var errors = new ConcurrentQueue<Exception>();
        await using var host = await Host.Start(database, f, builder => builder.Logging.AddProvider(new StorefrontFailureLoggerProvider(errors)));
        try
        {
            var request = await runtime.Prepare();
            var posCart = await host.Cart(f.Cashier);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var storefront = Task.Run(async () =>
            {
                await gate.Task;
                var start = Stopwatch.GetTimestamp();
                try
                {
                    var result = await runtime.Checkout(request);
                    return (Result: result, Error: (Exception?)null, Start: start, Finish: Stopwatch.GetTimestamp());
                }
                catch (Exception error)
                {
                    return (Result: (CheckoutResponseDto?)null, Error: error, Start: start, Finish: Stopwatch.GetTimestamp());
                }
            });
            var pos = Task.Run(async () =>
            {
                await gate.Task;
                var start = Stopwatch.GetTimestamp();
                var response = await host.Send(HttpMethod.Post, "/api/sales", f.Cashier, Sale(f, Tender.Cash), posCart.CartId);
                return (Response: response, Start: start, Finish: Stopwatch.GetTimestamp());
            });
            gate.SetResult();
            await Task.WhenAll(storefront, pos);
            var web = await storefront; var till = await pos;
            using var response = till.Response;
            var storefrontErrors = web.Error == null ? Enumerable.Empty<Exception>() : ExceptionChain(web.Error);
            var storefrontSqlState = storefrontErrors.OfType<PostgresException>().FirstOrDefault()?.SqlState;
            // Preserve failed attempts without exporting response/session tokens,
            // SQL parameters, connection strings or exception message payloads.
            await WriteStorefrontContinuationReceipt("concurrent-diagnostics", new
            {
                fixture = f, posHttpStatus = (int)response.StatusCode, storefrontCommitted = web.Result != null,
                storefrontErrorType = web.Error?.GetType().FullName, storefrontSqlState,
                exceptions = errors.SelectMany(ExceptionChain).Select(error => new
                {
                    type = error.GetType().FullName, sqlState = (error as PostgresException)?.SqlState,
                    stack = error.StackTrace
                }).ToArray()
            });
            Assert.True(web.Start < till.Finish && till.Start < web.Finish, "Both real request windows must overlap; serial execution is not concurrent acceptance.");
            var webWon = web.Result != null;
            Assert.NotEqual(webWon, response.IsSuccessStatusCode);
            if (webWon)
            {
                Assert.Null(web.Error);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }
            else
            {
                if (web.Error is WarehouseResolutionException warehouseConflict)
                {
                    Assert.Equal(WarehouseResolutionErrorCodes.InsufficientStockAtResolvedWarehouse, warehouseConflict.ErrorCode);
                    Assert.Equal(409, warehouseConflict.StatusCode);
                }
                else
                {
                    var conflict = Assert.IsType<CheckoutConcurrencyConflictException>(web.Error);
                    Assert.Equal("CHECKOUT_CONCURRENCY_CONFLICT", conflict.ErrorCode);
                    Assert.Equal(runtime.Cart.CartId, conflict.CartId);
                    Assert.Equal(1, conflict.AttemptCount); // Direct host never retries its publisher.
                    Assert.Contains(storefrontSqlState, new[] { PostgresErrorCodes.SerializationFailure, PostgresErrorCodes.DeadlockDetected });
                }
                await Success<SaleResponse>(response);
            }
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                Assert.Equal(webWon ? 1 : 0, await db.Orders.CountAsync());
                Assert.Equal(webWon ? 0 : 1, await db.SalesInvoices.CountAsync());
                Assert.Equal(webWon ? 1 : 0, await db.InventoryReservations.CountAsync());
                var stock = await db.ProductVariantStocks.SingleAsync();
                Assert.Equal(webWon ? 1 : 0, stock.Quantity); Assert.Equal(webWon ? 1 : 0, stock.ReservedQuantity);
                Assert.Equal(0, stock.AvailableQuantity);
            }
            await WriteStorefrontContinuationReceipt("concurrent-last-unit", new
            {
                fixture = f, winner = webWon ? "Storefront" : "POS", overlappingRequestWindows = true,
                storefrontStart = web.Start, storefrontFinish = web.Finish, posStart = till.Start, posFinish = till.Finish,
                stopwatchFrequency = Stopwatch.Frequency, storefrontOrderId = web.Result?.OrderId,
                storefrontDenial = web.Error is WarehouseResolutionException warehouse ? warehouse.ErrorCode : (web.Error as CheckoutConcurrencyConflictException)?.ErrorCode,
                storefrontSqlState,
                posHttpStatus = (int)response.StatusCode,
                boundary = "One concurrently released public-checkout-service/POS-HTTP attempt; not a deterministic SQL-lock barrier or stress/broker/Redis certification"
            });
        }
        finally
        {
            try { await host.CompensateAll(); }
            finally { await runtime.CancelAndVerify("concurrent-last-unit", allowCompensatedPos: true); }
        }
    }

    private static async Task WriteStorefrontContinuationReceipt(string scenario, object value)
    {
        var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR")!;
        await using var file = new FileStream(Path.Combine(directory, $"storefront-continuation-{scenario}.json"), FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(file, new { localOnly = true, production = false, browser = false, scenario, evidence = value },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static IEnumerable<Exception> ExceptionChain(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            yield return current;
    }

    private sealed class StorefrontFailureLoggerProvider(ConcurrentQueue<Exception> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FailureLogger(errors);
        public void Dispose() { }
        private sealed class FailureLogger(ConcurrentQueue<Exception> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (level >= LogLevel.Error && exception != null) errors.Enqueue(exception);
            }
        }
    }
}
#endif
