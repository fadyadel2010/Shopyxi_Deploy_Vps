using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Eventing;

namespace PointOfSale.Tests;

public sealed class PosCartAuditOutboxTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly ApplicationDbContext _db;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly PosCartAuditOutboxProcessor _processor;

    public PosCartAuditOutboxTests()
    {
        var services = new ServiceCollection();
        var name = Guid.NewGuid().ToString();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(name));
        _services = services.BuildServiceProvider();
        _db = _services.GetRequiredService<ApplicationDbContext>();
        _processor = new PosCartAuditOutboxProcessor(_services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PosCartAuditOutboxOptions { Enabled = true, TenantIds = [_tenant] }),
            NullLogger<PosCartAuditOutboxProcessor>.Instance);
    }

    [Fact]
    public async Task ProcessesOnce_PreservesSnapshotAndDoesNotInventLegacyActor()
    {
        var message = Event(_tenant, Guid.NewGuid(), 1);
        _db.PosCartOutboxMessages.Add(message); await _db.SaveChangesAsync();
        Assert.Equal(1, await _processor.ProcessTenantAsync(_tenant));
        Assert.Equal(0, await _processor.ProcessTenantAsync(_tenant));
        await _db.Entry(message).ReloadAsync();
        Assert.NotNull(message.ProcessedAtUtc);
        var audit = await _db.AuditLogs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(message.PayloadJson, audit.AfterDataJson);
        Assert.Equal(message.OccurredAtUtc, audit.TimestampUtc);
        Assert.Null(audit.UserId);
        Assert.Equal($"POSCART:{message.Id:D}", audit.ReferenceNumber);
        Assert.Single(await _db.PosCartOutboxMessages.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await _db.JournalEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await _db.StockMovements.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("PosCartHold")]
    [InlineData("PosCartResume")]
    [InlineData("PosCartCancel")]
    [InlineData("PosCartExpire")]
    [InlineData("PosCartRecover")]
    [InlineData("PosCartAbandon")]
    [InlineData("PosCartExpired")]
    [InlineData("PosCartCompleted")]
    public async Task ActualLifecycleEventNames_AreRecognized(string eventType)
    {
        var message = Event(_tenant, Guid.NewGuid(), 1); message.EventType = eventType; message.CreatedBy = Guid.NewGuid();
        _db.PosCartOutboxMessages.Add(message); await _db.SaveChangesAsync();
        Assert.Equal(1, await _processor.ProcessTenantAsync(_tenant));
        Assert.Equal(message.CreatedBy, (await _db.AuditLogs.IgnoreQueryFilters().SingleAsync()).UserId);
    }

    [Fact]
    public async Task UnknownOlderEvent_BlocksOnlyItsCart_NotOtherTenantsOrCarts()
    {
        var cart = Guid.NewGuid(); var otherTenant = Guid.NewGuid();
        var poison = Event(_tenant, cart, 1); poison.EventType = "Unknown";
        var later = Event(_tenant, cart, 2);
        var independent = Event(_tenant, Guid.NewGuid(), 1);
        var foreign = Event(otherTenant, Guid.NewGuid(), 1);
        _db.PosCartOutboxMessages.AddRange(poison, later, independent, foreign); await _db.SaveChangesAsync();
        Assert.Equal(1, await _processor.ProcessTenantAsync(_tenant));
        Assert.Equal(0, await _processor.ProcessTenantAsync(otherTenant));
        await _db.Entry(poison).ReloadAsync(); await _db.Entry(later).ReloadAsync(); await _db.Entry(foreign).ReloadAsync();
        Assert.Null(poison.ProcessedAtUtc); Assert.NotNull(poison.ProcessingError);
        Assert.Null(later.ProcessedAtUtc); Assert.Null(foreign.ProcessedAtUtc);
        Assert.Equal(_tenant, (await _db.AuditLogs.IgnoreQueryFilters().SingleAsync()).TenantId);
    }

    [Fact]
    public async Task MismatchedTenantPayload_RemainsPendingWithoutAudit()
    {
        var message = Event(_tenant, Guid.NewGuid(), 1);
        message.PayloadJson = message.PayloadJson.Replace(_tenant.ToString(), Guid.NewGuid().ToString());
        _db.PosCartOutboxMessages.Add(message); await _db.SaveChangesAsync();
        Assert.Equal(0, await _processor.ProcessTenantAsync(_tenant));
        await _db.Entry(message).ReloadAsync();
        Assert.Null(message.ProcessedAtUtc); Assert.NotNull(message.ProcessingError);
        Assert.Empty(await _db.AuditLogs.IgnoreQueryFilters().ToListAsync());
    }

    private static PosCartOutboxMessage Event(Guid tenant, Guid cart, int version) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenant, CartId = cart, CartVersion = version,
        EventType = "PosCartCreated", IdempotencyKey = Guid.NewGuid().ToString(),
        OccurredAtUtc = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
        PayloadJson = JsonSerializer.Serialize(new { tenantId = tenant, cartId = cart, version, status = 0, cashierId = Guid.NewGuid() })
    };

    public void Dispose() => _services.Dispose();
}
