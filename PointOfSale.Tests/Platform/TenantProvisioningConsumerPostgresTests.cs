using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using PointOfSale.API.Consumers;
using PointOfSale.API.Jobs;
using PointOfSale.API.Services.Auth;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.EventBus;

namespace PointOfSale.Tests.Platform;

[Collection("TenantProvisioningPostgres")]
public sealed class TenantProvisioningConsumerPostgresTests
{
    [TenantProvisioningPostgresFact]
    public async Task Consumer_ConvergesExactlyOnceAndRedeliveryIsFenced()
    {
        await using var db = await OpenDatabaseAsync();
        var authority = await SeedAuthorityAsync(db, TenantStatus.Active);
        var dependencies = Dependencies();
        var consumer = CreateConsumer(db, dependencies);
        var message = Message(authority);

        await consumer.Consume(Context(message));
        await consumer.Consume(Context(message));

        db.ChangeTracker.Clear();
        var persisted = await db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .SingleAsync(operation => operation.Id == authority.OperationId);
        Assert.Equal(TenantProvisioningState.Ready, persisted.State);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.NotNull(persisted.AccountsSeededAtUtc);
        Assert.NotNull(persisted.StorefrontProjectionQueuedAtUtc);
        Assert.NotNull(persisted.VerificationDispatchedAtUtc);
        Assert.NotNull(persisted.CompletedAtUtc);
        Assert.Null(persisted.LeaseId);
        dependencies.AccountSeeder.Verify(
            seeder => seeder.SeedAsync(authority.TenantId, It.IsAny<CancellationToken>()),
            Times.Once);
        dependencies.EmailVerification.Verify(
            service => service.CreateVerificationTokenAsync(
                It.Is<User>(user => user.Id == authority.OwnerUserId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        dependencies.Events.Verify(
            publisher => publisher.PublishAsync(
                It.Is<TenantProvisioningReadyEvent>(ready =>
                    ready.ProvisioningOperationId == authority.OperationId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TenantProvisioningPostgresFact]
    public async Task Consumer_ActiveLeaseFencesCompetitorAndExpiredLeaseRecovers()
    {
        await using var db = await OpenDatabaseAsync();
        var authority = await SeedAuthorityAsync(
            db,
            TenantStatus.Active,
            leaseId: Guid.NewGuid(),
            leaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(2));
        var dependencies = Dependencies();
        var consumer = CreateConsumer(db, dependencies);
        var message = Message(authority);

        await consumer.Consume(Context(message));
        dependencies.AccountSeeder.Verify(
            seeder => seeder.SeedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Id == authority.OperationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.LeaseExpiresAtUtc, DateTime.UtcNow.AddSeconds(-1))
                .SetProperty(operation => operation.UpdatedAtUtc, DateTime.UtcNow.AddMinutes(-10)));

        await consumer.Consume(Context(message));

        db.ChangeTracker.Clear();
        var persisted = await db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .SingleAsync(operation => operation.Id == authority.OperationId);
        Assert.Equal(TenantProvisioningState.Ready, persisted.State);
        Assert.Equal(1, persisted.AttemptCount);
        dependencies.AccountSeeder.Verify(
            seeder => seeder.SeedAsync(authority.TenantId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TenantProvisioningPostgresFact]
    public async Task Consumer_SuspensionPausesWorkAndReactivationResumesSameOperation()
    {
        await using var db = await OpenDatabaseAsync();
        var authority = await SeedAuthorityAsync(db, TenantStatus.Suspended);
        var dependencies = Dependencies();
        var consumer = CreateConsumer(db, dependencies);
        var message = Message(authority);

        await consumer.Consume(Context(message));

        db.ChangeTracker.Clear();
        var suspended = await db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .SingleAsync(operation => operation.Id == authority.OperationId);
        Assert.Equal(TenantProvisioningState.Suspended, suspended.State);
        Assert.NotNull(suspended.NextAttemptAtUtc);
        dependencies.AccountSeeder.Verify(
            seeder => seeder.SeedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await db.Tenants
            .IgnoreQueryFilters()
            .Where(tenant => tenant.Id == authority.TenantId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(tenant => tenant.Status, TenantStatus.Active)
                .SetProperty(tenant => tenant.IsActive, true)
                .SetProperty(tenant => tenant.SuspendedReason, (string?)null));
        await db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Id == authority.OperationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));

        await consumer.Consume(Context(message));

        db.ChangeTracker.Clear();
        var reactivated = await db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .SingleAsync(operation => operation.Id == authority.OperationId);
        Assert.Equal(TenantProvisioningState.Ready, reactivated.State);
        Assert.Equal(2, reactivated.AttemptCount);
        dependencies.AccountSeeder.Verify(
            seeder => seeder.SeedAsync(authority.TenantId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TenantProvisioningPostgresFact]
    public async Task RecoveryWorker_ReemitsPersistedCrashRecoveryAuthority()
    {
        var connectionString = DisposableConnectionString();
        Guid operationId;
        await using (var seed = await OpenDatabaseAsync())
        {
            var authority = await SeedAuthorityAsync(
                seed,
                TenantStatus.Active,
                updatedAtUtc: DateTime.UtcNow.AddMinutes(-20),
                leaseId: Guid.NewGuid(),
                leaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(-10));
            operationId = authority.OperationId;
        }

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
        await using var provider = services.BuildServiceProvider();
        var bus = new Mock<IBus>();
        var recovered = new List<TenantProvisioningRequestedEvent>();
        bus.Setup(endpoint => endpoint.Publish(
                It.IsAny<TenantProvisioningRequestedEvent>(),
                It.IsAny<CancellationToken>()))
            .Callback<TenantProvisioningRequestedEvent, CancellationToken>((message, _) => recovered.Add(message))
            .Returns(Task.CompletedTask);
        var worker = new TenantProvisioningRecoveryWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            bus.Object,
            NullLogger<TenantProvisioningRecoveryWorker>.Instance);

        var dispatched = await worker.DispatchDueOperationsAsync(CancellationToken.None);

        Assert.True(dispatched >= 1);
        var target = Assert.Single(recovered, message => message.ProvisioningOperationId == operationId);
        Assert.Equal("ahram.shopyxi.com", target.CanonicalHostname);
        Assert.StartsWith("tenant-provisioning-recovery:", target.IdempotencyKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Consumer_HasNoPerTenantDnsCertificateOrCloudflareDependency()
    {
        var dependencyTypes = typeof(TenantProvisioningConsumer)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
            .ToArray();
        var joined = string.Join('|', dependencyTypes);

        Assert.DoesNotContain("Cloudflare", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dns", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Certificate", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpClient", joined, StringComparison.OrdinalIgnoreCase);
    }

    [TenantProvisioningPostgresFact]
    public async Task Consumer_StaleWorkerCannotCheckpointOrOverwriteSuccessorLease()
    {
        await using var db = await OpenDatabaseAsync();
        await using var competitor = await OpenDatabaseAsync();
        var authority = await SeedAuthorityAsync(db, TenantStatus.Active);
        var dependencies = Dependencies();
        var successorLease = Guid.NewGuid();
        dependencies.AccountSeeder
            .Setup(seeder => seeder.SeedAsync(authority.TenantId, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // Deterministic takeover while the original worker is inside
                // an external/idempotent step; no five-minute wall-clock sleep.
                await competitor.TenantProvisioningOperations.IgnoreQueryFilters()
                    .Where(item => item.Id == authority.OperationId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.LeaseId, successorLease)
                        .SetProperty(item => item.AttemptCount, 2)
                        .SetProperty(item => item.LeaseExpiresAtUtc, DateTime.UtcNow.AddMinutes(5)));
            });

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            CreateConsumer(db, dependencies).Consume(Context(Message(authority))));

        competitor.ChangeTracker.Clear();
        var persisted = await competitor.TenantProvisioningOperations.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == authority.OperationId);
        Assert.Equal(successorLease, persisted.LeaseId);
        Assert.Equal(2, persisted.AttemptCount);
        Assert.Equal(TenantProvisioningState.Provisioning, persisted.State);
        Assert.Null(persisted.AccountsSeededAtUtc);
        Assert.Null(persisted.LastErrorCategory);
        dependencies.Events.Verify(publisher => publisher.PublishAsync(
            It.IsAny<TenantProvisioningReadyEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ModelSnapshot_HasNoPendingChangesAfterLeaseFencingMetadata()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=shopixy_provisioning_test_model;Username=postgres")
            .Options);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [TenantProvisioningPostgresFact]
    public async Task ProvisioningEvents_AreSqlOutboxAtomicEvenWithConsumeScopedPublishEndpoint()
    {
        await using var db = await OpenDatabaseAsync();
        await using var observer = await OpenDatabaseAsync();
        var authority = await SeedAuthorityAsync(db, TenantStatus.Active);
        var endpoint = new Mock<IPublishEndpoint>(MockBehavior.Strict);
        var publisher = new MassTransitEventPublisher(db, Mock.Of<IBus>(), endpoint.Object,
            NullLogger<MassTransitEventPublisher>.Instance, Mock.Of<ITenantContext>());
        var requested = Message(authority);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await publisher.PublishAsync(requested);
            await db.SaveChangesAsync();
            Assert.True(await db.OutboxMessages.IgnoreQueryFilters().AnyAsync(item => item.Id == requested.EventId));
            Assert.False(await observer.OutboxMessages.IgnoreQueryFilters().AnyAsync(item => item.Id == requested.EventId));
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.False(await db.OutboxMessages.IgnoreQueryFilters().AnyAsync(item => item.Id == requested.EventId));

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await publisher.PublishAsync(requested);
            await publisher.PublishAsync(new TenantProvisioningReadyEvent
            {
                TenantId = authority.TenantId,
                ProvisioningOperationId = authority.OperationId,
                CanonicalHostname = "ahram.shopyxi.com"
            });
            await publisher.PublishAsync(new StoreConfigCreatedEvent
            {
                TenantId = authority.TenantId,
                StoreConfigId = authority.StoreConfigId,
                StoreName = "Isolated outbox fixture",
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        Assert.Equal(3, await observer.OutboxMessages.IgnoreQueryFilters()
            .CountAsync(item => item.TenantId == authority.TenantId));
        endpoint.VerifyNoOtherCalls();
    }

    [TenantProvisioningPostgresFact]
    public async Task Persistence_CompletedAndReleasedLeaseCannotBeOverwrittenByStaleAdminRetry()
    {
        await using var db = await OpenDatabaseAsync();
        await using var competitor = await OpenDatabaseAsync();
        var authority = await SeedAuthorityAsync(db, TenantStatus.Active);
        var stale = await db.TenantProvisioningOperations.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == authority.OperationId);
        await CreateConsumer(competitor, Dependencies()).Consume(Context(Message(authority)));

        // Both the stale snapshot and completed record have a null LeaseId.
        // Attempt/state predicates must fence this ABA case as well.
        stale.State = TenantProvisioningState.Failed;
        stale.NextAttemptAtUtc = DateTime.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var persisted = await db.TenantProvisioningOperations.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == authority.OperationId);
        Assert.Equal(TenantProvisioningState.Ready, persisted.State);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Null(persisted.LeaseId);
    }

    [TenantProvisioningPostgresFact]
    public async Task Consumer_FailedDependencyRetriesSameDurableOperationWithoutDuplicateCheckpoint()
    {
        await using var db = await OpenDatabaseAsync();
        var authority = await SeedAuthorityAsync(db, TenantStatus.Active);
        var dependencies = Dependencies();
        dependencies.EmailVerification.SetupSequence(service => service.CreateVerificationTokenAsync(
                It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("isolated fixture failure"))
            .ReturnsAsync("fixture-token");
        var consumer = CreateConsumer(db, dependencies);
        await Assert.ThrowsAsync<TimeoutException>(() => consumer.Consume(Context(Message(authority))));
        await db.TenantProvisioningOperations.IgnoreQueryFilters()
            .Where(item => item.Id == authority.OperationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        db.ChangeTracker.Clear();
        await consumer.Consume(Context(Message(authority)));
        db.ChangeTracker.Clear();
        var persisted = await db.TenantProvisioningOperations.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == authority.OperationId);
        Assert.Equal(TenantProvisioningState.Ready, persisted.State);
        Assert.Equal(2, persisted.AttemptCount);
        dependencies.AccountSeeder.Verify(seeder => seeder.SeedAsync(authority.TenantId,
            It.IsAny<CancellationToken>()), Times.Once);
        dependencies.Events.Verify(publisher => publisher.PublishAsync(
            It.IsAny<StoreConfigCreatedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static TenantProvisioningConsumer CreateConsumer(
        ApplicationDbContext db,
        ProvisioningDependencies dependencies) => new(
        db,
        dependencies.AccountSeeder.Object,
        dependencies.EmailVerification.Object,
        dependencies.Events.Object,
        NullLogger<TenantProvisioningConsumer>.Instance);

    private static ProvisioningDependencies Dependencies()
    {
        var accounts = new Mock<ITenantAccountSeeder>(MockBehavior.Strict);
        accounts.Setup(seeder => seeder.SeedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var email = new Mock<IEmailVerificationService>(MockBehavior.Strict);
        email.Setup(service => service.CreateVerificationTokenAsync(
                It.IsAny<User>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("fixture-token");

        var events = new Mock<IEventPublisher>(MockBehavior.Strict);
        events.Setup(publisher => publisher.PublishAsync(
                It.IsAny<StoreConfigCreatedEvent>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        events.Setup(publisher => publisher.PublishAsync(
                It.IsAny<TenantProvisioningReadyEvent>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new ProvisioningDependencies(accounts, email, events);
    }

    private static ConsumeContext<TenantProvisioningRequestedEvent> Context(
        TenantProvisioningRequestedEvent message) =>
        Mock.Of<ConsumeContext<TenantProvisioningRequestedEvent>>(context =>
            context.Message == message &&
            context.CancellationToken == CancellationToken.None);

    private static TenantProvisioningRequestedEvent Message(ProvisioningAuthority authority) => new()
    {
        EventId = Guid.NewGuid(),
        TenantId = authority.TenantId,
        ProvisioningOperationId = authority.OperationId,
        OwnerUserId = authority.OwnerUserId,
        StoreConfigId = authority.StoreConfigId,
        CanonicalHostname = "ahram.shopyxi.com",
        IdempotencyKey = $"tenant-provisioning-test:{authority.OperationId:N}"
    };

    private static async Task<ProvisioningAuthority> SeedAuthorityAsync(
        ApplicationDbContext db,
        TenantStatus tenantStatus,
        DateTime? updatedAtUtc = null,
        Guid? leaseId = null,
        DateTime? leaseExpiresAtUtc = null)
    {
        var now = DateTime.UtcNow;
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var storeConfigId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var unique = tenantId.ToString("N");

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Provisioning fixture",
            Slug = $"t-{unique[..20]}",
            ContactEmail = $"owner-{unique}@example.test",
            Status = tenantStatus,
            IsActive = tenantStatus is TenantStatus.Active or TenantStatus.Suspended,
            Country = "EG",
            Currency = "EGP",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.Users.Add(new User
        {
            Id = ownerId,
            TenantId = tenantId,
            Username = $"owner-{unique}@example.test",
            NormalizedUsername = $"OWNER-{unique}@EXAMPLE.TEST",
            Email = $"owner-{unique}@example.test",
            NormalizedEmail = $"OWNER-{unique}@EXAMPLE.TEST",
            PasswordHash = "fixture-only-hash",
            FirstName = "Fixture",
            LastName = "Owner",
            Role = UserRole.Owner,
            IsActive = true,
            IsDeleted = false,
            EmailVerificationStatus = EmailVerificationStatus.Unverified,
            CreatedAt = now
        });
        db.StoreConfigs.Add(new StoreConfig
        {
            Id = storeConfigId,
            TenantId = tenantId,
            StoreName = "Provisioning fixture",
            StorefrontStatus = "disabled",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.TenantProvisioningOperations.Add(new TenantProvisioningOperation
        {
            Id = operationId,
            TenantId = tenantId,
            OwnerUserId = ownerId,
            StoreConfigId = storeConfigId,
            IdempotencyKeyHash = unique.PadRight(64, 'a'),
            RequestFingerprint = unique.PadRight(64, 'b'),
            CanonicalHostname = "ahram.shopyxi.com",
            State = TenantProvisioningState.Provisioning,
            AttemptCount = 0,
            LeaseId = leaseId,
            LeaseExpiresAtUtc = leaseExpiresAtUtc,
            CreatedAtUtc = now,
            UpdatedAtUtc = updatedAtUtc ?? now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new ProvisioningAuthority(tenantId, ownerId, storeConfigId, operationId);
    }

    private static async Task<ApplicationDbContext> OpenDatabaseAsync()
    {
        var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(DisposableConnectionString())
                .EnableDetailedErrors()
                .Options);
        await context.Database.MigrateAsync();
        return context;
    }

    private static string DisposableConnectionString()
    {
        var raw = Environment.GetEnvironmentVariable("TENANT_PROVISIONING_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException(
                "TENANT_PROVISIONING_POSTGRES_CONNECTION is required for the explicit disposable PostgreSQL suite.");
        var connection = new NpgsqlConnectionStringBuilder(raw)
        {
            Pooling = false,
            IncludeErrorDetail = true,
            SslMode = SslMode.Disable
        };
        if (connection.Host is not ("127.0.0.1" or "localhost") ||
            !connection.Database.StartsWith("shopixy_provisioning_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing tenant provisioning tests outside an explicit local disposable database.");
        }

        return connection.ConnectionString;
    }

    private sealed record ProvisioningDependencies(
        Mock<ITenantAccountSeeder> AccountSeeder,
        Mock<IEmailVerificationService> EmailVerification,
        Mock<IEventPublisher> Events);

    private sealed record ProvisioningAuthority(
        Guid TenantId,
        Guid OwnerUserId,
        Guid StoreConfigId,
        Guid OperationId);
}

[CollectionDefinition("TenantProvisioningPostgres", DisableParallelization = true)]
public sealed class TenantProvisioningPostgresCollection;

public sealed class TenantProvisioningPostgresFactAttribute : FactAttribute
{
    public TenantProvisioningPostgresFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TENANT_PROVISIONING_POSTGRES") != "1")
        {
            Skip = "Explicit local disposable PostgreSQL opt-in is required.";
        }
    }
}
