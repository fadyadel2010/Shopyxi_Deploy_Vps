using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PointOfSale.API.DTOs.GeographicAdministration;
using PointOfSale.API.Services.Admin;
using PointOfSale.API.SuperAdmin.Models;
using PointOfSale.Core.Entities.Geographic;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Shipping;

namespace PointOfSale.Tests.Integration;

// No host/startup, provider operation or shared database. Uses the guarded disposable
// PostgreSQL fixture and actual service mutation boundary, not a SQL-string assertion.
public sealed class GeographicPlatformAuthorityProviderTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("stale-token")]
    [InlineData("banned")]
    [InlineData("audit-failure")]
    public async Task GlobalDraftRequiresCurrentPlatformAuthorityAndAtomicAudit(string defect)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = Guid.NewGuid();
        var accessor = await GeographicPlatformFixture.Authorize(f.Db, actor);
        if (defect == "stale-token") (await f.Db.SuperAdmins.SingleAsync()).ClearRefreshToken();
        if (defect == "banned") (await f.Db.UserGovernanceProfiles.SingleAsync()).Status = UserGovernanceStatus.Banned;
        await f.Db.SaveChangesAsync();
        var probe = new AuditBoundary(defect == "audit-failure");
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(probe).Options);
        var service = new GeographicAdministrationService(db, accessor);
        var action = () => service.CreateCatalogDraftAsync(new(1,"synthetic","US","Synthetic fixture only","test"),actor,"Owner");
        if (defect is "stale-token" or "banned") await Assert.ThrowsAsync<UnauthorizedAccessException>(action);
        else if (defect == "audit-failure") await Assert.ThrowsAsync<InvalidOperationException>(action);
        else Assert.True((await action()).Succeeded);
        Assert.Null(db.Database.CurrentTransaction);
        await using var verify = f.ScopedDb(f.VendorId);
        Assert.Equal(defect == "valid" ? 1 : 0, await verify.GeographicCatalogReleases.CountAsync());
        Assert.Equal(defect == "valid" ? 1 : 0, await verify.GeographicAudit.CountAsync());
    }

    [Fact]
    public async Task CurrentSessionAndGovernanceRowsRemainLockedThroughCatalogAndAuditCommit()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = Guid.NewGuid();var accessor = await GeographicPlatformFixture.Authorize(f.Db, actor);
        var probe = new AuditBoundary(false, pause: true);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(probe).Options);
        var action = new GeographicAdministrationService(db, accessor).CreateCatalogDraftAsync(
            new(1,"synthetic-lock","US","Synthetic fixture only","test"),actor,"Owner");
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            foreach (var (table,column) in new[] { ("SuperAdmins","Id"), ("UserGovernanceProfiles","SubjectId") })
            {
                await using var other = new NpgsqlConnection(f.ConnectionString);await other.OpenAsync();
                await using var tx = await other.BeginTransactionAsync();
                await using var command = new NpgsqlCommand($"SELECT 1 FROM \"{table}\" WHERE \"{column}\"=@actor FOR UPDATE NOWAIT",other,tx);
                command.Parameters.AddWithValue("actor",actor);
                var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
                Assert.Equal(PostgresErrorCodes.LockNotAvailable,error.SqlState);
            }
        }
        finally { probe.Release.TrySetResult(); }
        Assert.True((await action.WaitAsync(TimeSpan.FromSeconds(20))).Succeeded);
        Assert.Single(await f.Db.GeographicAudit.ToListAsync());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("stale-session")]
    [InlineData("checksum")]
    [InlineData("audit-failure")]
    public async Task ImportUsesPostgresTransactionAndPreservesExactSyntheticSourceOrNothing(string defect)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = Guid.NewGuid(); var accessor = await GeographicPlatformFixture.Authorize(f.Db, actor);
        var manifest = SyntheticManifest();
        if (defect == "stale-session") { (await f.Db.SuperAdmins.SingleAsync()).ClearRefreshToken(); await f.Db.SaveChangesAsync(); }
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString)
            .AddInterceptors(new AuditBoundary(defect == "audit-failure")).Options);
        var service = new GeographicAdministrationService(db, accessor);
        var preview = await service.PreviewCatalogImportAsync(manifest);
        Assert.True(preview.Succeeded, string.Join(",", preview.Issues.Select(i => i.Code)));
        var action = () => service.StageCatalogImportAsync(new(manifest,
            defect == "checksum" ? new string('f', 64) : preview.ManifestChecksumSha256, "Fictional provider fixture import"), actor, SuperAdminSecurityConstants.Role);
        if (defect == "stale-session") await Assert.ThrowsAsync<UnauthorizedAccessException>(action);
        else if (defect == "audit-failure") await Assert.ThrowsAsync<InvalidOperationException>(action);
        else
        {
            var result = await action(); Assert.Equal(defect == "valid", result.Succeeded);
            if (defect == "checksum") Assert.Equal("CatalogImportContentChanged", result.FailureCode);
        }
        Assert.Null(db.Database.CurrentTransaction);
        await using var verify = f.ScopedDb(f.VendorId);
        Assert.Empty(await verify.GeographicCatalogPublications.ToListAsync());
        Assert.Empty(await verify.CarrierGeographicMappingPublications.ToListAsync());
        Assert.Empty(await verify.Orders.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await verify.Shipments.IgnoreQueryFilters().ToListAsync());
        if (defect == "valid")
        {
            var release = await verify.GeographicCatalogReleases.SingleAsync();
            Assert.Equal(GeographicReleaseStatus.Draft, release.Status); Assert.Equal(actor, release.CreatedByUserId);
            Assert.Equal(manifest.Places.Select(p => p.Id).Order(), (await verify.GeographicPlaces.Select(p => p.Id).ToListAsync()).Order());
            Assert.Equal(3, await verify.GeographicCatalogReleasePlaces.CountAsync());
            Assert.Equal(2, await verify.GeographicCatalogReleaseEdges.CountAsync());
            var audit = await verify.GeographicAudit.SingleAsync();
            Assert.Equal("CatalogImportStagedV1", audit.Action); Assert.Equal(actor, audit.ActorId);
            Assert.Equal(manifest.SourceEvidenceUri, audit.EvidenceObjectKey);
            Assert.Equal("geo-import-v1:sha256:" + manifest.SourceEvidenceSha256, audit.CorrelationId);
            Assert.Equal(preview.ManifestChecksumSha256, audit.ValidationChecksumSha256);
        }
        else
        {
            Assert.Empty(await verify.GeographicCatalogReleases.ToListAsync()); Assert.Empty(await verify.GeographicPlaces.ToListAsync());
            Assert.Empty(await verify.GeographicPlaceNames.ToListAsync()); Assert.Empty(await verify.GeographicCountryProfiles.ToListAsync());
            Assert.Empty(await verify.GeographicPlaceHierarchyEdges.ToListAsync()); Assert.Empty(await verify.GeographicCatalogReleasePlaces.ToListAsync());
            Assert.Empty(await verify.GeographicCatalogReleaseEdges.ToListAsync()); Assert.Empty(await verify.GeographicAudit.ToListAsync());
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("creator")]
    [InlineData("content-drift")]
    [InlineData("stale-session")]
    [InlineData("banned")]
    [InlineData("checksum")]
    [InlineData("audit-failure")]
    public async Task IndependentReviewAndPublicationBindCurrentAuthorityContentAndAtomicHistory(string defect)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var editor = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var accessor = await GeographicPlatformFixture.Authorize(f.Db, editor);
        var stageService = new GeographicAdministrationService(f.Db, accessor); var manifest = SyntheticManifest();
        var preview = await stageService.PreviewCatalogImportAsync(manifest); Assert.True(preview.Succeeded);
        var staged = await stageService.StageCatalogImportAsync(new(manifest, preview.ManifestChecksumSha256, "Fictional import"), editor, SuperAdminSecurityConstants.Role);
        Assert.True(staged.Succeeded, staged.FailureCode); var release = staged.ResourceId!.Value;
        var read = (await stageService.ReadCatalogReleaseAsync(release))!; Assert.True(read.Validation.Succeeded, read.Validation.FailureCode);
        var checksum = read.Validation.ContentChecksumSha256!;
        var request = new CatalogValidationRequest(checksum, manifest.SourceEvidenceUri, manifest.SourceEvidenceSha256, true, "Synthetic fixture attestation, not official source review");
        Assert.Equal("FourEyesApprovalRequired", (await stageService.ValidateCatalogDraftAsync(release, request, editor, SuperAdminSecurityConstants.Role)).FailureCode);
        accessor.HttpContext = (await GeographicPlatformFixture.Authorize(f.Db, reviewer)).HttpContext;
        Assert.Equal("CatalogImportSourceEvidenceChanged", (await stageService.ValidateCatalogDraftAsync(release,
            request with { SourceEvidenceSha256 = new string('b', 64) }, reviewer, SuperAdminSecurityConstants.Role)).FailureCode);
        Assert.True((await stageService.ValidateCatalogDraftAsync(release, request, reviewer, SuperAdminSecurityConstants.Role)).Succeeded);
        f.Db.ChangeTracker.Clear();
        if (defect == "creator") accessor.HttpContext = (await GeographicPlatformFixture.Authorize(f.Db, editor)).HttpContext;
        if (defect == "content-drift") (await f.Db.GeographicPlaceNames.FirstAsync()).Name = "Committed independent synthetic change";
        if (defect == "stale-session") (await f.Db.SuperAdmins.SingleAsync(a => a.Id == reviewer)).ClearRefreshToken();
        if (defect == "banned") (await f.Db.UserGovernanceProfiles.SingleAsync(p => p.SubjectId == reviewer)).Status = UserGovernanceStatus.Banned;
        await f.Db.SaveChangesAsync();
        var auditCount = await f.Db.GeographicAudit.CountAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString)
            .AddInterceptors(new AuditBoundary(defect == "audit-failure")).Options);
        var service = new GeographicAdministrationService(db, accessor);
        var actor = defect == "creator" ? editor : reviewer;
        var action = () => service.PublishCatalogReleaseAsync(release, new("Fictional explicit publication", defect == "checksum" ? new string('c', 64) : checksum), actor, SuperAdminSecurityConstants.Role);
        if (defect is "stale-session" or "banned") await Assert.ThrowsAsync<UnauthorizedAccessException>(action);
        else if (defect == "audit-failure") await Assert.ThrowsAsync<InvalidOperationException>(action);
        else
        {
            var result = await action(); Assert.Equal(defect == "valid", result.Succeeded);
            if (defect != "valid") Assert.Equal(defect == "creator" ? "FourEyesApprovalRequired" : "CatalogContentChanged", result.FailureCode);
        }
        Assert.Null(db.Database.CurrentTransaction);
        await using var verify = f.ScopedDb(f.VendorId);
        var current = await verify.GeographicCatalogReleases.SingleAsync();
        Assert.Equal(defect == "valid" ? GeographicReleaseStatus.Published : GeographicReleaseStatus.Validated, current.Status);
        Assert.Equal(auditCount + (defect == "valid" ? 1 : 0), await verify.GeographicAudit.CountAsync());
        var review = await verify.GeographicAudit.SingleAsync(a => a.Action == "CatalogReleaseValidatedV1");
        Assert.Equal(reviewer, review.ActorId); Assert.Equal(checksum, review.ValidationChecksumSha256);
        if (defect == "valid")
        {
            var publication = await verify.GeographicCatalogPublications.SingleAsync(); Assert.Equal(release, publication.CatalogReleaseId);
            Assert.Equal(reviewer, publication.PublishedByUserId); Assert.Equal(reviewer, current.PublishedByUserId);
            Assert.Equal("CatalogReleasePublished", (await verify.GeographicAudit.SingleAsync(a => a.Action == "CatalogReleasePublished")).Action);
            Assert.False((await action()).Succeeded); Assert.Single(await verify.GeographicCatalogPublications.ToListAsync());
            Assert.Equal("CatalogReplacementAndRollbackWorkflowRequired", (await service.RollbackCatalogReleaseAsync(release, new("No false pointer switch"), reviewer, SuperAdminSecurityConstants.Role)).FailureCode);
        }
        else Assert.Empty(await verify.GeographicCatalogPublications.ToListAsync());
        Assert.Empty(await verify.CarrierGeographicMappingPublications.ToListAsync()); Assert.Empty(await verify.CarrierAccounts.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await verify.Orders.IgnoreQueryFilters().ToListAsync()); Assert.Empty(await verify.Shipments.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingPublishersSerializeAndOnlyOneCommittedHistorySurvives(bool firstAuditFails)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = timeout.Token;
        var editor = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var accessor = await GeographicPlatformFixture.Authorize(f.Db, editor);
        var stage = new GeographicAdministrationService(f.Db, accessor); var manifest = SyntheticManifest();
        var preview = await stage.PreviewCatalogImportAsync(manifest, ct); Assert.True(preview.Succeeded);
        var imported = await stage.StageCatalogImportAsync(new(manifest, preview.ManifestChecksumSha256, "Fictional competing publication"), editor, SuperAdminSecurityConstants.Role, ct);
        Assert.True(imported.Succeeded); var release = imported.ResourceId!.Value;
        accessor.HttpContext = (await GeographicPlatformFixture.Authorize(f.Db, reviewer)).HttpContext;
        var review = await stage.ReadCatalogReleaseAsync(release, ct); Assert.NotNull(review); Assert.True(review.Validation.Succeeded);
        var checksum = review.Validation.ContentChecksumSha256!;
        Assert.True((await stage.ValidateCatalogDraftAsync(release, new(checksum, manifest.SourceEvidenceUri, manifest.SourceEvidenceSha256,
            true, "Fictional review only"), reviewer, SuperAdminSecurityConstants.Role, ct)).Succeeded);
        var auditCount = await f.Db.GeographicAudit.CountAsync(ct);
        var pause = new AuditBoundary(firstAuditFails, pause: true);
        await using var firstConnection = new NpgsqlConnection(f.ConnectionString); await firstConnection.OpenAsync(ct);
        await using var secondConnection = new NpgsqlConnection(f.ConnectionString); await secondConnection.OpenAsync(ct);
        await using var control = new NpgsqlConnection(f.ConnectionString); await control.OpenAsync(ct);
        await using var firstDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(firstConnection).AddInterceptors(pause).Options);
        await using var secondDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(secondConnection).Options);
        var first = new GeographicAdministrationService(firstDb, accessor).PublishCatalogReleaseAsync(release,
            new("Fictional first publisher", checksum), reviewer, SuperAdminSecurityConstants.Role, ct);
        Task<GeographicOperationResultDto>? second = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            second = new GeographicAdministrationService(secondDb, accessor).PublishCatalogReleaseAsync(release,
                new("Fictional second publisher", checksum), reviewer, SuperAdminSecurityConstants.Role, ct);
            var blocked = false; var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !second.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", control);
                probe.Parameters.AddWithValue("holder", firstConnection.ProcessID); probe.Parameters.AddWithValue("waiter", secondConnection.ProcessID);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                await Task.Delay(20, ct);
            }
            Assert.True(blocked, "The second actual publisher must wait on the first transaction's catalog gate.");
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        }
        catch
        {
            pause.Release.TrySetResult();
            try { await first; } catch { /* Drain only; preserve the synchronization failure. */ }
            if (second != null) { try { await second; } catch { /* Same owned-request drain. */ } }
            throw;
        }
        finally { pause.Release.TrySetResult(); }
        GeographicOperationResultDto? firstResult = null; GeographicOperationResultDto? secondResult = null;
        var firstError = await Record.ExceptionAsync(async () => firstResult = await first);
        Assert.NotNull(second);
        var secondError = await Record.ExceptionAsync(async () => secondResult = await second);
        if (firstAuditFails)
        {
            Assert.IsType<InvalidOperationException>(firstError); Assert.Null(secondError); Assert.True(secondResult!.Succeeded, secondResult.FailureCode);
        }
        else
        {
            Assert.Null(firstError); Assert.True(firstResult!.Succeeded, firstResult.FailureCode);
            if (secondError == null) Assert.False(secondResult!.Succeeded);
            else
            {
                // SERIALIZABLE may retain a pre-wait snapshot. A serialization
                // refusal is safe failure, never a second successful activation.
                Exception? cause = secondError;
                while (cause is not PostgresException && cause?.InnerException != null) cause = cause.InnerException;
                Assert.Equal(PostgresErrorCodes.SerializationFailure, Assert.IsType<PostgresException>(cause).SqlState);
            }
        }
        Assert.Null(firstDb.Database.CurrentTransaction); Assert.Null(secondDb.Database.CurrentTransaction);
        await using var verify = f.ScopedDb(f.VendorId);
        Assert.Equal(GeographicReleaseStatus.Published, (await verify.GeographicCatalogReleases.SingleAsync(ct)).Status);
        var publication = await verify.GeographicCatalogPublications.SingleAsync(ct);
        Assert.Equal(firstAuditFails ? "Fictional second publisher" : "Fictional first publisher", publication.Reason);
        Assert.Equal(auditCount + 1, await verify.GeographicAudit.CountAsync(ct));
        Assert.Single(await verify.GeographicAudit.Where(a => a.Action == "CatalogReleasePublished").ToListAsync(ct));
        Assert.Empty(await verify.CarrierGeographicMappingPublications.ToListAsync(ct));
        Assert.Empty(await verify.Orders.IgnoreQueryFilters().ToListAsync(ct)); Assert.Empty(await verify.Shipments.IgnoreQueryFilters().ToListAsync(ct));
    }

    private static CatalogImportManifest SyntheticManifest()
    {
        var country = Guid.NewGuid(); var area = Guid.NewGuid(); var city = Guid.NewGuid();
        return new(1, "synthetic-pg-import-only", "US", "Fictional provider fixture, not authoritative catalog data",
            "https://synthetic.example.invalid/source", new string('a', 64), [
                new(country, "fixture-country", GeographicPlaceKind.Country, null, null, "COUNTRY", "US", null, null, [new("en", "Synthetic country")]),
                new(area, "fixture-area", GeographicPlaceKind.AdministrativeArea, country, country, "AREA", "FIXTURE-AREA", null, null, [new("en", "Synthetic area")]),
                new(city, "fixture-city", GeographicPlaceKind.Locality, country, area, "CITY", "FIXTURE-CITY", null, null, [new("en", "Synthetic city")])],
            [new(country, "US", "USA", "en", ["en"], ["AREA", "CITY"], ["AREA"], GeographicPostalCodePolicy.Required, "\\A[0-9]{5}\\z", true, false, false, false)]);
    }

    private sealed class AuditBoundary(bool fail, bool pause = false) : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<GeographicAudit>().Any(e => e.State == EntityState.Added))
            {
                if (pause) { Entered.TrySetResult();await Release.Task.WaitAsync(TimeSpan.FromSeconds(30),ct); }
                if (fail) throw new InvalidOperationException("Controlled fixture audit failure");
            }
            return result;
        }
    }
}
