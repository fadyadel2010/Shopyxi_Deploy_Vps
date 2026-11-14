using System.IdentityModel.Tokens.Jwt;
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
using PointOfSale.API.Controllers;
using PointOfSale.API.Models;
using PointOfSale.API.Security;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Opt-in, loopback-only actual MVC/auth/services host. Never starts production Program,
/// background workers, Mongo, a bus, Treasury or provider adapters. No funding/injection endpoint.</summary>
public sealed class AffiliatePhase5BrowserHostTests
{
    [P5BrowserFact]
    public async Task IsolatedBrowserRuntime()
    {
        await using var f = new AffiliatePhase5WithdrawalTests.Fixture(); await f.Initialize();
        f.Commission(500); f.Commission(900, true); await f.Db.SaveChangesAsync();
        var user = await f.Db.AffiliateUsers.SingleAsync();
        var hasher = new PasswordHasher<AffiliateUser>();
        user.SetPasswordHash(hasher.HashPassword(user, "P5DisposableOnly!2026")); await f.Db.SaveChangesAsync();
        var root = TestConfiguration.GetSolutionRoot();
        var evidence = Path.Combine(root, ".ai", "evidence", "affiliate-phase5-20261022");
        var done = Path.Combine(evidence, "browser.done");
        Assert.False(File.Exists(done), "Remove only the task-owned browser.done marker before a new host run.");
        var jwt = new AffiliateJwtSettings { Secret = "P5-TEST-ONLY-NO-MONEY-LOOPBACK-20261022-KEY", Issuer = "P5NoMoney", Audience = "P5Portal" };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = typeof(AffiliatePhase5BrowserHostTests).Assembly.FullName });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["SyntheticQa:Enabled"] = "false" });
        builder.WebHost.UseUrls("http://127.0.0.1:5195");
        builder.Logging.ClearProviders();
        builder.Services.AddScoped(_ => f.Scope.CreateDbContext());
        builder.Services.AddScoped<IAffiliatePayoutService>(sp => AffiliatePhase5WithdrawalTests.Fixture.ServiceFor(sp.GetRequiredService<ApplicationDbContext>()));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience, ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true };
            o.Events = new JwtBearerEvents { OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Request.Headers.Authorization)) context.Token = context.Request.Cookies[AuthCookieConfiguration.AffiliateAccessTokenCookieName];
                return Task.CompletedTask;
            } };
        });
        builder.Services.AddAuthorization(o => o.AddPolicy("AffiliateOnly", p => p.RequireAuthenticatedUser().RequireRole("Affiliate")));
        builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("AffiliateAuthRateLimit", l =>
        { l.PermitLimit = 1000; l.Window = TimeSpan.FromMinutes(1); l.QueueLimit = 0; l.QueueProcessingOrder = QueueProcessingOrder.OldestFirst; }));
        builder.Services.AddControllers(o => o.Conventions.Add(new OnlyP5Actions())).AddApplicationPart(typeof(AffiliatePortalController).Assembly).AddControllersAsServices();
        // Application-model conventions restrict routes, while DI registration is a separate step.
        // Remove unrelated controller descriptors too: no production adapter dependency is registered.
        foreach (var descriptor in builder.Services.Where(d => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(d.ServiceType)).ToArray())
            builder.Services.Remove(descriptor);
        builder.Services.AddScoped(sp => new AffiliatePortalController(sp.GetRequiredService<ApplicationDbContext>(), null!, null!,
            sp.GetRequiredService<IAffiliatePayoutService>(), null!, null!, NullLogger<AffiliatePortalController>.Instance));
        builder.Services.AddScoped(sp => new AffiliateAuthController(sp.GetRequiredService<ApplicationDbContext>(), null!, Options.Create(jwt),
            hasher, null!, builder.Configuration, NullLogger<AffiliateAuthController>.Instance));
        builder.Services.AddScoped(sp => new AdminAffiliateController(sp.GetRequiredService<ApplicationDbContext>(), null!, null!,
            sp.GetRequiredService<IAffiliatePayoutService>(), null!, null!, null!, null!, new TenantContextForP5(f.Store), NullLogger<AdminAffiliateController>.Instance));
        await using var app = builder.Build();
        using var logGate = new SemaphoreSlim(1, 1);
        app.Use(async (context, next) =>
        {
            // Independently query the runtime's actual scoped connection before EVERY mutation.
            await using var cmd = context.RequestServices.GetRequiredService<ApplicationDbContext>().Database.GetDbConnection().CreateCommand();
            await cmd.Connection!.OpenAsync();
            cmd.CommandText = "SELECT current_database(), inet_server_addr()::text, pg_backend_pid()";
            await using var r = await cmd.ExecuteReaderAsync(); Assert.True(await r.ReadAsync());
            var dbName = r.GetString(0); Assert.StartsWith("pointofsale_tests_", dbName);
            Assert.Equal(f.Db.Database.GetDbConnection().Database, dbName); Assert.NotEqual("PosEcommerceDb_Baseline", dbName);
            var record = JsonSerializer.Serialize(new { method = context.Request.Method, path = context.Request.Path.Value,
                database = dbName, server = r.GetString(1), backendPid = r.GetInt32(2), apiPid = Environment.ProcessId, simulated = true });
            await r.DisposeAsync(); await cmd.Connection.CloseAsync();
            await logGate.WaitAsync();
            try { await File.AppendAllTextAsync(Path.Combine(evidence, "browser-runtime-connections.jsonl"), record + Environment.NewLine); }
            finally { logGate.Release(); }
            await next();
        });
        app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers();
        await app.StartAsync();
        var adminToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(jwt.Issuer, jwt.Audience,
            [new Claim(ClaimTypes.NameIdentifier, f.Admin.ToString()), new Claim(ClaimTypes.Role, "Admin")],
            expires: DateTime.UtcNow.AddMinutes(30), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)), SecurityAlgorithms.HmacSha256)));
        await File.WriteAllTextAsync(Path.Combine(evidence, "browser-runtime.json"), JsonSerializer.Serialize(new
        { identity = f.Identity, database = f.Db.Database.GetDbConnection().Database, affiliate = f.Account.Id, store = f.Store,
            email = user.Email, apiPid = Environment.ProcessId, url = "http://127.0.0.1:5195", simulated = true }));
        var privatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "p5-browser-admin-token.txt");
        await File.WriteAllTextAsync(privatePath, adminToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        try
        {
            while (!File.Exists(done)) await Task.Delay(500, timeout.Token);
            await using var db = f.Scope.CreateDbContext();
            Assert.False(await db.AffiliateCommissionLedger.AnyAsync(e => e.EntryType == CommissionEntryType.Paid));
            Assert.Empty(await db.AffiliateBalanceLedger.ToListAsync()); Assert.Empty(await db.JournalEntries.ToListAsync());
            Assert.Empty(await db.LedgerEntries.ToListAsync()); Assert.Empty(await db.AccountingOutboxMessages.ToListAsync());
            Assert.Empty(await db.AffiliatePayoutTransferAllocations.ToListAsync()); Assert.Empty(await db.AffiliatePayoutTransferEvidence.ToListAsync());
            await File.WriteAllTextAsync(Path.Combine(evidence, "browser-final-state.json"), JsonSerializer.Serialize(new
            { requests = await db.AffiliatePayoutRequests.ToListAsync(), balance = await AffiliatePhase5WithdrawalTests.Fixture.ServiceFor(db).GetAvailableBalanceAsync(f.Account.Id, f.Store), noMoneyEffects = true }));
        }
        finally { await app.StopAsync(); File.Delete(privatePath); }
    }

    private sealed class TenantContextForP5 : TenantContext
    {
        // TenantContext uses its normal trusted setter, never a request-supplied store.
        public TenantContextForP5(Guid tenant) { SetTenantId(tenant); }
    }

    private sealed class OnlyP5Actions : IApplicationModelConvention
    {
        public void Apply(ApplicationModel application)
        {
            var allowed = new Dictionary<Type, string[]>
            {
                [typeof(AffiliatePortalController)] = ["GetProfile", "GetDashboard", "GetEarningsSummary", "GetPayouts", "RequestPayout"],
                [typeof(AffiliateAuthController)] = ["Login", "Logout", "RefreshToken"],
                [typeof(AdminAffiliateController)] = ["ApprovePayoutRequest", "RejectPayoutRequest", "GetAffiliateWithdrawals"]
            };
            foreach (var c in application.Controllers.ToArray())
            {
                if (!allowed.TryGetValue(c.ControllerType.AsType(), out var names)) { application.Controllers.Remove(c); continue; }
                foreach (var a in c.Actions.ToArray()) if (!names.Contains(a.ActionMethod.Name)) c.Actions.Remove(a);
            }
        }
    }

    private sealed class P5BrowserFactAttribute : FactAttribute
    {
        public P5BrowserFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("P5_BROWSER_HOST") != "1")
                Skip = "Opt-in isolated headed-browser runtime; set P5_BROWSER_HOST=1.";
        }
    }
}
