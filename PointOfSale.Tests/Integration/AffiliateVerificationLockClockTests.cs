using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Canonical controllers, fictional bearer capabilities, restricted PostgreSQL18.
// No host, email, bank, or shared-primary workflow is constructed.
public sealed class AffiliateVerificationLockClockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenExpiryIsRecheckedUsingDatabaseClockAfterActualTokenLockWait(bool reset)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, _) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db);
        user.SetEmail(user.Email); // Verification must start unverified.
        var originalPassword = user.PasswordHash;
        var raw = AffiliateEmailVerificationToken.NewRawToken();
        await using var blocker = new NpgsqlConnection(f.ConnectionString);
        await blocker.OpenAsync();
        await using var clock = new NpgsqlCommand("SELECT clock_timestamp() + interval '4 seconds'", blocker);
        var expires = (DateTime)(await clock.ExecuteScalarAsync())!;
        Guid tokenId;
        if (reset)
        {
            var token = AffiliatePasswordResetToken.Create(user.Id, raw, expires);
            token.Id = Guid.NewGuid();
            tokenId = token.Id; f.Db.Add(token);
        }
        else
        {
            var token = AffiliateEmailVerificationToken.Create(user.Id, raw, expires);
            token.Id = Guid.NewGuid();
            tokenId = token.Id; f.Db.Add(token);
        }
        await f.Db.SaveChangesAsync();
        await using var held = await blocker.BeginTransactionAsync();
        var table = reset ? "AffiliatePasswordResetTokens" : "AffiliateEmailVerificationTokens";
        await using var hold = new NpgsqlCommand($"SELECT \"Id\" FROM \"{table}\" WHERE \"Id\"=@id FOR UPDATE", blocker, held);
        hold.Parameters.AddWithValue("id", tokenId);
        Assert.Equal(tokenId, await hold.ExecuteScalarAsync());

        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).Options);
        await db.Database.OpenConnectionAsync();
        var contenderPid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        var controller = AffiliateRefreshAuthorityProviderTests.Controller(db);
        var pending = reset
            ? controller.ResetPassword(new AffiliateResetPasswordDto { Token = raw, NewPassword = "Fictional-Reset-832!" }, default)
            : controller.VerifyEmail(new AffiliateVerifyEmailDto { Token = raw }, default);
        bool blockingObserved = false;
        DateTime releasedAt;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!blockingObserved)
            {
                await using var probe = new NpgsqlCommand("SELECT @blocker = ANY(pg_blocking_pids(@contender))", blocker, held);
                probe.Parameters.AddWithValue("blocker", blocker.ProcessID);
                probe.Parameters.AddWithValue("contender", contenderPid);
                blockingObserved = (bool)(await probe.ExecuteScalarAsync(deadline.Token))!;
                if (!blockingObserved) await Task.Delay(20, deadline.Token);
            }
            // Let the actual database clock cross expiry while the contender
            // is demonstrably blocked. Do not simulate a stale tracking read.
            await using var wait = new NpgsqlCommand("SELECT pg_sleep(GREATEST(0, EXTRACT(EPOCH FROM (@expires - clock_timestamp()))) + 0.1)", blocker, held);
            wait.Parameters.AddWithValue("expires", expires);
            await wait.ExecuteNonQueryAsync(deadline.Token);
            await using var readClock = new NpgsqlCommand("SELECT clock_timestamp()", blocker, held);
            releasedAt = (DateTime)(await readClock.ExecuteScalarAsync(deadline.Token))!;
        }
        finally { await held.RollbackAsync(); }
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        var directory = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR")!;
        await File.WriteAllTextAsync(Path.Combine(directory, "token-lock-clock-" + reset + ".json"),
            JsonSerializer.Serialize(new { table, tokenId, expires, releasedAt, blockingObserved,
                blockerPid = blocker.ProcessID, contenderPid, response = response.GetType().Name,
                provider = "PostgreSQL18", role = "app17c_fixture", canonicalController = true }));
        Assert.True(blockingObserved); Assert.True(releasedAt > expires);
        Assert.IsType<UnauthorizedObjectResult>(response);
        await using var observer = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        var saved = await observer.AffiliateUsers.IgnoreQueryFilters().SingleAsync(u => u.Id == user.Id);
        Assert.False(saved.EmailVerified); Assert.Equal(originalPassword, saved.PasswordHash);
        if (reset) Assert.Null((await observer.AffiliatePasswordResetTokens.IgnoreQueryFilters().SingleAsync()).ConsumedAtUtc);
        else Assert.Null((await observer.AffiliateEmailVerificationTokens.IgnoreQueryFilters().SingleAsync()).ConsumedAtUtc);
        Assert.Empty(await observer.AuditLogs.IgnoreQueryFilters().ToListAsync());
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
    }
}
