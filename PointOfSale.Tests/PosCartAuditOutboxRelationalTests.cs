using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Eventing;

namespace PointOfSale.Tests;

public sealed class PosCartAuditOutboxRelationalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditAndCompletion_CommitTogether_OrBothRollbackOnWriteFailure(bool failAudit)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new FailAuditInsert { Enabled = failAudit };
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection).AddInterceptors(interceptor));
        await using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<ApplicationDbContext>();
        // Only the two existing tables touched by this consumer are required.
        // This is an actual relational transaction, not InMemory's ignored one.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "PosCartOutboxMessages" (
              "Id" TEXT PRIMARY KEY, "TenantId" TEXT NOT NULL, "CartId" TEXT NOT NULL,
              "EventType" TEXT NOT NULL, "CartVersion" INTEGER NOT NULL, "PayloadJson" TEXT NOT NULL,
              "IdempotencyKey" TEXT NOT NULL, "OccurredAtUtc" TEXT NOT NULL, "ProcessedAtUtc" TEXT,
              "ProcessingError" TEXT, "RowVersion" INTEGER NOT NULL,
              "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT, "CreatedBy" TEXT, "UpdatedBy" TEXT, "IsDeleted" INTEGER NOT NULL);
            CREATE TABLE "AuditLogs" (
              "LogId" INTEGER PRIMARY KEY AUTOINCREMENT, "TenantId" TEXT, "TimestampUtc" TEXT NOT NULL,
              "UserId" TEXT, "UserNameSnapshot" TEXT, "ActionType" TEXT NOT NULL, "EntityType" TEXT NOT NULL,
              "EntityId" TEXT NOT NULL, "Description" TEXT NOT NULL, "BeforeDataJson" TEXT, "AfterDataJson" TEXT,
              "IpAddress" TEXT, "UserAgent" TEXT, "ReferenceNumber" TEXT, "Source" TEXT, "SuccessFlag" INTEGER NOT NULL);
            """);
        var tenant = Guid.NewGuid(); var cart = Guid.NewGuid();
        var clock = new ManualClock();
        var message = new PosCartOutboxMessage
        {
            Id = Guid.NewGuid(), TenantId = tenant, CartId = cart, CartVersion = 1,
            EventType = "PosCartCreated", IdempotencyKey = "relational-atomicity", CreatedAt = DateTime.UtcNow,
            OccurredAtUtc = DateTime.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new { tenantId = tenant, cartId = cart, version = 1, status = 0 })
        };
        db.PosCartOutboxMessages.Add(message); await db.SaveChangesAsync();
        var processor = new PosCartAuditOutboxProcessor(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PosCartAuditOutboxOptions { Enabled = true, TenantIds = [tenant] }),
            NullLogger<PosCartAuditOutboxProcessor>.Instance, clock);
        Assert.Equal(failAudit ? 0 : 1, await processor.ProcessTenantAsync(tenant));
        await db.Entry(message).ReloadAsync();
        Assert.Equal(!failAudit, message.ProcessedAtUtc.HasValue);
        Assert.Equal(failAudit ? 0 : 1, await db.AuditLogs.IgnoreQueryFilters().CountAsync());
        if (failAudit)
        {
            Assert.NotNull(message.ProcessingError);
            interceptor.Enabled = false;
            clock.UtcNow = clock.UtcNow.AddMinutes(2);
            Assert.Equal(1, await processor.ProcessTenantAsync(tenant));
            Assert.Single(await db.AuditLogs.IgnoreQueryFilters().ToListAsync());
        }
        Assert.Equal(0, await processor.ProcessTenantAsync(tenant));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FailAuditInsert : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO \"AuditLogs\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected local audit write failure.");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
