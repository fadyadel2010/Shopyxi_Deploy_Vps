using System.Data.Common;
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

// Soft-delete predicates dropped by a global scan must be restored at each
// SQL ownership boundary. The sender is strictly local; no remote effects occur.
public sealed partial class ProviderDeliveryDeletionAuthorityProviderTests
{
    [Theory]
    [InlineData("before-scan")]
    [InlineData("before-claim")]
    [InlineData("after-claim")]
    [InlineData("before-send")]
    [InlineData("during-send")]
    public async Task DeletedDeliveryCannotBeClaimedSentOrAcknowledgedPastItsCurrentSqlBoundary(string boundary)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var message = ProviderDeliveryOutboxMessage.Create(f.VendorId, nameof(AnalyticsProvider.GoogleAnalytics),
            "fixture", "APP2-delete-authority", "fictional-hash", "{}", "fixture", endpointUrl: "https://provider.invalid/never-sent");
        message.IsDeleted = boundary == "before-scan";
        f.Db.Add(message); await f.Db.SaveChangesAsync();
        var deleted = boundary == "before-scan";
        async Task DeleteAsync(CancellationToken ct)
        {
            await using var other = new NpgsqlConnection(f.ConnectionString); await other.OpenAsync(ct);
            await using var command = new NpgsqlCommand("UPDATE \"ProviderDeliveryOutbox\" SET \"IsDeleted\"=TRUE WHERE \"Id\"=@id", other);
            command.Parameters.AddWithValue("id", message.Id); Assert.Equal(1, await command.ExecuteNonQueryAsync(ct)); deleted = true;
        }
        var sender = new Mock<IProviderDeliveryHttpSender>(MockBehavior.Strict); var sendCount = 0;
        sender.Setup(s => s.SendAsync(It.IsAny<ProviderDeliveryOutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProviderDeliveryOutboxMessage _, CancellationToken ct) =>
            {
                sendCount++;
                if (boundary == "during-send") await DeleteAsync(ct);
                return ProviderDeliverySendResult.Ok(204, "", TimeSpan.Zero);
            });
        var breaker = new Mock<IProviderCircuitBreaker>(MockBehavior.Strict);
        breaker.Setup(b => b.CanAttemptAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        breaker.Setup(b => b.GetStateAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyticsProvider _, Guid? _, CancellationToken ct) =>
            {
                if (boundary == "before-send") await DeleteAsync(ct);
                return new ProviderCircuitState(false, 0, null);
            });
        // Permit recording so a regression is exposed by durable-state/count
        // assertions, not merely a mock exception before the receipt is written.
        breaker.Setup(b => b.RecordSuccessAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var interpose = new DeleteAroundClaim(boundary, DeleteAsync);
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(f.ConnectionString).AddInterceptors(interpose));
        services.AddSingleton(sender.Object); services.AddSingleton(breaker.Object);
        services.AddSingleton<IProviderDeliveryRetryEngine, ProviderDeliveryRetryEngine>();
        await using var provider = services.BuildServiceProvider(); using var metrics = new ProviderDeliveryMetrics();
        var processed = await new ProviderDeliveryProcessor(provider, Options.Create(new ProviderDeliveryOptions()),
            NullLogger<ProviderDeliveryProcessor>.Instance, metrics).ProcessPendingMessagesAsync(1);
        var saved = await f.Db.Set<ProviderDeliveryOutboxMessage>().IgnoreQueryFilters().AsNoTracking().SingleAsync();
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
        await using var receipt = new FileStream(Path.Combine(output, "provider-deletion-authority-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(receipt, new { boundary, deleted, sendCount, processed, saved.IsDeleted,
            saved.DeliveredAtUtc, saved.ClaimedUntilUtc, saved.AttemptCount, Scope = "SQL soft-delete fencing only; during-send is a local recording adapter, not remote cancellation proof" });
        Assert.True(deleted); Assert.True(saved.IsDeleted); Assert.Equal(0, processed); Assert.Null(saved.DeliveredAtUtc);
        Assert.Equal(boundary == "during-send" ? 1 : 0, sendCount); Assert.Equal(0, saved.AttemptCount);
        if (boundary is "before-scan" or "before-claim") Assert.Null(saved.ClaimedUntilUtc);
        else Assert.NotNull(saved.ClaimedUntilUtc);
        breaker.Verify(b => b.RecordSuccessAsync(It.IsAny<AnalyticsProvider>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private sealed class DeleteAroundClaim(string boundary, Func<CancellationToken, Task> delete) : DbCommandInterceptor
    {
        private bool _executed;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_executed && boundary == "before-claim" &&
                (command.CommandText.TrimStart().StartsWith("SELECT 1 FROM \"ProviderDeliveryOutbox\"", StringComparison.Ordinal) ||
                 command.CommandText.StartsWith("UPDATE \"ProviderDeliveryOutbox\"", StringComparison.Ordinal)))
            {
                _executed = true; await delete(cancellationToken);
            }
            return result;
        }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result,
            CancellationToken cancellationToken = default)
        {
            if (!_executed && boundary == "after-claim" && command.Transaction is null && result == 1 && command.CommandText.StartsWith("UPDATE \"ProviderDeliveryOutbox\"", StringComparison.Ordinal))
            {
                _executed = true; await delete(cancellationToken);
            }
            return result;
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // Current claim owns a transaction. Interpose after its COMMIT and
            // before the canonical entity read, never block that commit by
            // waiting for a competing writer from its own UPDATE interceptor.
            if (!_executed && boundary == "after-claim" && command.CommandText.Contains("\"PayloadJson\"", StringComparison.Ordinal))
            {
                _executed = true; await delete(cancellationToken);
            }
            return result;
        }
    }
}
