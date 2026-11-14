using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Cart;
using PointOfSale.API.Services.Checkout;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Required owned PostgreSQL18/current model. Actual two-row claims/savepoints;
// no checkout submission, financial operation, object storage, host or sender.
public sealed class CheckoutAttachmentAtomicClaimProviderTests
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("owner")]
    [InlineData("object-key")]
    [InlineData("final-key")]
    [InlineData("state")]
    [InlineData("deleted")]
    [InlineData("expired-after-admission")]
    public async Task SecondAttachmentDriftRollsBackFirstClaimWithoutAbortingCallerTransaction(string drift)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var field = new CheckoutFieldDefinition { Id = Guid.NewGuid(), TenantId = f.VendorId,
            FieldKind = CheckoutFieldKind.Custom, FieldType = CheckoutFieldType.MultiImage, Label = "fictional-two-file-field", IsEnabled = true,
            ConfigurationSchemaVersion = 1, ConfigurationJson = "{\"schemaVersion\":1,\"allowedMimeTypes\":[\"image/jpeg\"],\"maxFileSize\":5242880,\"maxFiles\":2}" };
        var cart = Guid.NewGuid();
        CheckoutAttachment NewAttachment(DateTime created) => new()
        {
            Id = Guid.NewGuid(), TenantId = f.VendorId, CartId = cart, CheckoutFieldDefinitionId = field.Id,
            OwnerId = "fixture-owner", OwnerType = "session", SessionId = "fixture-owner",
            ObjectKey = $"tenants/{f.VendorId:N}/checkout/{cart:N}/fields/{field.Id:N}/attachments/{Guid.NewGuid():N}.jpg",
            FinalObjectKey = $"tenants/{f.VendorId:N}/checkout-final/{cart:N}/fields/{field.Id:N}/attachments/{Guid.NewGuid():N}.jpg",
            Status = CheckoutAttachmentStatus.Finalized, ContentType = "image/jpeg", MediaType = "image", OriginalFileName = "fixture.jpg",
            ExpectedSizeBytes = 1024, SizeBytes = 1024, CreatedAt = created, ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var first = NewAttachment(DateTime.UtcNow.AddMinutes(-2));
        var second = NewAttachment(DateTime.UtcNow.AddMinutes(-1));
        var order = new Order { Id = Guid.NewGuid(), TenantId = f.VendorId, OrderNumber = "FICTIONAL-CLAIM-ONLY", CartId = cart };
        f.Db.AddRange(field, first, second, order); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        // BaseEntity.CreatedAt is assigned by SaveChanges. Use actual persisted
        // ordering, including PostgreSQL microsecond ties and Guid ordering,
        // rather than assuming the two pre-save timestamps survived.
        var persistedOrder = await f.Db.CheckoutAttachments.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        first = persistedOrder[0]; second = persistedOrder[1];
        var observer = new DriftBeforeSecondClaim(f.ConnectionString, second.Id, drift);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(observer).Options);
        var storage = new Mock<IR2StorageService>(MockBehavior.Strict);
        var carts = new Mock<ICartService>(MockBehavior.Strict);
        var service = new CheckoutAttachmentService(db, storage.Object, carts.Object, new FileValidationService(),
            NullLogger<CheckoutAttachmentService>.Instance);
        await using var tx = await db.Database.BeginTransactionAsync();
        // A caller-owned tracked change, outside the service's savepoint, must
        // remain usable after a rejected partial claim.
        var callerOrder = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == order.Id);
        callerOrder.CustomerNotes = "caller-before-savepoint"; await db.SaveChangesAsync();
        Task<IReadOnlyList<CheckoutAttachment>> Attach() => service.ValidateAndAttachAsync(f.VendorId, "fixture-owner", "session",
            "fixture-owner", null, cart, order.Id, field, [first.Id, second.Id]);
        if (drift == "unchanged")
        {
            Assert.Equal(2, (await Attach()).Count);
            await db.SaveChangesAsync();
        }
        else
        {
            var error = await Assert.ThrowsAsync<CheckoutDynamicFieldValidationException>(Attach);
            Assert.Equal("CHECKOUT_ATTACHMENT_INVALID_STATE", error.ErrorCode);
        }
        Assert.Equal(2, observer.ClaimsObserved); Assert.True(observer.BeforeSecondClaimObserved);
        var current = await db.CheckoutAttachments.IgnoreQueryFilters().AsNoTracking().OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        Assert.Equal(2, current.Count);
        if (drift == "unchanged") Assert.All(current, a => { Assert.Equal(order.Id, a.OrderId); Assert.Equal(CheckoutAttachmentStatus.Attached, a.Status); });
        else
        {
            Assert.All(current, a => Assert.Null(a.OrderId));
            Assert.Equal(CheckoutAttachmentStatus.Finalized, current[0].Status);
            Assert.Null(current[0].AttachedAtUtc);
            // Even after rollback-to-savepoint, caller transaction can flush
            // and commit its unrelated pre-savepoint work.
            callerOrder.CustomerNotes = "caller-after-rejected-claim"; await db.SaveChangesAsync();
        }
        storage.VerifyNoOtherCalls(); carts.VerifyNoOtherCalls();
        await tx.CommitAsync();
        await using var fresh = f.ScopedDb(f.VendorId);
        Assert.Equal(drift == "unchanged" ? "caller-before-savepoint" : "caller-after-rejected-claim",
            (await fresh.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == order.Id)).CustomerNotes);
        var persisted = await fresh.CheckoutAttachments.IgnoreQueryFilters().AsNoTracking().OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        if (drift != "unchanged") Assert.All(persisted, a => Assert.Null(a.OrderId));
        if (drift == "expired-after-admission") Assert.True(persisted[1].ExpiresAtUtc < DateTime.UtcNow);
    }

    [Fact]
    public async Task ExpiryDuringAnUnchangedRowLockWaitRejectsAndRollsBackTheEarlierClaim()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        var field = new CheckoutFieldDefinition { Id = Guid.NewGuid(), TenantId = f.VendorId,
            FieldKind = CheckoutFieldKind.Custom, FieldType = CheckoutFieldType.MultiImage, Label = "fictional-lock-wait-field", IsEnabled = true,
            ConfigurationSchemaVersion = 1, ConfigurationJson = "{\"schemaVersion\":1,\"allowedMimeTypes\":[\"image/jpeg\"],\"maxFileSize\":5242880,\"maxFiles\":2}" };
        var cart = Guid.NewGuid();
        CheckoutAttachment NewAttachment() => new()
        {
            Id = Guid.NewGuid(), TenantId = f.VendorId, CartId = cart, CheckoutFieldDefinitionId = field.Id,
            OwnerId = "fixture-owner", OwnerType = "session", SessionId = "fixture-owner",
            ObjectKey = $"tenants/{f.VendorId:N}/checkout/{cart:N}/fields/{field.Id:N}/attachments/{Guid.NewGuid():N}.jpg",
            FinalObjectKey = $"tenants/{f.VendorId:N}/checkout-final/{cart:N}/fields/{field.Id:N}/attachments/{Guid.NewGuid():N}.jpg",
            Status = CheckoutAttachmentStatus.Finalized, ContentType = "image/jpeg", MediaType = "image", OriginalFileName = "fixture.jpg",
            ExpectedSizeBytes = 1024, SizeBytes = 1024, ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var order = new Order { Id = Guid.NewGuid(), TenantId = f.VendorId, OrderNumber = "FICTIONAL-LOCK-WAIT", CartId = cart };
        f.Db.AddRange(field, NewAttachment(), NewAttachment(), order); await f.Db.SaveChangesAsync(ct); f.Db.ChangeTracker.Clear();
        var rows = await f.Db.CheckoutAttachments.IgnoreQueryFilters().AsNoTracking().OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct);
        var first = rows[0]; var second = rows[1];
        await using var control = new NpgsqlConnection(f.ConnectionString); await control.OpenAsync(ct);
        await using (var expiry = new NpgsqlCommand("UPDATE \"CheckoutAttachments\" SET \"ExpiresAtUtc\"=clock_timestamp()+interval '8 seconds' WHERE \"Id\"=@id RETURNING \"ExpiresAtUtc\"", control))
        {
            expiry.Parameters.AddWithValue("id", second.Id);
            second.ExpiresAtUtc = Assert.IsType<DateTime>(await expiry.ExecuteScalarAsync(ct));
        }
        // A pure tuple lock: no UPDATE/new tuple after the claimant's snapshot.
        // This distinguishes WHERE evaluation before a wait from expiry checked
        // after ownership is actually obtained; EvalPlanQual is not forced.
        await using var holder = new NpgsqlConnection(f.ConnectionString); await holder.OpenAsync(ct);
        await using var held = await holder.BeginTransactionAsync(ct);
        await using (var acquire = new NpgsqlCommand("SELECT \"Id\" FROM \"CheckoutAttachments\" WHERE \"Id\"=@id FOR UPDATE", holder, held))
        {
            acquire.Parameters.AddWithValue("id", second.Id);
            Assert.Equal(second.Id, Assert.IsType<Guid>(await acquire.ExecuteScalarAsync(ct)));
        }
        await using var claimant = new NpgsqlConnection(f.ConnectionString); await claimant.OpenAsync(ct);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(claimant).Options);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var callerOrder = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == order.Id, ct);
        callerOrder.CustomerNotes = "caller-before-lock-wait"; await db.SaveChangesAsync(ct);
        var storage = new Mock<IR2StorageService>(MockBehavior.Strict);
        var carts = new Mock<ICartService>(MockBehavior.Strict);
        var service = new CheckoutAttachmentService(db, storage.Object, carts.Object, new FileValidationService(),
            NullLogger<CheckoutAttachmentService>.Instance);
        var pending = service.ValidateAndAttachAsync(f.VendorId, "fixture-owner", "session", "fixture-owner", null,
            cart, order.Id, field, [first.Id, second.Id], ct);
        var released = false;
        try
        {
            var observed = false;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !pending.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@claimant))", control);
                probe.Parameters.AddWithValue("holder", holder.ProcessID); probe.Parameters.AddWithValue("claimant", claimant.ProcessID);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { observed = true; break; }
                await Task.Delay(20, ct);
            }
            Assert.True(observed, "The actual claimant must be blocked by this exact unchanged-row lock holder.");
            Assert.False(pending.IsCompleted);
            await using (var expire = new NpgsqlCommand("SELECT pg_sleep(GREATEST(0,EXTRACT(EPOCH FROM (@expiry-clock_timestamp())))+0.05)", control))
            {
                expire.Parameters.AddWithValue("expiry", second.ExpiresAtUtc); await expire.ExecuteNonQueryAsync(ct);
            }
            await using (var expired = new NpgsqlCommand("SELECT \"ExpiresAtUtc\"<clock_timestamp() FROM \"CheckoutAttachments\" WHERE \"Id\"=@id", control))
            {
                expired.Parameters.AddWithValue("id", second.Id);
                Assert.True(Assert.IsType<bool>(await expired.ExecuteScalarAsync(ct)));
            }
            Assert.False(pending.IsCompleted);
            await held.CommitAsync(ct); released = true;
            var error = await Assert.ThrowsAsync<CheckoutDynamicFieldValidationException>(async () => await pending);
            Assert.Equal("CHECKOUT_ATTACHMENT_INVALID_STATE", error.ErrorCode);
            callerOrder.CustomerNotes = "caller-after-expired-wait"; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            await using var fresh = f.ScopedDb(f.VendorId);
            var persisted = await fresh.CheckoutAttachments.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
            Assert.Equal(2, persisted.Count);
            Assert.All(persisted, a => { Assert.Null(a.OrderId); Assert.Null(a.AttachedAtUtc); Assert.Equal(CheckoutAttachmentStatus.Finalized, a.Status); });
            Assert.Equal("caller-after-expired-wait", (await fresh.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == order.Id, ct)).CustomerNotes);
            storage.VerifyNoOtherCalls(); carts.VerifyNoOtherCalls();
        }
        finally
        {
            if (!released) await held.RollbackAsync(CancellationToken.None);
            // Drain the one owned request even when a synchronization assertion
            // fails; never leave a waiter while disposing the owned schema.
            try { await pending; } catch { /* The assertion above retains the actual failure. */ }
        }
    }

    private sealed class DriftBeforeSecondClaim(string connection, Guid id, string drift) : DbCommandInterceptor
    {
        public int ClaimsObserved { get; private set; }
        public bool BeforeSecondClaimObserved { get; private set; }
        private DateTime? changedExpiry;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (drift == "expired-after-admission" && command.CommandText.StartsWith("UPDATE \"CheckoutAttachments\"", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid value && value == id))
            {
                var actualAdmission = command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<DateTime>().Max();
                Assert.True(changedExpiry.HasValue && changedExpiry.Value > actualAdmission,
                    "Expiry drift must remain after actual bound initial admission, not an initially expired fixture.");
            }
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // Commit drift immediately before the new ownership lock, not from
            // a second connection after the claimant already holds that lock.
            // Original identities and rejection/atomicity requirements remain.
            if (!command.CommandText.Contains("/* attachment-claim-lock */", StringComparison.Ordinal)) return result;
            ClaimsObserved++;
            if (ClaimsObserved != 2) return result;
            Assert.Contains(command.Parameters.Cast<DbParameter>(), parameter => parameter.Value is Guid value && value == id);
            BeforeSecondClaimObserved = true;
            if (drift == "unchanged") return result;
            var sql = drift switch
            {
                "owner" => "UPDATE \"CheckoutAttachments\" SET \"OwnerId\"='different-owner' WHERE \"Id\"=@id",
                "object-key" => "UPDATE \"CheckoutAttachments\" SET \"ObjectKey\"=\"ObjectKey\"||'-changed' WHERE \"Id\"=@id",
                "final-key" => "UPDATE \"CheckoutAttachments\" SET \"FinalObjectKey\"=\"FinalObjectKey\"||'-changed' WHERE \"Id\"=@id",
                "state" => "UPDATE \"CheckoutAttachments\" SET \"Status\"='Deleting' WHERE \"Id\"=@id",
                "deleted" => "UPDATE \"CheckoutAttachments\" SET \"IsDeleted\"=true WHERE \"Id\"=@id",
                "expired-after-admission" => "UPDATE \"CheckoutAttachments\" SET \"ExpiresAtUtc\"=@admitted + interval '1 microsecond' WHERE \"Id\"=@id RETURNING \"ExpiresAtUtc\" < clock_timestamp()",
                _ => throw new InvalidOperationException("Unknown fixture drift")
            };
            await using var other = new NpgsqlConnection(connection); await other.OpenAsync(cancellationToken);
            await using var mutation = new NpgsqlCommand(sql, other);
            mutation.Parameters.AddWithValue("id", id);
            if (drift == "expired-after-admission")
            {
                // Initial checks have completed. Preserve the original control:
                // expiry is after actual admission but before actual SQL claim.
                // NonQueryExecuting independently checks the bound initial time.
                var admitted = DateTime.UtcNow;
                changedExpiry = new DateTime(admitted.Ticks / 10 * 10, DateTimeKind.Utc).AddTicks(10);
                mutation.Parameters.AddWithValue("admitted", admitted);
                Assert.True(Assert.IsType<bool>(await mutation.ExecuteScalarAsync(cancellationToken)));
            }
            else Assert.Equal(1, await mutation.ExecuteNonQueryAsync(cancellationToken));
            return result;
        }
    }
}
