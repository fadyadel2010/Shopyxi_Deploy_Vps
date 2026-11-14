using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services.Auth;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Current recipient admission on the real guarded disposable PostgreSQL18;
// the mail adapter never contacts a network or exposes the bearer token.
public sealed class AffiliateVerificationRecipientAuthorityProviderTests
{
    [Theory]
    [InlineData("email-changed")]
    [InlineData("user-deleted")]
    [InlineData("account-deleted")]
    [InlineData("verified")]
    public async Task ResendCannotIssueOrDeliverFromAStaleRecipientAndIdentitySnapshot(string turnover)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "APP2RECIP", "Fictional recipient authority"); account.Id = Guid.NewGuid();
        var user = AffiliateUser.Create(Guid.Empty, "old-recipient@example.invalid", "fixture-not-a-login", account.Id); user.Id = userId;
        var token = AffiliateEmailVerificationToken.Create(userId, AffiliateEmailVerificationToken.NewRawToken(), DateTime.UtcNow.AddHours(1));
        f.Db.AddRange(account, user, token); await f.Db.SaveChangesAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        var pause = new PauseInitialIdentityRead();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var mail = new Mock<IEmailSender>(MockBehavior.Strict); var sent = 0;
        mail.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => sent++).Returns(Task.CompletedTask);
        var controller = AffiliateRefreshAuthorityProviderTests.Controller(db, mail.Object);
        var pending = controller.ResendVerification(new AffiliateForgotPasswordDto { Email = user.Email }, ct);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            // Independent context commits after the old SELECT executed, before
            // the actual user-row admission. No mocked concurrency is used.
            if (turnover == "email-changed") user.SetEmail("new-recipient@example.invalid");
            else if (turnover == "user-deleted") user.IsDeleted = true;
            else if (turnover == "account-deleted") account.IsDeleted = true;
            else user.VerifyEmail();
            await f.Db.SaveChangesAsync(ct);
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await pending; } catch { /* Drain exact owned work; assertions below retain its failure. */ }
        }
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var saved = await f.Db.AffiliateEmailVerificationTokens.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
        await using (var receipt = new FileStream(Path.Combine(output, "verification-recipient-authority-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew, FileAccess.Write))
            await JsonSerializer.SerializeAsync(receipt, new { turnover, pause.ObservedRead, SentCount = sent, TokenCount = saved.Count,
                OriginalTokenRetained = saved.Count == 1 && saved[0].Id == token.Id, Result = response.GetType().Name,
                TransactionDisposed = db.Database.CurrentTransaction == null }, cancellationToken: ct);
        Assert.IsType<OkObjectResult>(response); Assert.True(pause.ObservedRead); Assert.Equal(0, sent);
        var retained = Assert.Single(saved); Assert.Equal(token.Id, retained.Id); Assert.Equal(token.TokenHash, retained.TokenHash);
        Assert.Null(retained.ConsumedAtUtc); Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync(ct));
        Assert.Null(db.Database.CurrentTransaction);
    }

    private sealed class PauseInitialIdentityRead : DbCommandInterceptor
    {
        internal bool ObservedRead { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!ObservedRead && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"AffiliateUsers\"", StringComparison.Ordinal))
            {
                ObservedRead = true; Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}
