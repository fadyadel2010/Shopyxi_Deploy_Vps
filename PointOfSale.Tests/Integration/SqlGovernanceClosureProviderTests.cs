using System.Data.Common;
using System.Security.Claims;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PointOfSale.API.Models;
using PointOfSale.API.Services.Auth;
using Moq;
using Npgsql;
using PointOfSale.API.Jobs;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs;
using PointOfSale.API.Services.Audit;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Cart;
using PointOfSale.API.Services.Checkout;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.WhatsApp;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Infrastructure.WhatsApp.Interfaces;

namespace PointOfSale.Tests.Integration;

// This fixture rejects anything except the isolated PostgreSQL18 contract.
// No application host, production worker or external storage client is constructed.
public sealed class SqlGovernanceClosureProviderTests
{
    [Theory]
    [InlineData("account")]
    [InlineData("user")]
    public async Task AffiliateVerificationCannotUseAnEligibilityReadAfterConcurrentIdentityDeletion(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "APP2RACE", "Disposable identity race");
        account.Id = Guid.NewGuid();
        var user = AffiliateUser.Create(Guid.Empty, "race@example.invalid", "fixture-not-a-login", account.Id);
        user.Id = userId;
        var raw = AffiliateEmailVerificationToken.NewRawToken();
        var token = AffiliateEmailVerificationToken.Create(userId, raw, DateTime.UtcNow.AddHours(1));
        f.Db.AddRange(account, user, token); await f.Db.SaveChangesAsync();
        var pause = new PauseVerificationIdentityClaim();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var controller = new AffiliateAuthController(db, null!, Options.Create(new AffiliateJwtSettings()),
            new PasswordHasher<AffiliateUser>(), Mock.Of<IEmailSender>(MockBehavior.Strict),
            new ConfigurationBuilder().Build(), NullLogger<AffiliateAuthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var pending = controller.VerifyEmail(new AffiliateVerifyEmailDto { Token = raw }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            // The controller has already read the token/user/account. Commit a
            // revocation on an independent PostgreSQL connection before its CAS.
            if (identity == "account")
                Assert.Equal(1, await f.Db.AffiliateAccounts.IgnoreQueryFilters().Where(a => a.Id == account.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(a => a.IsDeleted, true)));
            else
                Assert.Equal(1, await f.Db.AffiliateUsers.IgnoreQueryFilters().Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(update => update.SetProperty(u => u.IsDeleted, true)));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<UnauthorizedObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        f.Db.ChangeTracker.Clear();
        Assert.False((await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync()).EmailVerified);
        Assert.Null((await f.Db.AffiliateEmailVerificationTokens.IgnoreQueryFilters().SingleAsync()).ConsumedAtUtc);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().Where(a => a.ActionType == "EMAIL_VERIFIED").ToListAsync());
    }

