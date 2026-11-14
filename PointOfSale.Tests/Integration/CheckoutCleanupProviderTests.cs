using System.Data.Common;
using System.Net;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using PointOfSale.API.Jobs;
using PointOfSale.API.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Real fenced disposable PostgreSQL18. Only storage outcomes are recorded;
// no R2/S3 client,external storage or production database is ever constructed.
public sealed class CheckoutCleanupProviderTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("missing-source")]
    [InlineData("missing-final")]
    [InlineData("unknown-source")]
    [InlineData("unknown-final")]
    [InlineData("known-source")]
    public async Task DurableDeletingPrecedesStorageAndUnknownOutcomesRecoverWithTheSameKeys(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var attachment = await SeedAsync(f);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        if (state == "missing-source") deleted.Add(attachment.ObjectKey);
        if (state == "missing-final") deleted.Add(attachment.FinalObjectKey!);
        var requests = new List<string>(); var fail = true;
        var storage = new Mock<IR2StorageService>(MockBehavior.Strict);
        storage.Setup(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string key, CancellationToken _) => {
                requests.Add(key);
                Assert.True(key == attachment.ObjectKey || key == attachment.FinalObjectKey);
                await using var observer = Context(f);
                var current = await observer.CheckoutAttachments.IgnoreQueryFilters().SingleAsync();
                Assert.Equal(CheckoutAttachmentStatus.Deleting, current.Status); Assert.False(current.IsDeleted);
                if (deleted.Contains(key)) throw Missing();
                if (fail && state == "known-source" && key == attachment.ObjectKey)
                    throw new InvalidOperationException("Controlled storage refusal before deletion");
                deleted.Add(key); // recorded remote effect before deliberately losing the response
                if (fail && ((state == "unknown-source" && key == attachment.ObjectKey) ||
                             (state == "unknown-final" && key == attachment.FinalObjectKey)))
                    throw new TimeoutException("Controlled unknown delete outcome");
            });
        await using (var first = Context(f))
        {
            var operation = Job(first, storage.Object);
            if (state.StartsWith("unknown-", StringComparison.Ordinal) || state == "known-source")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => operation.RunAsync());
                await using var observer = Context(f);
                var pending = await observer.CheckoutAttachments.IgnoreQueryFilters().SingleAsync();
                Assert.Equal(CheckoutAttachmentStatus.Deleting, pending.Status); Assert.False(pending.IsDeleted);
                Assert.Equal(state == "unknown-final" ? 2 : 1, requests.Count);
                fail = false;
                // A fresh context models process/session loss without expiring or
                // assuming that an unknown external operation was never sent.
                await using var restarted = Context(f);
                await Job(restarted, storage.Object).RunAsync();
            }
            else await operation.RunAsync();
        }
        await using var final = Context(f);
        var completed = await final.CheckoutAttachments.IgnoreQueryFilters().SingleAsync();
        Assert.True(completed.IsDeleted); Assert.Equal(CheckoutAttachmentStatus.Expired, completed.Status);
        Assert.Equal(attachment.ObjectKey, completed.ObjectKey); Assert.Equal(attachment.FinalObjectKey, completed.FinalObjectKey);
        Assert.Contains(attachment.ObjectKey, deleted); Assert.Contains(attachment.FinalObjectKey!, deleted);
        Assert.Null(completed.OrderId);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("final")]
    public async Task CallerCancellationPreservesDurableRetryStateAndIsNotReplacedByAggregateFailure(string boundary)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var attachment = await SeedAsync(f);
        using var cancellation = new CancellationTokenSource();
        var deleted = new HashSet<string>(StringComparer.Ordinal); var fail = true;
        var storage = new Mock<IR2StorageService>(MockBehavior.Strict);
        storage.Setup(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken ct) => {
                if (!deleted.Add(key)) return Task.FromException(Missing());
                if (fail && key == (boundary == "source" ? attachment.ObjectKey : attachment.FinalObjectKey))
                {
                    cancellation.Cancel(); return Task.FromCanceled(ct);
                }
                return Task.CompletedTask;
            });
        await using var first = Context(f);
        var error = await Record.ExceptionAsync(() => Job(first, storage.Object).RunAsync(cancellation.Token));
        await using (var observer = Context(f))
        {
            var pending = await observer.CheckoutAttachments.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(CheckoutAttachmentStatus.Deleting, pending.Status); Assert.False(pending.IsDeleted);
        }
        fail = false;
        await using var restarted = Context(f); await Job(restarted, storage.Object).RunAsync();
        var completed = await restarted.CheckoutAttachments.IgnoreQueryFilters().SingleAsync();
        Assert.True(completed.IsDeleted); Assert.Equal(CheckoutAttachmentStatus.Expired, completed.Status);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    [Fact]
    public async Task FinalKeyTurnoverBetweenCandidateReadAndClaimPreventsEveryStorageRequest()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var attachment = await SeedAsync(f); var replacement = attachment.FinalObjectKey + ".new";
        var interpose = new ChangeFinalKey(f.ConnectionString, attachment.Id, replacement);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(interpose).Options);
        var storage = new Mock<IR2StorageService>(MockBehavior.Strict);
        await Job(db, storage.Object).RunAsync();
        Assert.True(interpose.Executed);
        storage.Verify(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        await using var observer = Context(f);
        var current = await observer.CheckoutAttachments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(replacement, current.FinalObjectKey); Assert.False(current.IsDeleted);
        Assert.Equal(CheckoutAttachmentStatus.Finalized, current.Status);
    }

    [Theory]
    [InlineData("foreign-source")]
    [InlineData("foreign-final")]
    [InlineData("traversal")]
    public async Task CorruptOwnedKeysRejectBeforeClaimOrStorage(string corruption)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var attachment = await SeedAsync(f);
        if (corruption == "foreign-source") attachment.ObjectKey = attachment.ObjectKey.Replace(f.VendorId.ToString("N"), Guid.NewGuid().ToString("N"), StringComparison.Ordinal);
        else if (corruption == "foreign-final") attachment.FinalObjectKey = attachment.FinalObjectKey!.Replace(f.VendorId.ToString("N"), Guid.NewGuid().ToString("N"), StringComparison.Ordinal);
        else attachment.FinalObjectKey += "/../victim.jpg";
        await f.Db.SaveChangesAsync();
        var storage = new Mock<IR2StorageService>(MockBehavior.Strict);
        await using var db = Context(f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Job(db, storage.Object).RunAsync());
        storage.Verify(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        await using var observer = Context(f);
        var current = await observer.CheckoutAttachments.IgnoreQueryFilters().SingleAsync();
        Assert.False(current.IsDeleted); Assert.Equal(CheckoutAttachmentStatus.Finalized, current.Status);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("cart")]
    [InlineData("field")]
    [InlineData("owner")]
    [InlineData("owner-type")]
    [InlineData("session")]
    [InlineData("customer")]
    [InlineData("source")]
    [InlineData("order")]
    [InlineData("deleted")]
    [InlineData("expiry")]
    [InlineData("state")]
    public async Task EveryPersistedClaimIdentityAndEligibilityTurnoverRejectsBeforeStorage(string change)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var attachment = await SeedAsync(f);
        var order = new Order { Id = Guid.NewGuid(), TenantId = f.VendorId, OrderNumber = "FICTIONAL-CLEANUP-EXCLUSION" };
        f.Db.Orders.Add(order); await f.Db.SaveChangesAsync();
        string? committed = null;
        var boundary = new BeforeClaim(async ct =>
        {
            await Turnover(f, attachment.Id, order.Id, change, ct);
            await using var observer = Context(f);
            committed = Identity(await observer.CheckoutAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct));
        });
        await using var claimant = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(boundary).Options);
        var storage = new Mock<IR2StorageService>(MockBehavior.Strict);
        await Job(claimant, storage.Object).RunAsync();
        Assert.True(boundary.Observed); Assert.NotNull(committed); storage.VerifyNoOtherCalls();
        await using var verify = Context(f);
        Assert.Equal(committed, Identity(await verify.CheckoutAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync()));
        Assert.Single(await verify.Orders.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("final")]
    [InlineData("order")]
    [InlineData("deleted")]
    public async Task TerminalCleanupCannotOverwriteTurnedOverOwnershipAfterRecordedDeletes(string change)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var attachment = await SeedAsync(f);
        var order = new Order { Id = Guid.NewGuid(), TenantId = f.VendorId, OrderNumber = "FICTIONAL-CLEANUP-TERMINAL" };
        f.Db.Orders.Add(order); await f.Db.SaveChangesAsync();
        string? committed = null; var deleted = new List<string>();
        var storage = new Mock<IR2StorageService>(MockBehavior.Strict);
        storage.Setup(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string key, CancellationToken ct) =>
            {
                deleted.Add(key);
                await using var observer = Context(f);
                var current = await observer.CheckoutAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct);
                Assert.Equal(CheckoutAttachmentStatus.Deleting, current.Status);
                if (key != attachment.FinalObjectKey) return;
                // Recorded old-object deletion is already complete. This does
                // not promise external revocation; only the SQL terminal CAS
                // must refuse to overwrite independently committed turnover.
                await Turnover(f, attachment.Id, order.Id, change, ct);
                await using var changed = Context(f);
                committed = Identity(await changed.CheckoutAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct));
            });
        await using var claimant = Context(f);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Job(claimant, storage.Object).RunAsync());
        Assert.Equal("Attachment cleanup ownership changed before completion.", error.Message);
        Assert.Equal(new[] { attachment.ObjectKey, attachment.FinalObjectKey }, deleted);
        Assert.NotNull(committed);
        await using var verify = Context(f);
        Assert.Equal(committed, Identity(await verify.CheckoutAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync()));
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
        storage.Verify(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        storage.VerifyNoOtherCalls();
    }

    private static async Task Turnover(ProductionCoordinationAuthorityTests.Fixture f, Guid id, Guid order, string change, CancellationToken ct)
    {
        // Fixed fixture-only statements, never identifiers or SQL from an HTTP
        // request. A separate committed connection creates the actual race.
        var assignment = change switch
        {
            "tenant" => "\"TenantId\"=@replacement", "cart" => "\"CartId\"=@replacement", "field" => "\"CheckoutFieldDefinitionId\"=@replacement",
            "owner" => "\"OwnerId\"='new-fixture-owner'", "owner-type" => "\"OwnerType\"='customer'", "session" => "\"SessionId\"=NULL",
            "customer" => "\"OnlineCustomerId\"=@replacement", "source" => "\"ObjectKey\"=\"ObjectKey\"||'.replacement'",
            "final" => "\"FinalObjectKey\"=\"FinalObjectKey\"||'.replacement'", "order" => "\"OrderId\"=@order",
            "deleted" => "\"IsDeleted\"=TRUE", "expiry" => "\"ExpiresAtUtc\"=clock_timestamp()+interval '1 hour'",
            "state" => "\"Status\"='Attached'", _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        await using var connection = new NpgsqlConnection(f.ConnectionString); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("UPDATE \"CheckoutAttachments\" SET " + assignment + " WHERE \"Id\"=@id", connection);
        command.Parameters.AddWithValue("id", id);
        if (change is "tenant" or "cart" or "field" or "customer") command.Parameters.AddWithValue("replacement", Guid.NewGuid());
        if (change == "order") command.Parameters.AddWithValue("order", order);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
    }

    private static string Identity(CheckoutAttachment a) => System.Text.Json.JsonSerializer.Serialize(new
    {
        a.Id, a.TenantId, a.CartId, a.CheckoutFieldDefinitionId, a.OwnerId, a.OwnerType, a.SessionId, a.OnlineCustomerId,
        a.ObjectKey, a.FinalObjectKey, a.OrderId, a.Status, a.IsDeleted, a.ExpiresAtUtc, a.UpdatedAt
    });

    private sealed class BeforeClaim(Func<CancellationToken, Task> change) : DbCommandInterceptor
    {
        internal bool Observed { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Observed && command.CommandText.StartsWith("UPDATE \"CheckoutAttachments\"", StringComparison.Ordinal))
            { Observed = true; await change(cancellationToken); }
            return result;
        }
    }

    private static AmazonS3Exception Missing() => new("Recorded explicit NotFound") { StatusCode = HttpStatusCode.NotFound };
    private static ApplicationDbContext Context(ProductionCoordinationAuthorityTests.Fixture f) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
    private static CheckoutAttachmentCleanupJob Job(ApplicationDbContext db, IR2StorageService storage) =>
        new(db, storage, NullLogger<CheckoutAttachmentCleanupJob>.Instance);

    private static async Task<CheckoutAttachment> SeedAsync(ProductionCoordinationAuthorityTests.Fixture f)
    {
        var cart = Guid.NewGuid(); var id = Guid.NewGuid();
        var field = new CheckoutFieldDefinition { Id = Guid.NewGuid(), TenantId = f.VendorId, FieldKind = CheckoutFieldKind.Custom,
            FieldType = CheckoutFieldType.Image, Label = "Disposable cleanup", IsEnabled = true, ConfigurationSchemaVersion = 1,
            ConfigurationJson = "{\"schemaVersion\":1,\"allowedMimeTypes\":[\"image/jpeg\"],\"maxFileSize\":5242880,\"maxFiles\":2}" };
        var attachment = new CheckoutAttachment { Id = id, TenantId = f.VendorId, CartId = cart, CheckoutFieldDefinitionId = field.Id,
            OwnerId = "owner", OwnerType = "session", SessionId = "owner", Status = CheckoutAttachmentStatus.Finalized,
            ObjectKey = $"tenants/{f.VendorId:N}/checkout/{cart:N}/fields/{field.Id:N}/attachments/{id:N}.jpg",
            FinalObjectKey = $"tenants/{f.VendorId:N}/checkout-final/{cart:N}/fields/{field.Id:N}/attachments/{id:N}.jpg",
            ContentType = "image/jpeg", MediaType = "image", OriginalFileName = "fixture.jpg", ExpectedSizeBytes = 1024,
            SizeBytes = 1024, ExpiresAtUtc = DateTime.UtcNow.AddHours(-1) };
        f.Db.AddRange(field, attachment); await f.Db.SaveChangesAsync(); return attachment;
    }

    private sealed class ChangeFinalKey(string connection, Guid id, string replacement) : DbCommandInterceptor
    {
        public bool Executed { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Executed && command.CommandText.StartsWith("UPDATE \"CheckoutAttachments\"", StringComparison.Ordinal))
            {
                Executed = true;
                await using var other = new NpgsqlConnection(connection); await other.OpenAsync(cancellationToken);
                await using var mutation = new NpgsqlCommand("UPDATE \"CheckoutAttachments\" SET \"FinalObjectKey\"=@key WHERE \"Id\"=@id", other);
                mutation.Parameters.AddWithValue("key", replacement); mutation.Parameters.AddWithValue("id", id);
                Assert.Equal(1, await mutation.ExecuteNonQueryAsync(cancellationToken));
            }
            return result;
        }
    }
}
