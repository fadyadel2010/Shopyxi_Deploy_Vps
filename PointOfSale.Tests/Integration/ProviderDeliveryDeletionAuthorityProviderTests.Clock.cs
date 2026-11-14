using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PointOfSale.Core.Analytics;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Analytics.Delivery;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

public sealed partial class ProviderDeliveryDeletionAuthorityProviderTests
{
    [Fact]
    public async Task OutcomeExpiryIsReevaluatedUsingTheDatabaseClockAfterAnActualRowLockWait()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        var message = ProviderDeliveryOutboxMessage.Create(f.VendorId, nameof(AnalyticsProvider.GoogleAnalytics),
            "fixture", "APP2-postwait-clock", "fictional-hash", "{}", "fixture", endpointUrl: "https://provider.invalid/never-sent");
        f.Db.Add(message); await f.Db.SaveChangesAsync(ct);
        await using var holder = new NpgsqlConnection(f.ConnectionString); await holder.OpenAsync(ct);
        await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync(ct);
        await using var held = await holder.BeginTransactionAsync(ct);
        var sender = new Mock<IProviderDeliveryHttpSender>(MockBehavior.Strict); var sendCount = 0;
        sender.Setup(s => s.SendAsync(It.IsAny<ProviderDeliveryOutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProviderDeliveryOutboxMessage _, CancellationToken token) =>
            {
                sendCount++;
                await using var block = new NpgsqlCommand("UPDATE \"ProviderDeliveryOutbox\" SET \"UpdatedAt\"=\"UpdatedAt\" WHERE \"Id\"=@id", holder, held);
                block.Parameters.AddWithValue("id", message.Id); Assert.Equal(1, await block.ExecuteNonQueryAsync(token));
                return ProviderDeliverySendResult.Ok(204, "", TimeSpan.Zero);
            });
        var breaker = new Mock<IProviderCircuitBreaker>(MockBehavior.Loose);
        breaker.Setup(b => b.CanAttemptAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        breaker.Setup(b => b.GetStateAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ProviderCircuitState(false, 0, null));
        var boundary = new OutcomeBoundary(); var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(f.ConnectionString).AddInterceptors(boundary));
        services.AddSingleton(sender.Object); services.AddSingleton(breaker.Object);
        services.AddSingleton<IProviderDeliveryRetryEngine, ProviderDeliveryRetryEngine>();
        await using var provider = services.BuildServiceProvider(); using var metrics = new ProviderDeliveryMetrics();
        var pending = new ProviderDeliveryProcessor(provider, Options.Create(new ProviderDeliveryOptions { ProcessingLeaseSeconds = 5 }),
            NullLogger<ProviderDeliveryProcessor>.Instance, metrics).ProcessPendingMessagesAsync(1, ct);
        var blocked = false; var expired = false; var released = false;
        try
        {
            var waiter = await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(12))
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter)),clock_timestamp()>=\"ClaimedUntilUtc\" FROM \"ProviderDeliveryOutbox\" WHERE \"Id\"=@id", observer);
                probe.Parameters.AddWithValue("holder", holder.ProcessID); probe.Parameters.AddWithValue("waiter", waiter); probe.Parameters.AddWithValue("id", message.Id);
                await using (var rows = await probe.ExecuteReaderAsync(ct))
                {
                    Assert.True(await rows.ReadAsync(ct)); blocked = rows.GetBoolean(0); expired = rows.GetBoolean(1);
                }
                if (blocked && expired) break;
                await Task.Delay(20, ct);
            }
            Assert.True(blocked && expired, "Observe the exact backend blocker and server-clock expiry before releasing ownership.");
            await held.RollbackAsync(ct); released = true;
            var processed = await pending.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var saved = await f.Db.Set<ProviderDeliveryOutboxMessage>().IgnoreQueryFilters().AsNoTracking().SingleAsync(ct);
            var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence required");
            await using var receipt = new FileStream(Path.Combine(output, "provider-postwait-clock-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(receipt, new { blocked, expired, holder = holder.ProcessID, waiter, sendCount, processed, saved.DeliveredAtUtc, saved.ClaimedUntilUtc,
                scope = "Actual PostgreSQL postwait local outcome expiry; recording sender only, not remote fencing/delivery certification" }, cancellationToken: ct);
            Assert.Equal(1, sendCount); Assert.Equal(0, processed); Assert.Null(saved.DeliveredAtUtc); Assert.NotNull(saved.ClaimedUntilUtc);
            Assert.Equal(0, saved.AttemptCount);
            breaker.Verify(b => b.RecordSuccessAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never());
        }
        finally
        {
            if (!released) await held.RollbackAsync(CancellationToken.None);
            try { await pending; } catch { /* Drain owned work without replacing the original failure. */ }
        }
    }

    private sealed class OutcomeBoundary : DbCommandInterceptor
    {
        private int locks;
        private int updates;
        internal TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if ((command.CommandText.TrimStart().StartsWith("SELECT 1 FROM \"ProviderDeliveryOutbox\"", StringComparison.Ordinal) && ++locks == 2) ||
                (command.CommandText.StartsWith("UPDATE \"ProviderDeliveryOutbox\"", StringComparison.Ordinal) && ++updates == 2))
                Entered.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }
    }
}
