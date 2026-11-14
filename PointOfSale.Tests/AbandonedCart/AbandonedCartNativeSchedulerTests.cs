using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Moq;
using Npgsql;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events.AbandonedCart;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.WhatsApp;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Infrastructure.WhatsApp.Interfaces;
using PointOfSale.Infrastructure.WhatsApp.Services;
using PointOfSale.Tests.Infrastructure;
using PointOfSale.Tests.Integration;
using AbandonedCartEntity = PointOfSale.Core.Entities.AbandonedCart;

namespace PointOfSale.Tests.AbandonedCart;

public sealed class AbandonedCartNativeSchedulerTests
{
    // Guarded disposable PostgreSQL only; no configured/shared provider. The
    // original deterministic schedule controls retain their local test clock.
    private static readonly DateTime NowUtc = new(2001, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ScheduleAsync_WhenDiscoveryIsReplayed_PersistsOneDeterministicSchedule()
    {
        await using var scope = await DisposableSchedulerScope.OpenAsync();
        var context = scope.Context;
        var tenantId = scope.TenantId;
        var cart = await SeedCartAsync(context, tenantId, AbandonedCartRecoveryStatus.Abandoned);
        var harness = new SchedulerHarness(context, tenantId);
        var message = new TriggerRecoveryEvent
        {
            TenantId = tenantId,
            AbandonedCartId = cart.Id,
            CartId = cart.CartId
        };

        await harness.Scheduler.ScheduleAsync(message);
        await harness.Scheduler.ScheduleAsync(message);

        var schedule = Assert.Single(await context.AbandonedCartRecoveryLogs
            .IgnoreQueryFilters()
            .AsNoTracking().Where(item => item.TenantId == tenantId)
            .ToListAsync());
        Assert.Equal($"native-recovery:{tenantId:N}:{cart.CartId:N}", schedule.IdempotencyKey);
        Assert.Equal(NowUtc, schedule.FirstMessageDueAtUtc);
        Assert.Equal(AbandonedCartMessageStatus.Scheduled, schedule.FirstMessageStatus);
        Assert.Equal(AbandonedCartMessageStatus.NotScheduled, schedule.SecondMessageStatus);
        Assert.Equal("UTC", schedule.TimeZoneId);
    }

    [Fact]
    public async Task DispatchDueAsync_WhenExpiredLeaseReachesRetryLimit_FailsScheduleAndCartRetry()
    {
        await using var scope = await DisposableSchedulerScope.OpenAsync();
        var context = scope.Context;
        var tenantId = scope.TenantId;
        var cart = await SeedCartAsync(context, tenantId, AbandonedCartRecoveryStatus.Abandoned);
        var schedule = await SeedScheduleAsync(
            context,
            cart,
            AbandonedCartMessageStatus.Dispatching,
            attempts: 2,
            leaseExpiresAtUtc: NowUtc.AddMinutes(-1));
        var harness = new SchedulerHarness(context, tenantId);
        harness.Eligibility
            .Setup(service => service.GetEligibilityStatusAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbandonedCartEligibilityStatus
            {
                IsEligible = false,
                AutomationEnabled = true,
                IneligibilityCode = "native_dispatcher_unavailable",
                IneligibilityReason = "Dispatcher unavailable"
            });

        var processed = await harness.Scheduler.DispatchDueAsync();

        Assert.Equal(1, processed);
        var updated = await context.AbandonedCartRecoveryLogs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == schedule.Id);
        Assert.Equal(3, updated.FirstMessageAttempts);
        Assert.Equal(AbandonedCartMessageStatus.Failed, updated.FirstMessageStatus);
        Assert.Null(updated.FirstMessageDueAtUtc);
        Assert.Null(updated.FirstMessageLeaseId);
        Assert.NotNull(updated.WorkflowCompletedAtUtc);
        harness.CartService.Verify(
            service => service.RecordTriggerFailureAsync(
                cart.Id,
                "Dispatcher unavailable",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchDueAsync_WhenFirstMessageIsSent_ConfirmsCartAndSchedulesFollowUp()
    {
        await using var scope = await DisposableSchedulerScope.OpenAsync();
        var context = scope.Context;
        var tenantId = scope.TenantId;
        var cart = await SeedCartAsync(context, tenantId, AbandonedCartRecoveryStatus.Abandoned);
        var schedule = await SeedScheduleAsync(context, cart, AbandonedCartMessageStatus.Scheduled);
        var harness = new SchedulerHarness(context, tenantId);
        WhatsAppMessageLogDocument? messageLog = null;
        harness.CartService
            .Setup(service => service.TryMarkAsPendingAsync(cart.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        harness.CartService
            .Setup(service => service.ConfirmTriggerSuccessAsync(cart.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        harness.MessageLogService
            .Setup(service => service.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()))
            .ReturnsAsync((WhatsAppMessageLogDocument value) =>
            {
                messageLog = value;
                return value;
            });
        harness.Dispatcher
            .Setup(service => service.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                messageLog!.Status = "SENT";
                messageLog.SentAtUtc = NowUtc;
            })
            .Returns(Task.CompletedTask);
        harness.MessageLogService
            .Setup(service => service.GetByIdAsync(It.IsAny<string>()))
            .Returns(() => Task.FromResult(messageLog));
        harness.RateLimitService
            .Setup(service => service.RecordMessageSentAsync(tenantId, cart.PhoneNumber, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var processed = await harness.Scheduler.DispatchDueAsync();

        Assert.Equal(1, processed);
        var updated = await context.AbandonedCartRecoveryLogs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == schedule.Id);
        Assert.True(updated.FirstMessageSent);
        Assert.Equal(NowUtc, updated.FirstMessageSentAtUtc);
        Assert.Equal(AbandonedCartMessageStatus.Sent, updated.FirstMessageStatus);
        Assert.Equal(NowUtc.AddHours(1), updated.SecondMessageDueAtUtc);
        Assert.Equal(AbandonedCartMessageStatus.Scheduled, updated.SecondMessageStatus);
        Assert.False(string.IsNullOrWhiteSpace(updated.FirstMessageDispatchLogId));
        harness.CartService.Verify(
            service => service.ConfirmTriggerSuccessAsync(cart.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Dispatcher.Verify(
            service => service.DispatchAsync(updated.FirstMessageDispatchLogId!, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordTriggerFailureAsync_WhenCartIsAbandoned_MakesRetryExhaustionTerminal()
    {
        await using var scope = await DisposableSchedulerScope.OpenAsync();
        var context = scope.Context;
        var tenantId = scope.TenantId;
        var cart = await SeedCartAsync(context, tenantId, AbandonedCartRecoveryStatus.Abandoned);
        var service = new AbandonedCartService(
            context,
            Mock.Of<ITenantAutomationSettingsService>(),
            new EphemeralDataProtectionProvider(),
            NullLogger<AbandonedCartService>.Instance);

        await service.RecordTriggerFailureAsync(cart.Id, "temporary failure");
        await service.RecordTriggerFailureAsync(cart.Id, "temporary failure");
        await service.RecordTriggerFailureAsync(cart.Id, "final failure");

        var updated = await context.AbandonedCarts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == cart.Id);
        Assert.Equal(AbandonedCartEntity.MaxRetryCount, updated.RetryCount);
        Assert.Equal(AbandonedCartRecoveryStatus.Failed, updated.RecoveryStatus);
        Assert.Equal("final failure", updated.LastErrorMessage);
    }

    private static async Task<AbandonedCartEntity> SeedCartAsync(
        ApplicationDbContext context,
        Guid tenantId,
        AbandonedCartRecoveryStatus status)
    {
        context.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Native Recovery Test",
            Slug = $"native-recovery-{tenantId:N}",
            IsActive = true,
            Status = TenantStatus.Active,
            CreatedAtUtc = NowUtc.AddDays(-1)
        });
        var cart = new AbandonedCartEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CartId = Guid.NewGuid(),
            SessionId = Guid.NewGuid().ToString("N"),
            PhoneNumber = "+201000000000",
            ContactChannel = "phone",
            PhoneVerified = true,
            CartSnapshotJson = "{}",
            LastActivityAtUtc = NowUtc.AddHours(-1),
            AbandonedAtUtc = NowUtc.AddMinutes(-30),
            RecoveryStatus = status,
            CreatedAtUtc = NowUtc.AddHours(-1),
            UpdatedAtUtc = NowUtc.AddHours(-1)
        };
        context.AbandonedCarts.Add(cart);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return cart;
    }

    private static async Task<AbandonedCartRecoveryLog> SeedScheduleAsync(
        ApplicationDbContext context,
        AbandonedCartEntity cart,
        string status,
        int attempts = 0,
        DateTime? leaseExpiresAtUtc = null)
    {
        var schedule = new AbandonedCartRecoveryLog
        {
            Id = Guid.NewGuid(),
            TenantId = cart.TenantId,
            AbandonedCartId = cart.Id,
            CartId = cart.CartId,
            PhoneNumber = cart.PhoneNumber,
            FirstMessageDueAtUtc = NowUtc.AddMinutes(-1),
            FirstMessageStatus = status,
            FirstMessageAttempts = attempts,
            FirstMessageLeaseId = status == AbandonedCartMessageStatus.Dispatching ? "expired-lease" : null,
            FirstMessageLeaseExpiresAtUtc = leaseExpiresAtUtc,
            SecondMessageStatus = AbandonedCartMessageStatus.NotScheduled,
            TimeZoneId = "UTC",
            WorkflowStartedAtUtc = NowUtc.AddMinutes(-30),
            IdempotencyKey = $"native-recovery:{cart.TenantId:N}:{cart.CartId:N}",
            CreatedAtUtc = NowUtc.AddMinutes(-30)
        };
        context.AbandonedCartRecoveryLogs.Add(schedule);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return schedule;
    }

    [Theory]
    [InlineData("SENT", AbandonedCartMessageStatus.Sent)]
    [InlineData("DELIVERY_UNKNOWN", AbandonedCartMessageStatus.DeliveryUnknown)]
    [InlineData("SKIPPED", AbandonedCartMessageStatus.Suppressed)]
    [InlineData("FAILED", AbandonedCartMessageStatus.RetryScheduled)]
    public async Task ActualSqlRowOwnershipOutlivesAnAdmittedRecordedDispatchAndPreventsExpiredLeaseTakeover(string outcome, string expected)
    {
        await using var scope = await DisposableSchedulerScope.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        var context = scope.Context; var tenantId = scope.TenantId;
        var cart = await SeedCartAsync(context, tenantId, AbandonedCartRecoveryStatus.Abandoned);
        var schedule = await SeedScheduleAsync(context, cart, AbandonedCartMessageStatus.Dispatching, attempts: 1, leaseExpiresAtUtc: DateTime.UtcNow.AddSeconds(8));
        var harness = new SchedulerHarness(context, tenantId, TimeProvider.System);
        harness.Settings.SecondMessageDelayMinutes = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WhatsAppMessageLogDocument? recorded = null;
        harness.CartService.Setup(s => s.TryMarkAsPendingAsync(cart.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        harness.CartService.Setup(s => s.ConfirmTriggerSuccessAsync(cart.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        harness.MessageLogService.Setup(s => s.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()))
            .ReturnsAsync((WhatsAppMessageLogDocument value) => { recorded = value; return value; });
        harness.MessageLogService.Setup(s => s.GetByIdAsync(It.IsAny<string>())).Returns(() => Task.FromResult(recorded));
        harness.Dispatcher.Setup(s => s.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken request) =>
            {
                entered.TrySetResult(); await release.Task.WaitAsync(request);
                recorded!.Status = outcome; recorded.SentAtUtc = DateTime.UtcNow;
            });
        await using var observer = new NpgsqlConnection(scope.ConnectionString); await observer.OpenAsync(ct);
        await using var competingConnection = new NpgsqlConnection(scope.ConnectionString); await competingConnection.OpenAsync(ct);
        await using var competing = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(competingConnection).Options);
        var competitor = new SchedulerHarness(competing, tenantId, TimeProvider.System);
        var method = typeof(NativeAbandonedCartRecoveryScheduler).GetMethod("DispatchClaimedAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pending = (Task)method.Invoke(harness.Scheduler, [schedule.Id, 1, "expired-lease", ct])!;
        Task<int>? other = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await using (var expire = new NpgsqlCommand("SELECT pg_sleep(GREATEST(0,EXTRACT(EPOCH FROM (@expiry-clock_timestamp())))+0.05)", observer))
            {
                expire.Parameters.AddWithValue("expiry", schedule.FirstMessageLeaseExpiresAtUtc!.Value); await expire.ExecuteNonQueryAsync(ct);
            }
            await using (var expired = new NpgsqlCommand("SELECT \"FirstMessageLeaseExpiresAtUtc\"<clock_timestamp() FROM \"AbandonedCartRecoveryLogs\" WHERE \"Id\"=@id", observer))
            {
                expired.Parameters.AddWithValue("id", schedule.Id); Assert.True(Assert.IsType<bool>(await expired.ExecuteScalarAsync(ct)));
            }
            other = competitor.Scheduler.DispatchDueAsync(ct);
            var blocked = false; var watch = System.Diagnostics.Stopwatch.StartNew();
            var holder = ((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !other.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", observer);
                probe.Parameters.AddWithValue("holder", holder); probe.Parameters.AddWithValue("waiter", competingConnection.ProcessID);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                await Task.Delay(20, ct);
            }
            Assert.True(blocked, "The actual candidate claimant must wait on the transaction retaining the admitted dispatch.");
            Assert.False(pending.IsCompleted); Assert.False(other.IsCompleted);
            release.TrySetResult(); await pending;
            Assert.Equal(0, await other);
            await using var fresh = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(scope.ConnectionString).Options);
            var completed = await fresh.AbandonedCartRecoveryLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct);
            Assert.Equal(expected, completed.FirstMessageStatus); Assert.Null(completed.FirstMessageLeaseId);
            Assert.Equal(1, completed.FirstMessageAttempts); // competitor cannot increment the expired owner token
            harness.Dispatcher.Verify(s => s.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            competitor.Dispatcher.Verify(s => s.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            competitor.MessageLogService.Verify(s => s.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()), Times.Never);
            if (outcome == "SENT") harness.CartService.Verify(s => s.ConfirmTriggerSuccessAsync(cart.Id, It.IsAny<CancellationToken>()), Times.Once);
            else harness.CartService.Verify(s => s.ConfirmTriggerSuccessAsync(cart.Id, It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            release.TrySetResult();
            try { await pending; } catch { /* Drain exact owned dispatch; preserve assertion failures. */ }
            if (other != null) { try { await other; } catch { /* Drain exact owned claimant. */ } }
        }
    }

    [Theory]
    [InlineData("dispatch-first")]
    [InlineData("dispatch-second")]
    [InlineData("pending")]
    [InlineData("confirm")]
    public async Task CanceledCartCannotAdmitRecoveryDispatchOrRawLifecycleMutation(string command)
    {
        await using var scope = await DisposableSchedulerScope.OpenAsync();
        var context = scope.Context;
        var state = command switch {
            "confirm" => AbandonedCartRecoveryStatus.Pending,
            "dispatch-second" => AbandonedCartRecoveryStatus.Triggered,
            _ => AbandonedCartRecoveryStatus.Abandoned
        };
        var cart = await SeedCartAsync(context, scope.TenantId, state);
        var schedule = await SeedScheduleAsync(context, cart, AbandonedCartMessageStatus.Dispatching,
            attempts: 1, leaseExpiresAtUtc: NowUtc.AddHours(1));
        if (command == "dispatch-second")
        {
            var tracked = await context.AbandonedCartRecoveryLogs.IgnoreQueryFilters().SingleAsync();
            tracked.FirstMessageStatus = AbandonedCartMessageStatus.Sent;
            tracked.FirstMessageSentAtUtc = NowUtc.AddMinutes(-10);
            tracked.SecondMessageStatus = AbandonedCartMessageStatus.Dispatching;
            tracked.SecondMessageDueAtUtc = NowUtc.AddMinutes(-1);
            tracked.SecondMessageAttempts = 1;
            tracked.SecondMessageLeaseId = "expired-lease";
            tracked.SecondMessageLeaseExpiresAtUtc = NowUtc.AddHours(1);
            await context.SaveChangesAsync(); context.ChangeTracker.Clear();
        }
        Assert.Equal(1, await context.AbandonedCarts.IgnoreQueryFilters().Where(c => c.Id == cart.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RecoveryStatus, AbandonedCartRecoveryStatus.Canceled)));

        if (command is "pending" or "confirm")
        {
            var service = new AbandonedCartService(context, Mock.Of<ITenantAutomationSettingsService>(),
                new EphemeralDataProtectionProvider(), NullLogger<AbandonedCartService>.Instance);
            Assert.False(command == "pending" ? await service.TryMarkAsPendingAsync(cart.Id) : await service.ConfirmTriggerSuccessAsync(cart.Id));
        }
        else
        {
            var harness = new SchedulerHarness(context, scope.TenantId);
            harness.Settings.SecondMessageDelayMinutes = 0;
            harness.CartService.Setup(s => s.TryMarkAsPendingAsync(cart.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            harness.CartService.Setup(s => s.ConfirmTriggerSuccessAsync(cart.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            WhatsAppMessageLogDocument? recorded = null;
            harness.MessageLogService.Setup(s => s.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()))
                .ReturnsAsync((WhatsAppMessageLogDocument value) => { recorded = value; return value; });
            harness.Dispatcher.Setup(s => s.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback(() => { recorded!.Status = "SENT"; recorded.SentAtUtc = NowUtc; }).Returns(Task.CompletedTask);
            harness.MessageLogService.Setup(s => s.GetByIdAsync(It.IsAny<string>())).Returns(() => Task.FromResult(recorded));
            var method = typeof(NativeAbandonedCartRecoveryScheduler).GetMethod("DispatchClaimedAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await ((Task)method.Invoke(harness.Scheduler, [schedule.Id, command == "dispatch-second" ? 2 : 1, "expired-lease", CancellationToken.None])!)
                .WaitAsync(TimeSpan.FromSeconds(20));
            harness.MessageLogService.Verify(s => s.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()), Times.Never);
            harness.Dispatcher.Verify(s => s.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            harness.CartService.Verify(s => s.TryMarkAsPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
            harness.CartService.Verify(s => s.ConfirmTriggerSuccessAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
            var current = await context.AbandonedCartRecoveryLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(AbandonedCartMessageStatus.Suppressed, command == "dispatch-second" ? current.SecondMessageStatus : current.FirstMessageStatus);
            Assert.Null(command == "dispatch-second" ? current.SecondMessageLeaseId : current.FirstMessageLeaseId);
        }
        var persisted = await context.AbandonedCarts.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(AbandonedCartRecoveryStatus.Canceled, persisted.RecoveryStatus);
        Assert.Null(persisted.PendingAtUtc); Assert.Null(persisted.TriggeredAtUtc);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task FinalSqlDispatchAuthorityRejectsCanceledCartWithoutRenderingRecoveryMessage(int sequence, bool canceled)
    {
        await using var scope = await DisposableSchedulerScope.OpenAsync();
        var context = scope.Context;
        var cart = await SeedCartAsync(context, scope.TenantId,
            sequence == 1 ? AbandonedCartRecoveryStatus.Pending : AbandonedCartRecoveryStatus.Triggered);
        await SeedScheduleAsync(context, cart, AbandonedCartMessageStatus.Dispatching,
            attempts: 1, leaseExpiresAtUtc: NowUtc.AddHours(1));
        var schedule = await context.AbandonedCartRecoveryLogs.IgnoreQueryFilters().SingleAsync();
        if (sequence == 2) schedule.FirstMessageSentAtUtc = NowUtc.AddMinutes(-10);
        var tenant = await context.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == scope.TenantId);
        tenant.StorefrontBaseUrl = "https://fictional-cart.example.invalid";
        var account = new WhatsAppAccount { Id = Guid.NewGuid(), TenantId = scope.TenantId,
            PhoneNumber = "+201000000001", Enabled = true, Status = WhatsAppAccountStatus.Connected,
            UseForAbandonedCartRecovery = true, DisplayName = "Fictional no-network sender" };
        context.WhatsAppAccounts.Add(account);
        await context.SaveChangesAsync(); context.ChangeTracker.Clear();
        if (canceled) Assert.Equal(1, await context.AbandonedCarts.IgnoreQueryFilters().Where(c => c.Id == cart.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RecoveryStatus, AbandonedCartRecoveryStatus.Canceled)));
        var settings = new Mock<ITenantAutomationSettingsService>();
        settings.Setup(s => s.GetSettingsAsync(scope.TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAutomationSettings { TenantId = scope.TenantId, EnableAbandonedRecovery = true });
        var templates = new Mock<IWhatsAppTemplateResolver>();
        templates.Setup(t => t.ResolveAbandonedCartRecoveryAsync(scope.TenantId,
            It.IsAny<AbandonedCartRecoveryMessageData>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Fictional local message, never dispatched");
        var carts = new Mock<IAbandonedCartService>();
        carts.Setup(c => c.GenerateRecoveryToken(It.IsAny<AbandonedCartEntity>())).Returns("fictional-local-token");
        var authority = new WhatsAppDispatchAuthority(context, templates.Object, Mock.Of<IWhatsAppRateLimitService>(),
            settings.Object, carts.Object, new ConfigurationBuilder().Build());
        var result = await authority.AuthorizeAsync(new WhatsAppMessageLogDocument {
            Id = "fictional-authority-only", TenantId = scope.TenantId.ToString(), AccountId = account.Id.ToString(),
            RelatedEntityId = cart.Id.ToString(), MessageType = sequence == 1 ? "ABANDONED_CART_RECOVERY_FIRST" : "ABANDONED_CART_RECOVERY_SECOND",
            TargetPhone = "+201999999999"
        }, null, default);
        if (canceled) Assert.Null(result);
        else
        {
            Assert.NotNull(result); Assert.Equal(cart.PhoneNumber, result.TargetPhone);
            Assert.Equal(scope.TenantId, result.TenantId); Assert.Equal(account.Id, result.AccountId);
        }
        templates.Verify(t => t.ResolveAbandonedCartRecoveryAsync(scope.TenantId,
            It.IsAny<AbandonedCartRecoveryMessageData>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), canceled ? Times.Never() : Times.Once());
        carts.Verify(c => c.GenerateRecoveryToken(It.IsAny<AbandonedCartEntity>()), canceled ? Times.Never() : Times.Once());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AdmittedRecoveryRetainsExactCartRowAuthorityThroughRecordedOutcome(int sequence)
    {
        await using var scope = await DisposableSchedulerScope.OpenAsync();
        var context = scope.Context;
        var cart = await SeedCartAsync(context, scope.TenantId,
            sequence == 1 ? AbandonedCartRecoveryStatus.Pending : AbandonedCartRecoveryStatus.Triggered);
        var schedule = await SeedScheduleAsync(context, cart, AbandonedCartMessageStatus.Dispatching,
            attempts: 1, leaseExpiresAtUtc: NowUtc.AddHours(1));
        if (sequence == 2) await SetSecondDispatchAsync(context);
        var harness = new SchedulerHarness(context, scope.TenantId);
        // Keep the second message actually due: the default 60-minute delay
        // would reschedule before entering the probe and prove no row ownership.
        harness.Settings.SecondMessageDelayMinutes = 1;
        string? observed = null;
        harness.MessageLogService.Setup(s => s.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()))
            .Returns(async (WhatsAppMessageLogDocument log) => {
                await using var observer = new NpgsqlConnection(scope.ConnectionString); await observer.OpenAsync();
                await using var transaction = await observer.BeginTransactionAsync();
                await using var probe = new NpgsqlCommand(
                    "SELECT 1 FROM \"AbandonedCarts\" WHERE \"Id\"=@id AND \"TenantId\"=@tenant AND \"CartId\"=@cart FOR UPDATE NOWAIT", observer, transaction);
                probe.Parameters.AddWithValue("id", cart.Id); probe.Parameters.AddWithValue("tenant", cart.TenantId);
                probe.Parameters.AddWithValue("cart", cart.CartId);
                try { await probe.ExecuteScalarAsync(); }
                catch (PostgresException error) { observed = error.SqlState; }
                log.Status = "SKIPPED"; log.FailureReason = "Fictional recorded local suppression";
                return log;
            });
        var method = typeof(NativeAbandonedCartRecoveryScheduler).GetMethod("DispatchClaimedAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(harness.Scheduler, [schedule.Id, sequence, "expired-lease", CancellationToken.None])!)
            .WaitAsync(TimeSpan.FromSeconds(20));
        harness.MessageLogService.Verify(s => s.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()), Times.Once);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, observed);
        Assert.Null(context.Database.CurrentTransaction);
        harness.Dispatcher.Verify(s => s.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var current = await context.AbandonedCartRecoveryLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(AbandonedCartMessageStatus.Suppressed, sequence == 1 ? current.FirstMessageStatus : current.SecondMessageStatus);
    }

    [Fact]
    public async Task CartCancellationCommittedDuringAdmissionWaitSuppressesBeforeAnyMessageLogOrDispatch()
    {
        await using var scope = await DisposableSchedulerScope.OpenAsync();
        var context = scope.Context;
        var cart = await SeedCartAsync(context, scope.TenantId, AbandonedCartRecoveryStatus.Pending);
        var schedule = await SeedScheduleAsync(context, cart, AbandonedCartMessageStatus.Dispatching,
            attempts: 1, leaseExpiresAtUtc: NowUtc.AddHours(1));
        await context.Database.OpenConnectionAsync();
        var waiter = ((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
        await using var owner = new NpgsqlConnection(scope.ConnectionString); await owner.OpenAsync();
        await using var observer = new NpgsqlConnection(scope.ConnectionString); await observer.OpenAsync();
        await using var transaction = await owner.BeginTransactionAsync();
        await using (var cancel = new NpgsqlCommand("UPDATE \"AbandonedCarts\" SET \"RecoveryStatus\"='Canceled' WHERE \"Id\"=@id", owner, transaction))
        { cancel.Parameters.AddWithValue("id", cart.Id); Assert.Equal(1, await cancel.ExecuteNonQueryAsync()); }
        var harness = new SchedulerHarness(context, scope.TenantId);
        harness.MessageLogService.Setup(s => s.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()))
            .ReturnsAsync((WhatsAppMessageLogDocument log) => { log.Status = "SKIPPED"; return log; });
        var method = typeof(NativeAbandonedCartRecoveryScheduler).GetMethod("DispatchClaimedAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pending = (Task)method.Invoke(harness.Scheduler, [schedule.Id, 1, "expired-lease", CancellationToken.None])!;
        var blocked = false;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !pending.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", observer);
                probe.Parameters.AddWithValue("holder", owner.ProcessID); probe.Parameters.AddWithValue("waiter", waiter);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync())) { blocked = true; break; }
                await Task.Delay(20);
            }
        }
        finally { await transaction.CommitAsync(); await pending.WaitAsync(TimeSpan.FromSeconds(20)); }
        Assert.True(blocked, "Admission must wait on the exact cart row, not read an older committed live-cart image.");
        Assert.Null(context.Database.CurrentTransaction);
        harness.MessageLogService.Verify(s => s.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()), Times.Never);
        harness.Dispatcher.Verify(s => s.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var current = await context.AbandonedCartRecoveryLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(AbandonedCartMessageStatus.Suppressed, current.FirstMessageStatus);
        Assert.Null(current.FirstMessageLeaseId);
        var canceled = await context.AbandonedCarts.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(AbandonedCartRecoveryStatus.Canceled, canceled.RecoveryStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveCartRawTransitionsRetainTheirExistingSuccessfulStateMachine(bool confirm)
    {
        await using var scope = await DisposableSchedulerScope.OpenAsync();
        var context = scope.Context;
        await SeedCartAsync(context, scope.TenantId, AbandonedCartRecoveryStatus.Abandoned);
        var service = new AbandonedCartService(context, Mock.Of<ITenantAutomationSettingsService>(),
            new EphemeralDataProtectionProvider(), NullLogger<AbandonedCartService>.Instance);
        var cart = await context.AbandonedCarts.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.True(await service.TryMarkAsPendingAsync(cart.Id));
        if (confirm) Assert.True(await service.ConfirmTriggerSuccessAsync(cart.Id));
        var current = await context.AbandonedCarts.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(confirm ? AbandonedCartRecoveryStatus.Triggered : AbandonedCartRecoveryStatus.Pending, current.RecoveryStatus);
        if (confirm) { Assert.Null(current.PendingAtUtc); Assert.NotNull(current.TriggeredAtUtc); }
        else { Assert.NotNull(current.PendingAtUtc); Assert.Null(current.TriggeredAtUtc); }
    }

    private static async Task SetSecondDispatchAsync(ApplicationDbContext context)
    {
        var log = await context.AbandonedCartRecoveryLogs.IgnoreQueryFilters().SingleAsync();
        log.FirstMessageStatus = AbandonedCartMessageStatus.Sent; log.FirstMessageSentAtUtc = NowUtc.AddMinutes(-10);
        log.SecondMessageStatus = AbandonedCartMessageStatus.Dispatching; log.SecondMessageDueAtUtc = NowUtc.AddMinutes(-1);
        log.SecondMessageAttempts = 1; log.SecondMessageLeaseId = "expired-lease"; log.SecondMessageLeaseExpiresAtUtc = NowUtc.AddHours(1);
        await context.SaveChangesAsync(); context.ChangeTracker.Clear();
    }

    private sealed class DisposableSchedulerScope : IAsyncDisposable
    {
        private ProductionCoordinationAuthorityTests.Fixture fixture = null!;
        public Guid TenantId { get; } = Guid.NewGuid();
        public ApplicationDbContext Context { get; private set; } = null!;
        public string ConnectionString => fixture.ConnectionString;
        public static async Task<DisposableSchedulerScope> OpenAsync()
        {
            var scope = new DisposableSchedulerScope { fixture = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync() };
            try { scope.Context = scope.fixture.ScopedDb(scope.TenantId); return scope; }
            catch { await scope.fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await fixture.DisposeAsync(); }
    }

    private sealed class SchedulerHarness
    {
        public SchedulerHarness(ApplicationDbContext context, Guid tenantId, TimeProvider? timeProvider = null)
        {
            Settings = new TenantAutomationSettings
            {
                TenantId = tenantId,
                EnableAbandonedRecovery = true,
                FirstMessageDelayMinutes = 30,
                SecondMessageDelayMinutes = 60,
                WorkingHoursFrom = TimeSpan.Zero,
                WorkingHoursTo = new TimeSpan(23, 59, 0),
                TimeZoneId = "UTC"
            };
            SettingsService
                .Setup(service => service.GetSettingsAsync(tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Settings);
            Eligibility
                .Setup(service => service.GetEligibilityStatusAsync(tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AbandonedCartEligibilityStatus
                {
                    IsEligible = true,
                    AutomationEnabled = true,
                    NativeSchedulerOperational = true,
                    NativeDispatcherOperational = true,
                    TimeZoneId = "UTC"
                });
            AccountService
                .Setup(service => service.GetActiveAccountForUseCaseAsync(
                    tenantId,
                    WhatsAppUseCase.AbandonedCartRecovery,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WhatsAppAccount
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Enabled = true,
                    Status = WhatsAppAccountStatus.Connected
                });

            Scheduler = new NativeAbandonedCartRecoveryScheduler(
                context,
                SettingsService.Object,
                Eligibility.Object,
                CartService.Object,
                AccountService.Object,
                MessageLogService.Object,
                Dispatcher.Object,
                RateLimitService.Object,
                timeProvider ?? new FixedTimeProvider(NowUtc),
                NullLogger<NativeAbandonedCartRecoveryScheduler>.Instance);
        }

        public TenantAutomationSettings Settings { get; }
        public Mock<ITenantAutomationSettingsService> SettingsService { get; } = new();
        public Mock<IAbandonedCartEligibilityService> Eligibility { get; } = new();
        public Mock<IAbandonedCartService> CartService { get; } = new();
        public Mock<IWhatsAppAccountService> AccountService { get; } = new();
        public Mock<IWhatsAppMessageLogService> MessageLogService { get; } = new();
        public Mock<IWhatsAppMessageDispatcher> Dispatcher { get; } = new();
        public Mock<IWhatsAppRateLimitService> RateLimitService { get; } = new();
        public NativeAbandonedCartRecoveryScheduler Scheduler { get; }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
