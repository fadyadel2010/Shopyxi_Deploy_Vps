using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Data.Seeding;

namespace PointOfSale.Tests.Integration;

// Actual production seeder on real PostgreSQL 18, isolated model-derived fixture schema.
// This does not certify application startup, production permissions or migrations.
public sealed class AffiliateTaxonomySeederProviderTests
{
    [Fact]
    public async Task ReplayPreservesOperatorFieldsUnrelatedRowsAndProductPoliciesExactly()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        await Seed(f.Db);
        await AssertBaseline(f.Db);
        var edited = await f.Db.AffiliateCategories.SingleAsync(c => c.Id == AffiliateTaxonomyCatalog.Entries[0].Id);
        edited.UpdateName("Operator renamed category"); edited.UpdateSlug("operator-slug");
        edited.UpdateDisplayOrder(91); edited.Deactivate(); edited.IsDeleted = true;
        var extra = AffiliateCategory.CreateMainCategory("Operator managed", "operator-managed");
        var merchantCategory = new Category { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Merchant category" };
        var product = new Product { Id = Guid.NewGuid(), TenantId = f.VendorId, CategoryId = merchantCategory.Id, Name = "Fixture", SKU = "TAXONOMY" };
        var policy = AffiliateProductPolicy.Create(f.VendorId, product.Id, .1m, categoryId: edited.Id);
        f.Db.AddRange(extra, merchantCategory, product, policy); await f.Db.SaveChangesAsync();
        var before = await Rows(f.Db, "AffiliateCategories");
        var policies = await Rows(f.Db, "AffiliateProductPolicies");
        await Seed(f.Db); await Seed(f.Db);
        Assert.Equal(before, await Rows(f.Db, "AffiliateCategories"));
        Assert.Equal(policies, await Rows(f.Db, "AffiliateProductPolicies"));
        Assert.Null(f.Db.Model.FindEntityType(typeof(AffiliateCategory))!.FindProperty("TenantId"));
    }

