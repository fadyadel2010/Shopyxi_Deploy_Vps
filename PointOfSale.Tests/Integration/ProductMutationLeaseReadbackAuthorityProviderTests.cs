using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PointOfSale.API.Infrastructure.Idempotency;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Real autocommit SQL followed by an independently committed takeover before
// readback. Synthetic clock arguments avoid waits; no business product is authored.
public sealed class ProductMutationLeaseReadbackAuthorityProviderTests
{
    [Theory]
    [InlineData(-1000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    public async Task InvalidLeaseCannotReissueAnExistingOwnersFencingToken(int milliseconds)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        await using var owner = f.ScopedDb(f.VendorId);
        var request = new ProductMutationIdempotencyRequest(f.VendorId, "invalid-lease-aba", "APP2-clock", Guid.NewGuid(), null, "fictional-fingerprint");
        var service = new ProductMutationIdempotencyService(owner);
        var claim = (await service.TryClaimAsync(request)).Record!;
        var token = claim.ClaimLeaseUpdatedAtUtc!.Value;
        await using var contender = f.ScopedDb(f.VendorId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ProductMutationIdempotencyService(contender)
            .TryRecoverAbandonedClaimAsync(request, TimeSpan.FromMilliseconds(milliseconds), token));
        await using var observer = f.ScopedDb(f.VendorId);
        Assert.Equal(token, (await observer.ProductRequestIdempotencyRecords.AsNoTracking().SingleAsync()).UpdatedAt);
        Assert.Equal(ProductMutationIdempotencyCompletionKind.PersistedNow,
            (await service.TryCompleteDurablyAsync(claim, 201, new { owner = "original" })).Kind);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task NonUtcClockCannotChangeRecoveryOrRenewalAuthority(DateTimeKind kind)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        await using var owner = f.ScopedDb(f.VendorId);
        var request = new ProductMutationIdempotencyRequest(f.VendorId, "invalid-clock", "APP2-clock", Guid.NewGuid(), null, "fictional-fingerprint");
        var service = new ProductMutationIdempotencyService(owner);
        var claim = (await service.TryClaimAsync(request)).Record!;
        var token = claim.ClaimLeaseUpdatedAtUtc!.Value;
        var ambiguousTime = DateTime.SpecifyKind(token.AddMinutes(2), kind);
        await Assert.ThrowsAsync<ArgumentException>(() => service.TryRecoverAbandonedClaimAsync(request, TimeSpan.FromSeconds(1), ambiguousTime));
        await Assert.ThrowsAsync<ArgumentException>(() => service.TryRenewClaimLeaseAsync(claim, ambiguousTime));
        await using var observer = f.ScopedDb(f.VendorId);
        Assert.Equal(token, (await observer.ProductRequestIdempotencyRecords.AsNoTracking().SingleAsync()).UpdatedAt);
        Assert.Null((await observer.ProductRequestIdempotencyRecords.AsNoTracking().SingleAsync()).ResponseStatusCode);
    }

    [Fact]
    public async Task RecoveryTokenIsStrictlyIncreasingAndFormerOwnerCannotCommitAfterTakeover()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        await using var owner = f.ScopedDb(f.VendorId);
        var request = new ProductMutationIdempotencyRequest(f.VendorId, "monotone-clock", "APP2-clock", Guid.NewGuid(), null, "fictional-fingerprint");
        var service = new ProductMutationIdempotencyService(owner);
        var claim = (await service.TryClaimAsync(request)).Record!;
        var token = claim.ClaimLeaseUpdatedAtUtc!.Value;
        await using var successor = f.ScopedDb(f.VendorId);
        var current = new ProductMutationIdempotencyService(successor);
        var recovered = await current.TryRecoverAbandonedClaimAsync(request, TimeSpan.FromSeconds(1), token.AddSeconds(2));
        Assert.Equal(ProductMutationIdempotencyRecoveryKind.Recovered, recovered.Kind);
        Assert.True(recovered.Record!.ClaimLeaseUpdatedAtUtc > token);
        Assert.False(await service.TryRenewClaimLeaseAsync(claim, token.AddSeconds(3)));
        Assert.False(await service.TryWriteIncompleteClaimStateAsync(claim, "{\"owner\":\"stale\"}"));
        Assert.Equal(ProductMutationIdempotencyCompletionKind.LostOwnership,
            (await service.TryCompleteDurablyAsync(claim, 200, new { owner = "stale" })).Kind);
        Assert.Equal(ProductMutationIdempotencyCompletionKind.PersistedNow,
            (await current.TryCompleteDurablyAsync(recovered.Record, 201, new { owner = "successor" })).Kind);
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("recovery")]
    public async Task SuccessfulMutationCannotAdoptASuccessorsLeaseDuringReadback(string operation)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var request = new ProductMutationIdempotencyRequest(f.VendorId, "readback-owner", "APP2-readback", Guid.NewGuid(), null, "fictional-fingerprint");
        var firstTime = DateTime.UtcNow.AddMinutes(2); var successorTime = firstTime.AddMinutes(2);
        if (operation == "recovery")
        {
            await using var seed = f.ScopedDb(f.VendorId);
            Assert.NotNull((await new ProductMutationIdempotencyService(seed).TryClaimAsync(request)).Record);
        }
        ProductMutationIdempotencyRecoveryResult? successor = null;
        var interpose = new TakeOverAfterMutation(operation, async () =>
        {
            await using var db = f.ScopedDb(f.VendorId);
            successor = await new ProductMutationIdempotencyService(db).TryRecoverAbandonedClaimAsync(request, TimeSpan.FromSeconds(1), successorTime);
            Assert.Equal(ProductMutationIdempotencyRecoveryKind.Recovered, successor.Kind); Assert.NotNull(successor.Record);
        });
        await using var first = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(interpose).Options, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        var service = new ProductMutationIdempotencyService(first);
        if (operation == "insert")
        {
            var result = await service.TryClaimAsync(request);
            Assert.Null(result.Record); Assert.Equal(ProductMutationIdempotencyReplayKind.InProgress, result.Replay!.Kind);
        }
        else
        {
            var result = await service.TryRecoverAbandonedClaimAsync(request, TimeSpan.FromSeconds(1), firstTime);
            Assert.Equal(ProductMutationIdempotencyRecoveryKind.NotRecoverable, result.Kind); Assert.Null(result.Record);
        }
        Assert.True(interpose.Executed); Assert.NotNull(successor);
        await using var owner = f.ScopedDb(f.VendorId);
        var ownerService = new ProductMutationIdempotencyService(owner);
        var completion = await ownerService.TryCompleteDurablyAsync(successor!.Record!, 201, new { owner = "successor" });
        Assert.Equal(ProductMutationIdempotencyCompletionKind.PersistedNow, completion.Kind);
        var saved = await owner.ProductRequestIdempotencyRecords.AsNoTracking().SingleAsync();
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
        await using var receipt = new FileStream(Path.Combine(output, "product-lease-readback-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(receipt, new { operation, interpose.Executed, SuccessorKind = successor.Kind.ToString(),
            CompletionKind = completion.Kind.ToString(), saved.ResponseStatusCode, RowCount = await owner.ProductRequestIdempotencyRecords.CountAsync(),
            Scope = "Actual insert/recovery/readback fencing only; no product, outbox, host, SQL Server or SQLite proof" });
        Assert.Equal(201, saved.ResponseStatusCode); Assert.Contains("successor", saved.ResponseJson!);
        Assert.Empty(await owner.OutboxMessages.ToListAsync());
    }

    private sealed class TakeOverAfterMutation(string operation, Func<Task> takeOver) : DbCommandInterceptor
    {
        internal bool Executed { get; private set; }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result,
            CancellationToken cancellationToken = default)
        {
            if (!Executed && result == 1 && command.CommandText.Contains("\"ProductRequestIdempotency\"", StringComparison.Ordinal) &&
                command.CommandText.TrimStart().StartsWith(operation == "insert" ? "INSERT" : "UPDATE", StringComparison.Ordinal))
            {
                Assert.Null(command.Transaction); Executed = true; await takeOver();
            }
            return result;
        }
    }
}
