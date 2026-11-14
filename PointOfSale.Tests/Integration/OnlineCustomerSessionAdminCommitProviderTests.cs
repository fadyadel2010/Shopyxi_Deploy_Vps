using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Moq;
using PointOfSale.API.Models;
using PointOfSale.API.Services.Admin;
using PointOfSale.API.Services.Storefront;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents;

namespace PointOfSale.Tests.Integration;

// Real PostgreSQL persistence; Redis/bus/Mongo are strict no-network boundaries.
// No authentication issuance, external notification or financial operation runs.
public sealed class OnlineCustomerSessionAdminCommitProviderTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("save-failure")]
    [InlineData("blacklist-failure")]
    [InlineData("event-failure")]
    [InlineData("no-active")]
    [InlineData("foreign-tenant")]
    [InlineData("outer-transaction")]
    public async Task RevokeAllPersistsBeforeDownstreamEffectsAndReportsPostCommitFailures(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var customerId = Guid.NewGuid(); var otherId = Guid.NewGuid();
        f.Db.AddRange(new OnlineCustomer { Id = customerId, TenantId = f.VendorId, Email = "session@example.invalid", NormalizedEmail = "session@example.invalid" },
            new OnlineCustomer { Id = otherId, TenantId = f.VendorId, Email = "other@example.invalid", NormalizedEmail = "other@example.invalid" });
        OnlineCustomerRefreshToken Token(Guid customer, char hash) => new()
        {
            Id = Guid.NewGuid(), TenantId = f.VendorId, OnlineCustomerId = customer, TokenHash = new string(hash, 64),
            CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1), CreatedByIp = "fictional-not-an-ip",
            IsRevoked = state == "no-active" && customer == customerId
        };
        var target = Token(customerId, 'a'); var other = Token(otherId, 'b');
        f.Db.AddRange(target, other); await f.Db.SaveChangesAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString);
        if (state == "save-failure") options.AddInterceptors(new FailRevokeSave());
        await using var db = new ApplicationDbContext(options.Options,
            new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        await using var callerTransaction = state == "outer-transaction" ? await db.Database.BeginTransactionAsync() : null;
        var mongo = new Mock<IMongoDatabase>(MockBehavior.Strict);
        mongo.Setup(m => m.GetCollection<OnlineCustomerSessionDocument>("online_customer_sessions", It.IsAny<MongoCollectionSettings>()))
            .Returns(Mock.Of<IMongoCollection<OnlineCustomerSessionDocument>>(MockBehavior.Strict));
        var blacklist = new Mock<IStorefrontJwtBlacklist>(MockBehavior.Strict);
        var publisher = new Mock<IEventPublisher>(MockBehavior.Strict);
        var blacklistCalls = 0; var eventCalls = 0;
        var effectsExpected = state is "valid" or "blacklist-failure" or "event-failure";
        async Task VerifyCommitted()
        {
            Assert.Null(db.Database.CurrentTransaction);
            await using var observer = f.ScopedDb(f.VendorId);
            var persisted = await observer.OnlineCustomerRefreshTokens.AsNoTracking().SingleAsync(t => t.Id == target.Id);
            Assert.True(persisted.IsRevoked); Assert.NotNull(persisted.RevokedAtUtc);
            Assert.False((await observer.OnlineCustomerRefreshTokens.AsNoTracking().SingleAsync(t => t.Id == other.Id)).IsRevoked);
        }
        if (effectsExpected)
        {
            blacklist.Setup(b => b.BlacklistCustomerAsync(f.VendorId, customerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    blacklistCalls++; await VerifyCommitted();
                    if (state == "blacklist-failure") throw new InvalidOperationException("Controlled no-network blacklist failure");
                });
            publisher.Setup(p => p.PublishAsync(It.IsAny<OnlineCustomerAllTokensRevokedEvent>(), It.IsAny<CancellationToken>()))
                .Returns(async (OnlineCustomerAllTokensRevokedEvent message, CancellationToken _) =>
                {
                    eventCalls++; await VerifyCommitted();
                    Assert.Equal(f.VendorId, message.TenantId); Assert.Equal(customerId, message.CustomerId); Assert.Equal(1, message.TokensRevoked);
                    if (state == "event-failure") throw new InvalidOperationException("Controlled no-network event failure");
                });
        }
        var service = new OnlineCustomerSessionAdminService(db, mongo.Object, blacklist.Object, publisher.Object,
            Options.Create(new StorefrontJwtSettings { AccessTokenExpirationMinutes = 15 }), NullLogger<OnlineCustomerSessionAdminService>.Instance);
        var result = await service.RevokeAllSessionsAsync(state == "foreign-tenant" ? Guid.NewGuid() : f.VendorId,
            customerId, "fictional-admin", "contract revocation", default);
        if (state == "outer-transaction")
        {
            Assert.Empty(blacklist.Invocations); Assert.Empty(publisher.Invocations);
            Assert.Same(callerTransaction, db.Database.CurrentTransaction);
            Assert.False(result.Success); Assert.Equal("REVOKE_ALL_TRANSACTION_UNSUPPORTED", result.Error);
            Assert.False(db.ChangeTracker.HasChanges());
        }
        else if (state == "save-failure")
        { Assert.False(result.Success); Assert.Equal("REVOKE_ALL_FAILED", result.Error); }
        else
        {
            Assert.True(result.Success); Assert.Equal(effectsExpected ? 1 : 0, result.AffectedCount);
            if (state is "blacklist-failure" or "event-failure")
            { Assert.Equal("REVOKE_ALL_PARTIAL_SUCCESS", result.Error); Assert.Single(Assert.IsType<List<string>>(result.PartialFailures)); }
            else Assert.Null(result.Error);
        }
        Assert.Equal(effectsExpected ? state == "blacklist-failure" ? 3 : 1 : 0, blacklistCalls);
        Assert.Equal(effectsExpected ? 1 : 0, eventCalls);
        await using var final = f.ScopedDb(f.VendorId);
        Assert.Equal(effectsExpected || state == "no-active", (await final.OnlineCustomerRefreshTokens.AsNoTracking().SingleAsync(t => t.Id == target.Id)).IsRevoked);
        Assert.False((await final.OnlineCustomerRefreshTokens.AsNoTracking().SingleAsync(t => t.Id == other.Id)).IsRevoked);
        if (!effectsExpected) { blacklist.VerifyNoOtherCalls(); publisher.VerifyNoOtherCalls(); }
    }

    private sealed class FailRevokeSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Controlled token persistence failure before SQL commit");
    }
}
