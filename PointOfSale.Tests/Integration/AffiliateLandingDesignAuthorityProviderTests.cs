using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Funnel;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Tests.Integration;

// Real presentation-only conditional upsert, isolated PostgreSQL18. No product
// publication worker, external provider, mail or financial operation is invoked.
public sealed class AffiliateLandingDesignAuthorityProviderTests
{
    private static async Task<(SqlGovernanceClosureProviderTests.LandingAuthority Seed, AffiliateCategory Parent, AffiliateCategory Child)> SeedAsync(
        ProductionCoordinationAuthorityTests.Fixture f)
    {
        var seed = await SqlGovernanceClosureProviderTests.SeedLandingAuthority(f);
        var parent = await f.Db.AffiliateCategories.SingleAsync();
        var child = AffiliateCategory.CreateSubcategory("Disposable child", "disposable-child", parent.Id);
        f.Db.Add(child); seed.Policy.UpdateCategory(child.Id); await f.Db.SaveChangesAsync();
        return (seed, parent, child);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("account")]
    [InlineData("product")]
    [InlineData("policy")]
    [InlineData("tenant")]
    [InlineData("category")]
    [InlineData("parent")]
    public async Task DesignUpsertHoldsEachExactAuthorityRowUntilCallerRollback(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (seed, parent, child) = await SeedAsync(f);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AffiliateLandingPageDesignStore.SaveAsync(db, seed.Account.Id, f.VendorId, seed.Product.Id,
            "Classic:v1", SqlGovernanceClosureProviderTests.LandingActor(seed), default);
        var (table, id) = identity switch
        {
            "user" => ("AffiliateUsers", seed.User.Id), "account" => ("AffiliateAccounts", seed.Account.Id),
            "product" => ("Products", seed.Product.Id), "policy" => ("AffiliateProductPolicies", seed.Policy.Id),
            "tenant" => ("Tenants", f.VendorId), "category" => ("AffiliateCategories", child.Id),
            _ => ("AffiliateCategories", parent.Id)
        };
        await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync();
        await using var probeTransaction = await observer.BeginTransactionAsync();
        // Table is selected only from the fixed local switch above, never input.
        await using var probe = new NpgsqlCommand($"SELECT \"Id\" FROM \"{table}\" WHERE \"Id\"=@id FOR UPDATE NOWAIT", observer, probeTransaction);
        probe.Parameters.AddWithValue("id", id); string? observed = null;
        try { await probe.ExecuteScalarAsync(); }
        catch (PostgresException error) { observed = error.SqlState; }
        await probeTransaction.RollbackAsync(); await transaction.RollbackAsync();
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, observed);
        Assert.Empty(await f.Db.AffiliateLandingPageDesigns.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("account")]
    [InlineData("product")]
    [InlineData("policy")]
    [InlineData("tenant")]
    [InlineData("category")]
    [InlineData("parent")]
    public async Task DesignWriteRejectsEachRevocationCommittedAfterTheCallerAdmissionSnapshot(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (seed, parent, child) = await SeedAsync(f);
        var actor = SqlGovernanceClosureProviderTests.LandingActor(seed); var pause = new PauseDesignWrite();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var pending = AffiliateLandingPageDesignStore.SaveAsync(db, seed.Account.Id, f.VendorId, seed.Product.Id, "Classic:v1", actor, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            switch (identity)
            {
                case "user": seed.User.RevokeSessions(DateTime.UtcNow); break;
                case "account": seed.Account.Suspend("Disposable revocation"); break;
                case "product": seed.Product.IsOnlineVisible = false; break;
                case "policy": seed.Policy.Disable(); break;
                case "tenant": (await f.Db.Tenants.IgnoreQueryFilters().SingleAsync()).IsActive = false; break;
                case "category": child.Deactivate(); break;
                default: parent.Deactivate(); break;
            }
            await f.Db.SaveChangesAsync();
        }
        finally { pause.Release.TrySetResult(); }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Empty(await f.Db.AffiliateLandingPageDesigns.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScopedDesignReadsObserveOnlyCommittedStateAndPreserveOuterRollback(bool commit)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (seed, _, _) = await SeedAsync(f);
        await using var writer = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await using var reader = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await AffiliateLandingPageDesignStore.SaveAsync(writer, seed.Account.Id, f.VendorId, seed.Product.Id,
            "Classic:v1", SqlGovernanceClosureProviderTests.LandingActor(seed), default);
        Assert.Equal("Classic:v1", await AffiliateLandingPageDesignStore.ReadAsync(writer, seed.Account.Id, f.VendorId, seed.Product.Id, default));
        Assert.Null(await AffiliateLandingPageDesignStore.ReadAsync(reader, seed.Account.Id, f.VendorId, seed.Product.Id, default));
        if (commit) await transaction.CommitAsync(); else await transaction.RollbackAsync();
        Assert.Equal(commit ? "Classic:v1" : null, await AffiliateLandingPageDesignStore.ReadAsync(reader, seed.Account.Id, f.VendorId, seed.Product.Id, default));
        Assert.Null(await AffiliateLandingPageDesignStore.ReadAsync(reader, Guid.NewGuid(), f.VendorId, seed.Product.Id, default));
        Assert.Null(await AffiliateLandingPageDesignStore.ReadAsync(reader, seed.Account.Id, Guid.NewGuid(), seed.Product.Id, default));
        Assert.Null(await AffiliateLandingPageDesignStore.ReadAsync(reader, seed.Account.Id, f.VendorId, Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("wrong-account")]
    [InlineData("missing-user")]
    [InlineData("bad-iat")]
    [InlineData("negative-iat")]
    [InlineData("out-of-range-iat")]
    [InlineData("missing-session")]
    [InlineData("bad-template")]
    public async Task MalformedPresentationCapabilitiesCannotEnterTheDesignWrite(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (seed, _, _) = await SeedAsync(f);
        var claims = SqlGovernanceClosureProviderTests.LandingActor(seed).Claims.ToList();
        if (state == "missing-user") claims.RemoveAll(c => c.Type == ClaimTypes.NameIdentifier);
        if (state == "missing-session") claims.RemoveAll(c => c.Type == "affiliate_session");
        if (state == "wrong-account")
        {
            claims.RemoveAll(c => c.Type == "affiliate_account_id"); claims.Add(new Claim("affiliate_account_id", Guid.NewGuid().ToString()));
        }
        if (state is "bad-iat" or "negative-iat" or "out-of-range-iat")
        {
            claims.RemoveAll(c => c.Type == "iat");
            claims.Add(new Claim("iat", state == "bad-iat" ? "not-a-time" : state == "negative-iat" ? "-1" : "253402300800"));
        }
        var actor = new ClaimsPrincipal(new ClaimsIdentity(claims, state == "anonymous" ? null : "AffiliateJwt"));
        Task Save() => AffiliateLandingPageDesignStore.SaveAsync(f.Db, seed.Account.Id, f.VendorId, seed.Product.Id,
            state == "bad-template" ? "unsupported-fixture-template" : "Classic:v1", actor, default);
        if (state == "bad-template") await Assert.ThrowsAsync<ArgumentException>(Save);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(Save);
        Assert.Empty(await f.Db.AffiliateLandingPageDesigns.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task DesignAndAccountRevocationUseCompatibleUserBeforeAccountLocksWithoutDeadlock()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (seed, _, _) = await SeedAsync(f); var pause = new PauseLifecycleUserLock();
        await using var revoker = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var revocation = AffiliateAccountSessionAuthorityProviderTests.Service(revoker)
            .SuspendAffiliateAsync(seed.Account.Id, "Disposable lock-order revocation");
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await using var designer = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await designer.Database.OpenConnectionAsync();
        var design = AffiliateLandingPageDesignStore.SaveAsync(designer, seed.Account.Id, f.VendorId, seed.Product.Id,
            "Classic:v1", SqlGovernanceClosureProviderTests.LandingActor(seed), default);
        var accountHeldWhileWaitingForUser = false;
        try
        {
            await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync();
            var watch = System.Diagnostics.Stopwatch.StartNew(); var blocked = false;
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await using var inspect = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event_type='Lock')", observer);
                inspect.Parameters.AddWithValue("pid", ((NpgsqlConnection)designer.Database.GetDbConnection()).ProcessID);
                if ((bool)(await inspect.ExecuteScalarAsync())!) { blocked = true; break; }
                if (design.IsCompleted) break;
                await Task.Delay(20);
            }
            Assert.True(blocked, "The actual design upsert must observably wait for revocation's user lock.");
            await using var tx = await observer.BeginTransactionAsync();
            await using var probe = new NpgsqlCommand("SELECT \"Id\" FROM \"AffiliateAccounts\" WHERE \"Id\"=@id FOR UPDATE NOWAIT", observer, tx);
            probe.Parameters.AddWithValue("id", seed.Account.Id);
            try { await probe.ExecuteScalarAsync(); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
            { accountHeldWhileWaitingForUser = true; }
            await tx.RollbackAsync();
        }
        finally { pause.Release.TrySetResult(); }
        // Observe BOTH real tasks to completion, retaining PostgreSQL errors; a
        // deadlock victim is not converted into an expected authority refusal.
        static async Task<Exception?> Observe(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(20)); return null; }
            catch (Exception error) { return error; }
        }
        var outcomes = await Task.WhenAll(Observe(revocation), Observe(design));
        Assert.True(outcomes[0] is null, outcomes[0]?.ToString());
        Assert.True(outcomes[1] is UnauthorizedAccessException, outcomes[1]?.ToString() ?? "Design unexpectedly succeeded after revocation.");
        Assert.False(accountHeldWhileWaitingForUser, "Design may not hold account authority while waiting for the earlier user gate.");
        Assert.Empty(await f.Db.AffiliateLandingPageDesigns.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("suspended")]
    [InlineData("closed")]
    [InlineData("deleted")]
    public async Task PublicLandingCannotTreatAnEarlierMiddlewareAccountSnapshotAsCurrentAuthority(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (seed, _, _) = await SeedAsync(f); const string slug = "disposable-landing-product";
        seed.Product.Slug = slug; await f.Db.SaveChangesAsync();
        var http = new DefaultHttpContext();
        // Request-local output of the earlier middleware admission. This is not
        // an authority mock: the account is then really revoked in PostgreSQL.
        http.Items["AffiliateMode"] = true; http.Items["AffiliateAccountId"] = seed.Account.Id;
        http.Items["AffiliateReferralCode"] = seed.Account.ReferralCode;
        http.Items["AffiliateDisplayName"] = seed.Account.DisplayName;
        if (state == "suspended") seed.Account.Suspend("Disposable public-reader revocation");
        if (state == "closed") seed.Account.Close("Disposable public-reader closure");
        if (state == "deleted") seed.Account.IsDeleted = true;
        await f.Db.SaveChangesAsync();
        // Only a stale read-model CANDIDATE is supplied by this no-network
        // adapter. The production SQL overlay, policy/category/account queries
        // and public controller all execute against actual disposable PostgreSQL.
        var repository = new Mock<IProductReadRepository>(MockBehavior.Strict);
        repository.Setup(r => r.FindBySlugAsync(f.VendorId, slug, true, default))
            .ReturnsAsync(new ProductDocument { ProductId = seed.Product.Id, TenantId = f.VendorId, Slug = slug });
        var tenant = new TenantContext(); tenant.SetTenantId(f.VendorId);
        await using var db = f.ScopedDb(f.VendorId);
        var controller = new StorefrontLandingPageController(repository.Object, tenant, db, NullLogger<StorefrontLandingPageController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = await controller.GetLandingPage(seed.Account.ReferralCode, slug, null, default);
        if (state == "valid") Assert.IsType<OkObjectResult>(result);
        else Assert.IsType<NotFoundObjectResult>(result);
        Assert.Empty(await f.Db.AffiliateLandingPageDesigns.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task ValidPublicLandingPreservesCurrentAffiliateIdentityAndCtaAttribution()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (seed, _, _) = await SeedAsync(f);
        const string slug = "disposable-attribution-product";
        seed.Product.Slug = slug; await f.Db.SaveChangesAsync();
        await using var db = f.ScopedDb(f.VendorId);
        var controller = PublicController(db, f.VendorId, seed, slug);
        var response = Assert.IsType<OkObjectResult>(await controller.GetLandingPage(seed.Account.ReferralCode, slug, null, default));
        var landing = Assert.IsType<LandingPageDto>(response.Value);
        Assert.True(landing.Affiliate.IsValid);
        Assert.Equal(seed.Account.ReferralCode, landing.Affiliate.ReferralCode);
        Assert.Equal(seed.Account.DisplayName, landing.Affiliate.DisplayName);
        Assert.True(landing.CTAConfig.IncludeAffiliateAttribution);
    }

    [Theory]
    [InlineData("product-inactive")]
    [InlineData("product-hidden")]
    [InlineData("product-unpublished")]
    [InlineData("product-deleted")]
    [InlineData("policy-disabled")]
    [InlineData("policy-deleted")]
    [InlineData("category")]
    [InlineData("parent")]
    [InlineData("tenant-inactive")]
    [InlineData("tenant-deleted")]
    public async Task PublicLandingRejectsAuthorityRevokedAfterProductAndPolicyReads(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (seed, parent, child) = await SeedAsync(f);
        const string slug = "disposable-reader-revocation";
        seed.Product.Slug = slug; await f.Db.SaveChangesAsync();
        var pause = new PausePublicTemplateRead();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options,
            new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        var controller = PublicController(db, f.VendorId, seed, slug);
        var pending = controller.GetLandingPage(seed.Account.ReferralCode, slug, null, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            switch (identity)
            {
                case "product-inactive": seed.Product.IsActive = false; break;
                case "product-hidden": seed.Product.IsOnlineVisible = false; break;
                case "product-unpublished": seed.Product.PublishStatus = PublishStatus.Draft; break;
                case "product-deleted": seed.Product.IsDeleted = true; break;
                case "policy-disabled": seed.Policy.Disable(); break;
                case "policy-deleted": seed.Policy.IsDeleted = true; break;
                case "category": child.Deactivate(); break;
                case "parent": parent.Deactivate(); break;
                case "tenant-inactive": (await f.Db.Tenants.IgnoreQueryFilters().SingleAsync()).IsActive = false; break;
                default: (await f.Db.Tenants.IgnoreQueryFilters().SingleAsync()).DeletedAtUtc = DateTime.UtcNow; break;
            }
            await f.Db.SaveChangesAsync();
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<NotFoundObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Empty(await f.Db.AffiliateLandingPageDesigns.IgnoreQueryFilters().ToListAsync());
    }

    private static StorefrontLandingPageController PublicController(ApplicationDbContext db, Guid tenantId,
        SqlGovernanceClosureProviderTests.LandingAuthority seed, string slug)
    {
        var repository = new Mock<IProductReadRepository>(MockBehavior.Strict);
        repository.Setup(r => r.FindBySlugAsync(tenantId, slug, true, default))
            .ReturnsAsync(new ProductDocument { ProductId = seed.Product.Id, TenantId = tenantId, Slug = slug });
        var tenant = new TenantContext(); tenant.SetTenantId(tenantId);
        var http = new DefaultHttpContext();
        http.Items["AffiliateMode"] = true; http.Items["AffiliateAccountId"] = seed.Account.Id;
        http.Items["AffiliateReferralCode"] = "STALE-CANDIDATE"; http.Items["AffiliateDisplayName"] = "Stale candidate";
        return new StorefrontLandingPageController(repository.Object, tenant, db, NullLogger<StorefrontLandingPageController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed class PausePublicTemplateRead : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"AffiliateLandingPageDesigns\"", StringComparison.Ordinal))
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class PauseLifecycleUserLock : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT 1 FROM \"AffiliateUsers\"", StringComparison.Ordinal))
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class PauseDesignWrite : DbCommandInterceptor
    {
        private bool _paused;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            // Pause before the FIRST authority lock/statement. Pausing only the
            // later upsert would hold new user/account locks while this test's
            // independent revocation waits to commit, manufacturing a timeout.
            if (!_paused && (command.CommandText.Contains("WITH admitted AS", StringComparison.Ordinal) ||
                command.CommandText.Contains("SELECT 1 FROM \"AffiliateUsers\"", StringComparison.Ordinal)))
            {
                _paused = true;
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}