    [Theory]
    [InlineData("root-slug")]
    [InlineData("child-slug")]
    [InlineData("child-parent-name")]
    public async Task ConflictingOperatorIdentityAbortsWholePassWithoutAttachingCanonicalChildren(string collision)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var entry = collision == "root-slug" ? AffiliateTaxonomyCatalog.Roots[3]
            : AffiliateTaxonomyCatalog.Entries.First(e => e.ParentId.HasValue);
        if (entry.ParentId is Guid parent)
        {
            var root = AffiliateTaxonomyCatalog.Entries.Single(e => e.Id == parent);
            var row = AffiliateCategory.CreateMainCategory(root.Name, root.Slug); row.Id = root.Id;
            f.Db.Add(row);
        }
        var blocker = collision == "child-parent-name"
            ? AffiliateCategory.CreateSubcategory(entry.Name, "operator-collision", entry.ParentId!.Value)
            : AffiliateCategory.CreateMainCategory("Slug blocker", entry.Slug);
        f.Db.Add(blocker); await f.Db.SaveChangesAsync();
        var before = await Rows(f.Db, "AffiliateCategories");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Seed(f.Db));
        Assert.Contains(entry.Id.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains(blocker.Id.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await Rows(f.Db, "AffiliateCategories"));
        Assert.False(await f.Db.AffiliateCategories.AnyAsync(c => c.Id == entry.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptCanonicalPlacementIsRejectedAndAllOtherNewRowsRollback(bool corruptLevel)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var parent = AffiliateCategory.CreateMainCategory("Operator parent", "operator-parent");
        parent.Id = Guid.NewGuid();
        var entry = corruptLevel ? AffiliateTaxonomyCatalog.Roots[0]
            : AffiliateTaxonomyCatalog.Entries.First(e => e.ParentId.HasValue);
        var corrupt = AffiliateCategory.CreateSubcategory(entry.Name, entry.Slug, parent.Id); corrupt.Id = entry.Id;
        f.Db.AddRange(parent, corrupt); await f.Db.SaveChangesAsync();
        var before = await Rows(f.Db, "AffiliateCategories");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Seed(f.Db));
        Assert.Contains("identity is corrupted", error.Message, StringComparison.Ordinal);
        Assert.Contains(entry.Id.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await Rows(f.Db, "AffiliateCategories"));
    }

    [Fact]
    public async Task LateDatabaseFailureRollsBackParentsAndChildrenThenCleanRetrySucceeds()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var last = AffiliateTaxonomyCatalog.Entries[^1];
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TABLE fixture_taxonomy_control(source_id uuid PRIMARY KEY,gate bigint)");
        await f.Db.Database.ExecuteSqlAsync($"INSERT INTO fixture_taxonomy_control(source_id) VALUES({last.Id})");
        await f.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fixture_taxonomy_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Id"=(SELECT source_id FROM fixture_taxonomy_control) THEN
                RAISE EXCEPTION 'fixture late seed failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fixture_taxonomy_failure BEFORE INSERT ON "AffiliateCategories"
                FOR EACH ROW EXECUTE FUNCTION fixture_taxonomy_failure();
            """);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => Seed(f.Db));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Empty(await f.Db.AffiliateCategories.ToListAsync());
        await f.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fixture_taxonomy_failure ON \"AffiliateCategories\"");
        await Seed(f.Db); await AssertBaseline(f.Db);
    }

    [Fact]
    public async Task TwoSeedTransactionsActuallyContendAndConvergeToOneCompleteCanonicalHierarchy()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        await using var blocker = new NpgsqlConnection(f.ConnectionString); await blocker.OpenAsync();
        var key = Random.Shared.NextInt64(1, long.MaxValue);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", blocker))
        { hold.Parameters.AddWithValue("key", key); await hold.ExecuteNonQueryAsync(); }
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TABLE fixture_taxonomy_control(source_id uuid PRIMARY KEY,gate bigint)");
        await f.Db.Database.ExecuteSqlAsync($"INSERT INTO fixture_taxonomy_control(source_id,gate) VALUES({AffiliateTaxonomyCatalog.Entries[0].Id},{key})");
        await f.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fixture_taxonomy_pause() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Id"=(SELECT source_id FROM fixture_taxonomy_control) THEN
                PERFORM pg_advisory_xact_lock((SELECT gate FROM fixture_taxonomy_control)); END IF; RETURN NEW; END $$;
            CREATE TRIGGER fixture_taxonomy_pause AFTER INSERT ON "AffiliateCategories"
                FOR EACH ROW EXECUTE FUNCTION fixture_taxonomy_pause();
            """);
        await using var first = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await using var second = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await first.Database.OpenConnectionAsync(); await second.Database.OpenConnectionAsync();
        var firstPid = ((NpgsqlConnection)first.Database.GetDbConnection()).ProcessID;
        var secondPid = ((NpgsqlConnection)second.Database.GetDbConnection()).ProcessID;
        var a = Seed(first); Task? b = null;
        try
        {
            await AssertWaiting(f.Db, firstPid, "advisory");
            b = Seed(second);
            await AssertWaiting(f.Db, secondPid, "transactionid");
            Assert.False(a.IsCompleted); Assert.False(b.IsCompleted);
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", blocker);
            release.Parameters.AddWithValue("key", key); await release.ExecuteNonQueryAsync();
            await Task.WhenAll(b is null ? [a] : new[] { a, b }).WaitAsync(TimeSpan.FromSeconds(30));
        }
        await AssertBaseline(f.Db);
        var before = await Rows(f.Db, "AffiliateCategories"); await Seed(f.Db);
        Assert.Equal(before, await Rows(f.Db, "AffiliateCategories"));
    }

    private static Task Seed(ApplicationDbContext db) => new AffiliateTaxonomySeeder(db, NullLogger<AffiliateTaxonomySeeder>.Instance).SeedAsync();

    private static async Task AssertBaseline(ApplicationDbContext db)
    {
        var rows = await db.AffiliateCategories.AsNoTracking().ToDictionaryAsync(c => c.Id);
        Assert.Equal(AffiliateTaxonomyCatalog.Entries.Count, rows.Count);
        foreach (var entry in AffiliateTaxonomyCatalog.Entries)
        {
            Assert.True(rows.TryGetValue(entry.Id, out var row));
            Assert.Equal(entry.ParentId, row.ParentId); Assert.Equal(entry.Level, row.Level);
            Assert.Equal(entry.Name, row.Name); Assert.Equal(entry.Slug, row.Slug);
            Assert.Equal(entry.DisplayOrder, row.DisplayOrder); Assert.True(row.IsActive); Assert.False(row.IsDeleted);
        }
    }

    private static async Task<string[]> Rows(ApplicationDbContext db, string table)
    {
        // Only the two fixture tables explicitly admitted here; never user-provided SQL.
        Assert.Contains(table, new[] { "AffiliateCategories", "AffiliateProductPolicies" });
        await using var command = new NpgsqlCommand($"SELECT to_jsonb(r)::text FROM \"{table}\" r ORDER BY \"Id\"", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(); var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private static async Task AssertWaiting(ApplicationDbContext db, int pid, string waitEvent)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var probe = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event_type='Lock' AND wait_event=@event)", (NpgsqlConnection)db.Database.GetDbConnection());
            probe.Parameters.AddWithValue("pid", pid); probe.Parameters.AddWithValue("event", waitEvent);
            if (await probe.ExecuteScalarAsync() is true) return;
            await Task.Delay(20);
        }
        Assert.Fail($"PostgreSQL backend {pid} did not visibly contend on {waitEvent}.");
    }
}
