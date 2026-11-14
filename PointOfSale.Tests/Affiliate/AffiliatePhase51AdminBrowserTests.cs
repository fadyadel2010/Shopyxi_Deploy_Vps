using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using PointOfSale.API.Configuration;
using PointOfSale.API.Controllers;
using PointOfSale.API.Models;
using PointOfSale.API.Security;
using PointOfSale.API.Services.CommerceHub.Finance;
using PointOfSale.API.Services.Governance;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Treasury;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Opt-in, disposable SQL, actual POS auth/admin MVC and unmodified Vite UI.
/// No production Program, provider, posting service, bus, Mongo or background workers.</summary>
public sealed class AffiliatePhase51AdminBrowserTests
{
    [P51BrowserFact]
    public async Task IsolatedAdminBrowserRuntime()
    {
        await using var f = new AffiliatePhase5WithdrawalTests.Fixture(); await f.Initialize();
        var earned = f.Commission(500); f.Commission(900, true); await f.Db.SaveChangesAsync();
        var request = await f.Service.RequestPayoutAsync(f.Account.Id, 150, f.Store);
        var user = await f.Db.AffiliateUsers.SingleAsync();
        var affiliateHasher = new PasswordHasher<AffiliateUser>();
        user.SetPasswordHash(affiliateHasher.HashPassword(user, "P51DisposableOnly!2026"));
        var admin = new User { Id = f.Admin, TenantId = f.Store, Username = "p51-admin", Email = $"p51-admin-{f.Admin:N}@example.invalid",
            FirstName = "P51", LastName = "NO MONEY", Role = UserRole.Admin, IsActive = true };
        var posHasher = new PasswordHasher<User>(); admin.PasswordHash = posHasher.HashPassword(admin, "P51DisposableOnly!2026");
        f.Db.Add(admin); await f.Db.SaveChangesAsync();
        var evidence = Path.Combine(TestConfiguration.GetSolutionRoot(), ".ai", "evidence", "affiliate-phase51-20261022");
        var done = Path.Combine(evidence, "browser.done"); Assert.False(File.Exists(done));
        var jwt = new JwtSettings { Secret = "P51-TEST-ONLY-NO-MONEY-LOOPBACK-20261022-KEY", Issuer = "P51NoMoney", Audience = "P51UI", AccessTokenExpirationMinutes = 60, RefreshTokenExpirationDays = 1 };
        var affiliateJwt = new AffiliateJwtSettings { Secret = jwt.Secret, Issuer = jwt.Issuer, Audience = jwt.Audience };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = typeof(AffiliatePhase51AdminBrowserTests).Assembly.FullName });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["SyntheticQa:Enabled"] = "false" });
        builder.WebHost.UseUrls("http://127.0.0.1:5196"); builder.Logging.ClearProviders();
        var tenant = new TenantContext(); tenant.SetTenantId(f.Store);
        builder.Services.AddScoped(_ => f.Scope.CreateDbContext());
        builder.Services.AddScoped<IAffiliatePayoutService>(sp => AffiliatePhase5WithdrawalTests.Fixture.ServiceFor(sp.GetRequiredService<ApplicationDbContext>()));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)), ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience, ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true };
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = c =>
                {
                    if (string.IsNullOrEmpty(c.Request.Headers.Authorization))
                        c.Token = c.Request.Path.StartsWithSegments("/api/affiliate")
                            ? c.Request.Cookies[AuthCookieConfiguration.AffiliateAccessTokenCookieName]
                            : c.Request.Cookies[AuthCookieConfiguration.PosAccessTokenCookieName];
                    return Task.CompletedTask;
                },
                OnTokenValidated = async c =>
                {
                    if (c.Principal!.IsInRole("Admin"))
                    {
                        // AuthController emits the production session-binding claim. Require the
                        // exact current SQL binding, not a fabricated browser auth payload.
                        var binding = c.Principal.FindFirstValue(PosSecurityConstants.SessionBindingClaim);
                        var db = c.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
                        if (!await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == f.Admin && u.IsActive && u.RefreshToken == binding)) c.Fail("Stale test session");
                    }
                }
            };
        });
        builder.Services.AddAuthorization(o => o.AddPolicy("AffiliateOnly", p => p.RequireAuthenticatedUser().RequireRole("Affiliate")));
        builder.Services.AddRateLimiter(o =>
        {
            foreach (var policy in new[] { "AffiliateAuthRateLimit", "PublicLoginRateLimit", "PublicRefreshRateLimit" })
                o.AddFixedWindowLimiter(policy, l => { l.PermitLimit = 1000; l.Window = TimeSpan.FromMinutes(1); l.QueueLimit = 0; l.QueueProcessingOrder = QueueProcessingOrder.OldestFirst; });
        });
        builder.Services.AddControllers(o => o.Conventions.Add(new OnlyP51Actions())).AddApplicationPart(typeof(AdminAffiliateController).Assembly).AddControllersAsServices();
        foreach (var descriptor in builder.Services.Where(d => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(d.ServiceType)).ToArray()) builder.Services.Remove(descriptor);
        builder.Services.AddScoped(sp => new AffiliatePortalController(sp.GetRequiredService<ApplicationDbContext>(), null!, null!, sp.GetRequiredService<IAffiliatePayoutService>(), null!, null!, NullLogger<AffiliatePortalController>.Instance));
        builder.Services.AddScoped(sp => new AffiliateAuthController(sp.GetRequiredService<ApplicationDbContext>(), null!, Options.Create(affiliateJwt), affiliateHasher, null!, builder.Configuration, NullLogger<AffiliateAuthController>.Instance));
        builder.Services.AddScoped(sp => new AdminAffiliateController(sp.GetRequiredService<ApplicationDbContext>(), null!, null!, sp.GetRequiredService<IAffiliatePayoutService>(), null!, null!, null!,
            new TreasuryReadService(sp.GetRequiredService<ApplicationDbContext>(), NullLogger<TreasuryReadService>.Instance), tenant, NullLogger<AdminAffiliateController>.Instance));
        builder.Services.AddScoped(sp => new AuthController(sp.GetRequiredService<ApplicationDbContext>(), Options.Create(jwt), tenant, posHasher, null!,
            Mock.Of<PointOfSale.API.Services.Audit.IAuditService>(), Mock.Of<IUserGovernanceService>(),
            new CommerceHubVendorDebtService(sp.GetRequiredService<ApplicationDbContext>(), Options.Create(new CommerceHubVendorDebtOptions())), NullLogger<AuthController>.Instance));
        await using var app = builder.Build(); using var gate = new SemaphoreSlim(1, 1);
        app.Use(async (context, next) =>
        {
            var db = context.RequestServices.GetRequiredService<ApplicationDbContext>();
            await db.Database.OpenConnectionAsync();
            await using (var cmd = db.Database.GetDbConnection().CreateCommand())
            {
                cmd.CommandText = "select current_database(), inet_server_addr()::text, pg_backend_pid()";
                await using var reader = await cmd.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
                Assert.Equal(f.Db.Database.GetDbConnection().Database, reader.GetString(0)); Assert.StartsWith("pointofsale_tests_", reader.GetString(0));
                Assert.NotEqual("PosEcommerceDb_Baseline", reader.GetString(0));
                var record = JsonSerializer.Serialize(new { method = context.Request.Method, path = context.Request.Path.Value,
                    database = reader.GetString(0), server = reader.GetString(1), backendPid = reader.GetInt32(2), apiPid = Environment.ProcessId, simulated = true });
                await gate.WaitAsync(); try { await File.AppendAllTextAsync(Path.Combine(evidence, "browser-runtime-connections.jsonl"), record + Environment.NewLine); } finally { gate.Release(); }
            }
            // The independently verified scoped connection stays open through action execution.
            await next();
        });
        app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers(); await app.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(evidence, "browser-runtime.json"), JsonSerializer.Serialize(new
        { identity = f.Identity, database = f.Db.Database.GetDbConnection().Database, affiliate = f.Account.Id, store = f.Store,
            email = user.Email, adminEmail = admin.Email, request = request.Id, earned = earned.Id, apiPid = Environment.ProcessId, url = "http://127.0.0.1:5196", simulated = true }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        try
        {
            while (!File.Exists(done))
            {
                // File-triggered fixture-only phase change, never an HTTP funding/reversal route.
                var stale = Path.Combine(evidence, "stale.trigger");
                if (File.Exists(stale) && !File.Exists(Path.Combine(evidence, "stale.ready")))
                {
                    await using var db = f.Scope.CreateDbContext();
                    var pending = await AffiliatePhase5WithdrawalTests.Fixture.ServiceFor(db).RequestPayoutAsync(f.Account.Id, 200, f.Store);
                    db.Add(AffiliateCommissionLedger.CreateReversalWithCustomKey(f.Store, earned.Id, earned.AttributionId,
                        f.Account.Id, earned.OrderId, earned.OrderItemId, 4000, 400, .1m, "EGP", $"P5_SIMULATED_NO_MONEY:{pending.Id}"));
                    await db.SaveChangesAsync();
                    await File.WriteAllTextAsync(Path.Combine(evidence, "stale.ready"), pending.Id.ToString());
                }
                await Task.Delay(250, timeout.Token);
            }
            await using var final = f.Scope.CreateDbContext();
            Assert.False(await final.AffiliateCommissionLedger.AnyAsync(e => e.EntryType == CommissionEntryType.Paid));
            Assert.Empty(await final.AffiliateBalanceLedger.ToListAsync()); Assert.Empty(await final.JournalEntries.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await final.LedgerEntries.IgnoreQueryFilters().ToListAsync()); Assert.Empty(await final.AccountingOutboxMessages.ToListAsync());
            Assert.Empty(await final.AffiliatePayoutTransferAllocations.ToListAsync()); Assert.Empty(await final.AffiliatePayoutTransferEvidence.ToListAsync());
            await File.WriteAllTextAsync(Path.Combine(evidence, "browser-final-state.json"), JsonSerializer.Serialize(new
            { requests = await final.AffiliatePayoutRequests.ToListAsync(), balance = await AffiliatePhase5WithdrawalTests.Fixture.ServiceFor(final).GetAvailableBalanceAsync(f.Account.Id, f.Store), noMoneyEffects = true }));
        }
        finally { await app.StopAsync(); }
    }

    private sealed class OnlyP51Actions : IApplicationModelConvention
    {
        public void Apply(ApplicationModel application)
        {
            var allowed = new Dictionary<Type, string[]>
            {
                [typeof(AuthController)] = ["Login", "Logout", "GetSession", "RefreshToken"],
                [typeof(AffiliateAuthController)] = ["Login", "Logout", "RefreshToken"],
                [typeof(AffiliatePortalController)] = ["GetProfile", "GetDashboard", "GetEarningsSummary", "GetPayouts", "RequestPayout"],
                [typeof(AdminAffiliateController)] = ["GetAffiliateDetail", "GetAffiliateOrders", "ApprovePayoutRequest", "RejectPayoutRequest", "GetAffiliateWithdrawals"]
            };
            foreach (var c in application.Controllers.ToArray())
            {
                if (!allowed.TryGetValue(c.ControllerType.AsType(), out var names)) { application.Controllers.Remove(c); continue; }
                foreach (var a in c.Actions.ToArray()) if (!names.Contains(a.ActionMethod.Name)) c.Actions.Remove(a);
            }
        }
    }
    private sealed class P51BrowserFactAttribute : FactAttribute
    { public P51BrowserFactAttribute() { if (Environment.GetEnvironmentVariable("P51_BROWSER_HOST") != "1") Skip = "Opt-in disposable headed Admin browser test"; } }
}
