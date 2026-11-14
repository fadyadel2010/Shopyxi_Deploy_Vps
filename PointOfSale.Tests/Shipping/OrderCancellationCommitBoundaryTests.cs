using System.Reflection;
using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.EventBus;
using PointOfSale.Infrastructure.MultiTenancy;

namespace PointOfSale.Tests.Shipping;

public class OrderCancellationCommitBoundaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancellationFact_UsesSourceTransaction_NotConsumeScopedEndpoint(bool boxed, bool commit)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var tenant = new TenantContext(); tenant.SetTenantId(Guid.NewGuid());
        await using var db = new BoundaryDb(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options, tenant);
        await db.Database.EnsureCreatedAsync();
        var endpoint = new Mock<IPublishEndpoint>(MockBehavior.Strict);
        var publisher = new MassTransitEventPublisher(db, Mock.Of<IBus>(), endpoint.Object,
            NullLogger<MassTransitEventPublisher>.Instance, tenant);
        var order = new Order { Id = Guid.NewGuid(), TenantId = tenant.TenantId!.Value };
        order.AdvanceProjectionMutationVersion(1); db.Attach(order);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var fact = new OrderCancelledEvent { TenantId = tenant.TenantId, OrderId = order.Id,
            OrderNumber = "QA-ONLY", CancellationReason = "Synthetic rollback/commit regression" };
        if (boxed) await publisher.PublishAsync((object)fact); else await publisher.PublishAsync(fact);
        var staged = Assert.Single(db.ChangeTracker.Entries<OutboxMessage>());
        Assert.Equal(fact.EventId, staged.Entity.Id);
        Assert.Contains(nameof(OrderCancelledEvent), staged.Entity.EventType);
        Assert.Contains(order.Id.ToString(), staged.Entity.EventPayload);
        endpoint.VerifyNoOtherCalls();
        await db.SaveChangesAsync();
        if (commit) await transaction.CommitAsync(); else await transaction.RollbackAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(commit ? 1 : 0, await db.OutboxMessages.CountAsync());
    }

    private sealed class BoundaryDb(DbContextOptions<ApplicationDbContext> options, ITenantContext tenant)
        : ApplicationDbContext(options, tenant)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            foreach (var p in typeof(ApplicationDbContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>)))
            {
                var type = p.PropertyType.GetGenericArguments()[0];
                if (type != typeof(Order) && type != typeof(OutboxMessage)) builder.Ignore(type);
            }
            var order = builder.Entity<Order>();
            foreach (var p in typeof(Order).GetProperties())
                if (p.Name is not ("Id" or "TenantId" or "ProjectionMutationVersion")) order.Ignore(p.Name);
            order.HasKey(x => x.Id);
            builder.Entity<OutboxMessage>().HasKey(x => x.Id);
        }
    }
}