    private sealed class PauseVerificationIdentityClaim : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE \"AffiliateUsers\"", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("audit-failure")]
    [InlineData("expired")]
    [InlineData("deleted-account")]
    [InlineData("deleted-user")]
    [InlineData("deleted-token")]
    [InlineData("consumed")]
    [InlineData("unknown-token")]
    public async Task AffiliatePasswordResetCommitsCredentialSessionCutoffTokensAndAuditBeforeClearingCookies(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "APP2RESET", "Disposable password reset");
        account.Id = Guid.NewGuid();
        var user = AffiliateUser.Create(Guid.Empty, "reset@example.invalid", "fixture-old-password-hash", account.Id);
        user.Id = userId; user.SetRefreshToken("fixture-existing-refresh", DateTime.UtcNow.AddHours(1));
        var raw = AffiliateEmailVerificationToken.NewRawToken();
        var token = AffiliatePasswordResetToken.Create(userId, raw, DateTime.UtcNow.AddHours(state == "expired" ? -1 : 1));
        var other = AffiliatePasswordResetToken.Create(userId, AffiliateEmailVerificationToken.NewRawToken(), DateTime.UtcNow.AddHours(1));
        if (state == "deleted-account") account.IsDeleted = true;
        if (state == "deleted-user") user.IsDeleted = true;
        if (state == "deleted-token") token.IsDeleted = true;
        if (state == "consumed") token.Consume(DateTime.UtcNow.AddMinutes(-1));
        f.Db.AddRange(account, user, token, other); await f.Db.SaveChangesAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(new FailAuditSave(state == "audit-failure")).Options);
        var hasher = new PasswordHasher<AffiliateUser>();
        var controller = new AffiliateAuthController(db, null!, Options.Create(new AffiliateJwtSettings()), hasher,
            Mock.Of<IEmailSender>(MockBehavior.Strict), new ConfigurationBuilder().Build(), NullLogger<AffiliateAuthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Task<IActionResult> Reset() => controller.ResetPassword(new AffiliateResetPasswordDto {
            Token = state == "unknown-token" ? AffiliateEmailVerificationToken.NewRawToken() : raw,
            NewPassword = "FixtureOnly-Reset2026!" }, default);
        if (state == "audit-failure") await Assert.ThrowsAsync<InvalidOperationException>(Reset);
        else if (state == "valid")
        {
            Assert.IsType<OkObjectResult>(await Reset());
            Assert.IsType<UnauthorizedObjectResult>(await Reset());
        }
        else Assert.IsType<UnauthorizedObjectResult>(await Reset());
        f.Db.ChangeTracker.Clear();
        var saved = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        var savedTokens = await f.Db.AffiliatePasswordResetTokens.IgnoreQueryFilters().ToListAsync();
        var audits = await f.Db.AuditLogs.IgnoreQueryFilters().Where(a => a.ActionType == "PASSWORD_RESET_COMPLETED").ToListAsync();
        if (state == "valid")
        {
            Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(saved, saved.PasswordHash, "FixtureOnly-Reset2026!"));
            Assert.Null(saved.RefreshToken); Assert.NotNull(saved.TokensValidAfterUtc);
            Assert.All(savedTokens, row => Assert.NotNull(row.ConsumedAtUtc));
            Assert.Single(audits); Assert.True(controller.Response.Headers.SetCookie.Count > 0);
        }
        else
        {
            Assert.Equal("fixture-old-password-hash", saved.PasswordHash);
            Assert.Equal("fixture-existing-refresh", saved.RefreshToken); Assert.Null(saved.TokensValidAfterUtc);
            Assert.Equal(state == "consumed", savedTokens.Single(row => row.Id == token.Id).ConsumedAtUtc.HasValue);
            Assert.Null(savedTokens.Single(row => row.Id == other.Id).ConsumedAtUtc);
            Assert.Empty(audits); Assert.Equal(0, controller.Response.Headers.SetCookie.Count);
        }
    }

    [Theory]
    [InlineData("account")]
    [InlineData("user")]
    public async Task AffiliatePasswordResetCannotUseAnEligibilityReadAfterConcurrentIdentityDeletion(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "APP2RSTRACE", "Disposable reset race");
        account.Id = Guid.NewGuid();
        var user = AffiliateUser.Create(Guid.Empty, "reset-race@example.invalid", "fixture-old-password-hash", account.Id);
        user.Id = userId;
        var raw = AffiliateEmailVerificationToken.NewRawToken();
        var token = AffiliatePasswordResetToken.Create(userId, raw, DateTime.UtcNow.AddHours(1));
        f.Db.AddRange(account, user, token); await f.Db.SaveChangesAsync();
        var pause = new PauseVerificationIdentityClaim();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var controller = new AffiliateAuthController(db, null!, Options.Create(new AffiliateJwtSettings()),
            new PasswordHasher<AffiliateUser>(), Mock.Of<IEmailSender>(MockBehavior.Strict),
            new ConfigurationBuilder().Build(), NullLogger<AffiliateAuthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var pending = controller.ResetPassword(new AffiliateResetPasswordDto { Token = raw, NewPassword = "FixtureOnly-Reset2026!" }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            if (identity == "account")
                Assert.Equal(1, await f.Db.AffiliateAccounts.IgnoreQueryFilters().Where(a => a.Id == account.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(a => a.IsDeleted, true)));
            else
                Assert.Equal(1, await f.Db.AffiliateUsers.IgnoreQueryFilters().Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(update => update.SetProperty(u => u.IsDeleted, true)));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<UnauthorizedObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        f.Db.ChangeTracker.Clear();
        Assert.Equal("fixture-old-password-hash", (await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync()).PasswordHash);
        Assert.Null((await f.Db.AffiliatePasswordResetTokens.IgnoreQueryFilters().SingleAsync()).ConsumedAtUtc);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().Where(a => a.ActionType == "PASSWORD_RESET_COMPLETED").ToListAsync());
        Assert.Equal(0, controller.Response.Headers.SetCookie.Count);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("audit-failure")]
    [InlineData("expired")]
    [InlineData("deleted-account")]
    [InlineData("deleted-user")]
    [InlineData("deleted-token")]
    [InlineData("consumed-unverified")]
    [InlineData("unknown-token")]
    public async Task AffiliateVerificationUsesRealPostgresAndCommitsIdentityTokenAndAuditTogether(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "APP2VERIFY", "Disposable verification fixture");
        account.Id = Guid.NewGuid();
        var user = AffiliateUser.Create(Guid.Empty, "verify@example.invalid", "fixture-not-a-login", account.Id);
        user.Id = userId;
        var raw = AffiliateEmailVerificationToken.NewRawToken();
        var token = AffiliateEmailVerificationToken.Create(userId, raw,
            DateTime.UtcNow.AddHours(state == "expired" ? -1 : 1));
        if (state == "deleted-account") account.IsDeleted = true;
        if (state == "deleted-user") user.IsDeleted = true;
        if (state == "deleted-token") token.IsDeleted = true;
        if (state == "consumed-unverified") token.Consume(DateTime.UtcNow.AddMinutes(-1));
        f.Db.AddRange(account, user, token); await f.Db.SaveChangesAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(new FailAuditSave(state == "audit-failure")).Options);
        // Anonymous global affiliate authority: no tenant context, Mongo client,
        // email transport, application host or shared-primary fixture is used.
        var controller = new AffiliateAuthController(db, null!, Options.Create(new AffiliateJwtSettings()),
            new PasswordHasher<AffiliateUser>(), Mock.Of<IEmailSender>(MockBehavior.Strict),
            new ConfigurationBuilder().Build(), NullLogger<AffiliateAuthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Task<IActionResult> Verify() => controller.VerifyEmail(new AffiliateVerifyEmailDto {
            Token = state == "unknown-token" ? AffiliateEmailVerificationToken.NewRawToken() : raw }, default);
        if (state == "audit-failure") await Assert.ThrowsAsync<InvalidOperationException>(Verify);
        else if (state == "valid")
        {
            Assert.IsType<OkObjectResult>(await Verify());
            Assert.IsType<OkObjectResult>(await Verify()); // repeated click must not duplicate audit
        }
        else Assert.IsType<UnauthorizedObjectResult>(await Verify());
        f.Db.ChangeTracker.Clear();
        var savedUser = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        var savedToken = await f.Db.AffiliateEmailVerificationTokens.IgnoreQueryFilters().SingleAsync();
        var audits = await f.Db.AuditLogs.IgnoreQueryFilters().Where(a => a.ActionType == "EMAIL_VERIFIED").ToListAsync();
        Assert.Equal(state == "valid", savedUser.EmailVerified);
        if (state == "valid") { Assert.NotNull(savedToken.ConsumedAtUtc); Assert.Single(audits); }
        else
        {
            Assert.Empty(audits);
            Assert.Equal(state == "consumed-unverified", savedToken.ConsumedAtUtc.HasValue);
        }
        Assert.Null(savedUser.RefreshToken);
        Assert.Equal(0, controller.Response.Headers.SetCookie.Count);
    }

    [Fact]
    public async Task ConcurrentCashboxCloseHasOneCommittedWinnerAndOneAuditWithoutLedgerMutation()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "concurrent-close",
            Email = "close@example.invalid", PasswordHash = "fixture-not-a-login", IsActive = true, Role = UserRole.Admin };
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "concurrent fixture", IsActive = true };
        f.Db.AddRange(actor, box); await f.Db.SaveChangesAsync();
        var pause = new PauseCloseAudit();
        var tenant = new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId);
        await using var first = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options, tenant);
        await using var second = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).Options, tenant);
        CashboxController Controller(ApplicationDbContext db)
        {
            var controller = new CashboxController(db, tenant, NullLogger<CashboxController>.Instance,
                Mock.Of<ITreasuryOperationService>(), Mock.Of<ITreasuryReadService>(), Mock.Of<ICashboxLedgerAccountService>(), Mock.Of<IAuditService>());
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin") }, "fixture")) } };
            return controller;
        }
        var a = Controller(first).CloseCashbox(box.Id, new CloseCashboxRequest { ActualCash = 0m });
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await second.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)second.Database.GetDbConnection()).ProcessID;
        var b = Controller(second).CloseCashbox(box.Id, new CloseCashboxRequest { ActualCash = 0m });
        try
        {
            await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync();
            var watch = System.Diagnostics.Stopwatch.StartNew(); var waiting = false;
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event_type='Lock')", observer);
                command.Parameters.AddWithValue("pid", pid);
                if ((bool)(await command.ExecuteScalarAsync())!) { waiting = true; break; }
                if (b.IsCompleted) break;
                await Task.Delay(20);
            }
            Assert.True(waiting, "Second close must observably contend on the first close's PostgreSQL row lock.");
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await a.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.IsType<ConflictObjectResult>(await b.WaitAsync(TimeSpan.FromSeconds(20)));
        f.Db.ChangeTracker.Clear();
        Assert.False((await f.Db.Cashboxes.IgnoreQueryFilters().SingleAsync()).IsActive);
        Assert.Single(await f.Db.AuditLogs.IgnoreQueryFilters().Where(log => log.ActionType == "CashboxClose").ToListAsync());
        Assert.Empty(await f.Db.LedgerEntries.IgnoreQueryFilters().ToListAsync());
    }

    private sealed class PauseCloseAudit : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added && e.Entity.ActionType == "CashboxClose"))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task ShippingReadinessCoreUsesRealPostgresReadOnlyTransactionAndRollbackOnly()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        // Model-derived disposable schema, not an applied migration or production readiness claim.
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL)");
        const string fixtureVersion = "fixture-model-only";
        await f.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"__EFMigrationsHistory\" VALUES ({PointOfSale.API.Services.Shipping.ShippingWebhookClaimStartupValidator.RequiredMigration}, {fixtureVersion})");
        var commands = new ReadOnlyShapeProbe();
        var transactions = new RollbackOnlyProbe();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(commands, transactions).Options);
        await PointOfSale.API.Services.Shipping.ShippingWebhookClaimStartupValidator.ValidateDatabaseAsync(
            db, NullLogger.Instance, TimeSpan.FromSeconds(20));
        Assert.True(commands.ServerConfirmedReadOnly);
        Assert.Equal("SET TRANSACTION READ ONLY", Assert.Single(commands.NonQueries));
        Assert.Contains(commands.Reads, sql => sql.Contains("__EFMigrationsHistory", StringComparison.Ordinal));
        Assert.Contains(commands.Reads, sql => sql.Contains("ShippingWebhookClaims", StringComparison.Ordinal));
        Assert.Equal(1, transactions.Rollbacks);
        Assert.Equal(0, transactions.Commits);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
    }

    private sealed class ReadOnlyShapeProbe : DbCommandInterceptor
    {
        public List<string> NonQueries { get; } = [];
        public List<string> Reads { get; } = [];
        public bool ServerConfirmedReadOnly { get; private set; }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data,
            int result, CancellationToken cancellationToken = default)
        {
            NonQueries.Add(command.CommandText);
            Assert.NotNull(command.Transaction);
            await using var show = command.Connection!.CreateCommand();
            show.Transaction = command.Transaction;
            show.CommandText = "SHOW transaction_read_only";
            Assert.Equal("on", await show.ExecuteScalarAsync(cancellationToken));
            ServerConfirmedReadOnly = true;
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Assert.True(ServerConfirmedReadOnly);
            Assert.NotNull(command.Transaction);
            Reads.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RollbackOnlyProbe : DbTransactionInterceptor
    {
        public int Rollbacks { get; private set; }
        public int Commits { get; private set; }
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
            TransactionEventData data, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Rollbacks++;
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("inactive")]
    [InlineData("cashier")]
    [InlineData("foreign")]
    [InlineData("audit-failure")]
    public async Task CashboxCloseRequiresCurrentActorAndCommitsAuditAtomically(string state)
    {
        await using var f=await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor=new User { Id=Guid.NewGuid(), TenantId=f.VendorId, Username="app17e-actor", Email="cashbox@example.invalid",
            PasswordHash="fixture-not-a-login", IsActive=state!="inactive", Role=state=="cashier"?UserRole.Cashier:UserRole.Admin };
        var box=new Cashbox { Id=Guid.NewGuid(), TenantId=f.VendorId, Name="APP17E fixture", IsActive=true };
        f.Db.AddRange(actor,box); await f.Db.SaveChangesAsync();
        var interceptor=new FailAuditSave(state=="audit-failure");
        var tenant=state=="foreign"?Guid.NewGuid():f.VendorId;
        var tenantContext=new PointOfSale.Tests.Infrastructure.TestTenantContext(tenant);
        await using var db=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString)
            .AddInterceptors(interceptor).Options,tenantContext);
        var controller=new CashboxController(db,tenantContext,NullLogger<CashboxController>.Instance,
            Mock.Of<ITreasuryOperationService>(),Mock.Of<ITreasuryReadService>(),Mock.Of<ICashboxLedgerAccountService>(),Mock.Of<IAuditService>());
        controller.ControllerContext=new ControllerContext { HttpContext=new DefaultHttpContext { User=new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier,actor.Id.ToString()), new Claim(ClaimTypes.Role,"Admin") },"fixture")) } };
        var response=await controller.CloseCashbox(box.Id,new CloseCashboxRequest { ActualCash=0m });
        f.Db.ChangeTracker.Clear();
        var current=await f.Db.Cashboxes.IgnoreQueryFilters().SingleAsync();
        var audits=await f.Db.AuditLogs.IgnoreQueryFilters().Where(a=>a.ActionType=="CashboxClose").ToListAsync();
        if (state=="valid") { Assert.IsType<OkObjectResult>(response); Assert.False(current.IsActive); Assert.Single(audits); }
        else { Assert.True(current.IsActive); Assert.Empty(audits);
            if(state=="audit-failure") Assert.Equal(500,Assert.IsType<ObjectResult>(response).StatusCode);
            else Assert.IsType<ForbidResult>(response);
        }
        Assert.Empty(await f.Db.LedgerEntries.IgnoreQueryFilters().ToListAsync());
    }

    private sealed class FailAuditSave(bool enabled) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result,CancellationToken cancellationToken=default)
        {
            if(enabled && eventData.Context!.ChangeTracker.Entries<AuditLog>().Any(e=>e.State==EntityState.Added))
                throw new InvalidOperationException("APP17E controlled audit failure");
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryDispatchRejectsStaleOwnerAndHoldsTheFenceThroughAdmission(bool currentOwner)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var cart = new PointOfSale.Core.Entities.AbandonedCart { Id=Guid.NewGuid(), TenantId=f.VendorId, CartId=Guid.NewGuid(),
            PhoneNumber="+201000000000", CartSnapshotJson="{}", RecoveryStatus=AbandonedCartRecoveryStatus.Abandoned,
            LastActivityAtUtc=DateTime.UtcNow.AddHours(-1) };
        var schedule = new AbandonedCartRecoveryLog { Id=Guid.NewGuid(), TenantId=f.VendorId, AbandonedCartId=cart.Id,
            CartId=cart.CartId, PhoneNumber=cart.PhoneNumber, FirstMessageStatus=AbandonedCartMessageStatus.Dispatching,
            FirstMessageLeaseId="current", FirstMessageLeaseExpiresAtUtc=DateTime.UtcNow.AddMinutes(2),
            FirstMessageDueAtUtc=DateTime.UtcNow.AddMinutes(-1), IdempotencyKey="app17e-fence", TimeZoneId="UTC" };
        f.Db.AddRange(cart,schedule); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var admitted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eligibility=new Mock<IAbandonedCartEligibilityService>(MockBehavior.Strict);
        if (currentOwner)
            eligibility.Setup(s=>s.GetEligibilityStatusAsync(f.VendorId,It.IsAny<CancellationToken>())).Returns(async()=> {
                admitted.SetResult(); await release.Task;
                return new AbandonedCartEligibilityStatus { IsEligible=false, AutomationEnabled=false, IneligibilityReason="fixture-disabled" };
            });
        var dispatcher=new Mock<IWhatsAppMessageDispatcher>(MockBehavior.Strict);
        var scheduler=new NativeAbandonedCartRecoveryScheduler(f.Db, Mock.Of<ITenantAutomationSettingsService>(),
            eligibility.Object, Mock.Of<IAbandonedCartService>(), Mock.Of<IWhatsAppAccountService>(),
            Mock.Of<IWhatsAppMessageLogService>(), dispatcher.Object, Mock.Of<IWhatsAppRateLimitService>(),
            TimeProvider.System, NullLogger<NativeAbandonedCartRecoveryScheduler>.Instance);
        var method=typeof(NativeAbandonedCartRecoveryScheduler).GetMethod("DispatchClaimedAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var dispatch=(Task)method.Invoke(scheduler,new object[] { schedule.Id,1,currentOwner?"current":"stale",CancellationToken.None })!;
        if (currentOwner)
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            try
            {
                await using var other=new NpgsqlConnection(f.ConnectionString); await other.OpenAsync();
                await using var tx=await other.BeginTransactionAsync();
                await using var command=new NpgsqlCommand("SELECT \"Id\" FROM \"AbandonedCartRecoveryLogs\" WHERE \"Id\"=@id FOR UPDATE NOWAIT",other,tx);
                command.Parameters.AddWithValue("id",schedule.Id);
                var error=await Assert.ThrowsAsync<PostgresException>(()=>command.ExecuteScalarAsync());
                Assert.Equal(PostgresErrorCodes.LockNotAvailable,error.SqlState);
            }
            finally { release.TrySetResult(); }
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(20));
        dispatcher.Verify(s=>s.DispatchAsync(It.IsAny<string>(),It.IsAny<CancellationToken>()),Times.Never);
        eligibility.Verify(s=>s.GetEligibilityStatusAsync(It.IsAny<Guid>(),It.IsAny<CancellationToken>()),currentOwner?Times.Once():Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentClaimsRequireOrderTransactionAndPreserveRollback(bool useTransaction)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var field = Field(f.VendorId);
        var attachment = Attachment(f.VendorId, field.Id);
        var order = new Order { Id = Guid.NewGuid(), TenantId = f.VendorId, OrderNumber = "APP17E-attachment", CartId = attachment.CartId };
        f.Db.AddRange(field, attachment, order); await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var service = Service(f.Db);
        if (!useTransaction)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateAndAttachAsync(f.VendorId,
                "owner", "session", " owner ", null, attachment.CartId, order.Id, field, new[] { attachment.Id }));
        }
        else
        {
            await using var tx = await f.Db.Database.BeginTransactionAsync();
            var attached = await service.ValidateAndAttachAsync(f.VendorId, "owner", "session", " owner ", null,
                attachment.CartId, order.Id, field, new[] { attachment.Id });
            Assert.Equal(order.Id, Assert.Single(attached).OrderId);
            // The post-bulk metadata flush must use the refreshed PostgreSQL xmin.
            await f.Db.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        f.Db.ChangeTracker.Clear();
        var current = await f.Db.CheckoutAttachments.IgnoreQueryFilters().SingleAsync();
        Assert.Null(current.OrderId); Assert.Equal(CheckoutAttachmentStatus.Finalized, current.Status);
    }

    [Fact]
    public async Task CleanupCannotUseAnAdmissionSnapshotAfterPersistedOwnerChanges()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var field = Field(f.VendorId); var attachment = Attachment(f.VendorId, field.Id);
        attachment.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        f.Db.AddRange(field, attachment); await f.Db.SaveChangesAsync();
        var interceptor = new ChangeOwnerBeforeMutation(f.ConnectionString, attachment.Id);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(interceptor).Options);
        var storage = new Mock<IR2StorageService>(MockBehavior.Strict);
        await new CheckoutAttachmentCleanupJob(db, storage.Object, NullLogger<CheckoutAttachmentCleanupJob>.Instance).RunAsync();
        Assert.True(interceptor.Executed);
        storage.Verify(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        f.Db.ChangeTracker.Clear();
        var current = await f.Db.CheckoutAttachments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("new-owner", current.OwnerId); Assert.False(current.IsDeleted);
        Assert.Equal(CheckoutAttachmentStatus.Finalized, current.Status);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-user")]
    [InlineData("wrong-session")]
    [InlineData("revoked")]
    [InlineData("disabled-policy")]
    [InlineData("foreign-tenant")]
    [InlineData("suspended")]
    public async Task LandingDesignUpsertRechecksCurrentSqlAuthority(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var seed = await SeedLandingAuthority(f);
        var account = seed.Account; var user = seed.User; var product = seed.Product; var policy = seed.Policy;
        var issued = DateTimeOffset.UtcNow.AddMinutes(-1);
        if (state == "revoked") user.RevokeSessions(DateTime.UtcNow);
        if (state == "disabled-policy") policy.Disable();
        if (state == "suspended") account.Suspend("fixture");
        await f.Db.SaveChangesAsync();
        var actor = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier, (state == "wrong-user" ? Guid.NewGuid() : user.Id).ToString()),
            new Claim("affiliate_account_id", account.Id.ToString()), new Claim("iat", issued.ToUnixTimeSeconds().ToString()),
            new Claim("affiliate_session", state == "wrong-session" ? "OTHER" : "ABCDEF")
        }, "AffiliateJwt"));
        Task Save() => AffiliateLandingPageDesignStore.SaveAsync(f.Db, account.Id,
            state == "foreign-tenant" ? Guid.NewGuid() : f.VendorId, product.Id, "Classic:v1", actor, default);
        if (state != "valid")
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(Save);
            Assert.Empty(await f.Db.AffiliateLandingPageDesigns.IgnoreQueryFilters().ToListAsync());
        }
        else
        {
            await Save(); await Save(); f.Db.ChangeTracker.Clear();
            var saved = await f.Db.AffiliateLandingPageDesigns.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(account.Id, saved.AffiliateAccountId); Assert.Equal(f.VendorId, saved.TenantId);
            Assert.Equal(product.Id, saved.ProductId); Assert.Equal("Classic:v1", saved.TemplateType);
        }
    }

    [Fact]
    public async Task SimultaneousAuthorizedFirstDesignSavesConvergeToOneRow()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var seed = await SeedLandingAuthority(f); var actor = LandingActor(seed);
        await using var first = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await using var second = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await Task.WhenAll(new[]{first,second}.Select(db=>AffiliateLandingPageDesignStore.SaveAsync(db,
            seed.Account.Id,f.VendorId,seed.Product.Id,"Classic:v1",actor,default)));
        var saved = await f.Db.AffiliateLandingPageDesigns.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(seed.Account.Id,saved.AffiliateAccountId);Assert.Equal(seed.Product.Id,saved.ProductId);
        Assert.Equal(f.VendorId,saved.TenantId);Assert.Equal("Classic:v1",saved.TemplateType);
    }

    [Fact]
    public async Task DesignAuthorityLocksRemainHeldUntilCallerRollbackAndRevocationWinsNextSave()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var seed = await SeedLandingAuthority(f); var actor = LandingActor(seed);
        await using var owner = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await using var transaction = await owner.Database.BeginTransactionAsync();
        await AffiliateLandingPageDesignStore.SaveAsync(owner,seed.Account.Id,f.VendorId,seed.Product.Id,"Classic:v1",actor,default);
        await using var revoker = new NpgsqlConnection(f.ConnectionString);await revoker.OpenAsync();
        await using var revoke = new NpgsqlCommand("UPDATE \"AffiliateProductPolicies\" SET \"IsEnabled\"=false WHERE \"Id\"=@id",revoker);
        revoke.Parameters.AddWithValue("id",seed.Policy.Id);
        var pending = revoke.ExecuteNonQueryAsync();
        try
        {
            await using var observer = new NpgsqlConnection(f.ConnectionString);await observer.OpenAsync();
            var watch = System.Diagnostics.Stopwatch.StartNew();var blocked = false;
            while(watch.Elapsed<TimeSpan.FromSeconds(10))
            {
                await using var probe = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event_type='Lock')",observer);
                probe.Parameters.AddWithValue("pid",revoker.ProcessID);
                if((bool)(await probe.ExecuteScalarAsync())!){blocked=true;break;}
                await Task.Delay(20);
            }
            Assert.True(blocked,"Expected persisted policy revocation to wait on the command's authority row lock.");
            Assert.False(pending.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); await transaction.DisposeAsync(); }
        Assert.Equal(1,await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(await f.Db.AffiliateLandingPageDesigns.IgnoreQueryFilters().ToListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>AffiliateLandingPageDesignStore.SaveAsync(owner,
            seed.Account.Id,f.VendorId,seed.Product.Id,"Classic:v1",actor,default));
    }

    internal static async Task<LandingAuthority> SeedLandingAuthority(ProductionCoordinationAuthorityTests.Fixture f)
    {
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "APP17EAA", "fixture"); account.Activate();
        account.Id = Guid.NewGuid();
        var user = AffiliateUser.Create(Guid.Empty, "fixture@example.invalid", "no-real-credential", account.Id);
        user.Id = userId; user.VerifyEmail(); user.SetRefreshToken("ABCDEF", DateTime.UtcNow.AddHours(1));
        var category = AffiliateCategory.CreateMainCategory("Fixture", "app17e-fixture");
        category.Id = Guid.NewGuid();
        // Merchant product taxonomy and the global affiliate taxonomy are distinct
        // authorities. Populate the product's required merchant Category FK explicitly.
        var merchantCategory = new Category { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Fixture merchant category" };
        var product = new Product { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Fixture", SKU = "APP17E", IsActive = true,
            CategoryId = merchantCategory.Id, IsOnlineVisible = true, IsSearchable = true, PublishStatus = PublishStatus.Published };
        var policy = AffiliateProductPolicy.Create(f.VendorId, product.Id, .1m, categoryId: category.Id);
        f.Db.AddRange(account, user, category, merchantCategory, product, policy); await f.Db.SaveChangesAsync();
        return new(account,user,product,policy);
    }

    internal sealed record LandingAuthority(AffiliateAccount Account,AffiliateUser User,Product Product,AffiliateProductPolicy Policy);
    internal static ClaimsPrincipal LandingActor(LandingAuthority seed) => new(new ClaimsIdentity(new[] {
        new Claim(ClaimTypes.NameIdentifier,seed.User.Id.ToString()),new Claim("affiliate_account_id",seed.Account.Id.ToString()),
        new Claim("iat",DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds().ToString()),new Claim("affiliate_session","ABCDEF")
    },"AffiliateJwt"));

    private static CheckoutAttachmentService Service(ApplicationDbContext db) => new(db,
        Mock.Of<IR2StorageService>(), Mock.Of<ICartService>(), new FileValidationService(), NullLogger<CheckoutAttachmentService>.Instance);
    private static CheckoutFieldDefinition Field(Guid tenant) => new() { Id=Guid.NewGuid(), TenantId=tenant,
        FieldKind=CheckoutFieldKind.Custom, FieldType=CheckoutFieldType.Image, Label="fixture", IsEnabled=true,
        ConfigurationSchemaVersion=1, ConfigurationJson="{\"schemaVersion\":1,\"allowedMimeTypes\":[\"image/jpeg\"],\"maxFileSize\":5242880,\"maxFiles\":2}" };
    private static CheckoutAttachment Attachment(Guid tenant, Guid field)
    {
        var cart = Guid.NewGuid(); var id = Guid.NewGuid();
        return new() { Id=id, TenantId=tenant, CartId=cart, CheckoutFieldDefinitionId=field, OwnerId="owner", OwnerType="session", SessionId="owner",
            ObjectKey=$"tenants/{tenant:N}/checkout/{cart:N}/fields/{field:N}/attachments/{id:N}.jpg",
            FinalObjectKey=$"tenants/{tenant:N}/checkout-final/{cart:N}/fields/{field:N}/attachments/{id:N}.jpg",
            Status=CheckoutAttachmentStatus.Finalized, ContentType="image/jpeg", MediaType="image", OriginalFileName="fixture.jpg",
            ExpectedSizeBytes=1024, SizeBytes=1024, ExpiresAtUtc=DateTime.UtcNow.AddHours(1) };
    }
    private sealed class ChangeOwnerBeforeMutation(string connection, Guid id) : DbCommandInterceptor
    {
        public bool Executed { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken=default)
        {
            if (!Executed && command.CommandText.StartsWith("UPDATE \"CheckoutAttachments\"", StringComparison.Ordinal))
            {
                Executed=true;
                await using var other=new NpgsqlConnection(connection); await other.OpenAsync(cancellationToken);
                await using var mutation=new NpgsqlCommand("UPDATE \"CheckoutAttachments\" SET \"OwnerId\"='new-owner' WHERE \"Id\"=@id",other);
                mutation.Parameters.AddWithValue("id",id); Assert.Equal(1,await mutation.ExecuteNonQueryAsync(cancellationToken));
            }
            return result;
        }
    }
}
