using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Infrastructure;

/// <summary>
/// Seeds synthetic, already-posted read-side snapshots through a separate plain
/// DbContext. Dashboard query tests are not financial posting tests: Production
/// SaveChanges must continue to reject InMemory financial writes.
/// </summary>
internal sealed class DashboardReadQueryFixture : ApplicationDbContext
{
    private readonly string _database;
    private readonly InMemoryDatabaseRoot _root;

    private DashboardReadQueryFixture(string database, InMemoryDatabaseRoot root)
        : base(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(database, root).Options)
    {
        _database = database;
        _root = root;
    }

    public static DashboardReadQueryFixture Create() => new(Guid.NewGuid().ToString("N"), new());

    public async Task SeedReadSnapshotAsync()
    {
        var entities = ChangeTracker.Entries()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity).ToArray();
        if (ChangeTracker.Entries().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Read fixture only accepts new synthetic snapshots.");

        ChangeTracker.Clear();
        var options = new DbContextOptionsBuilder()
            .UseInMemoryDatabase(_database, _root)
            .UseModel(Model).Options;
        await using var seeder = new DbContext(options);
        seeder.AddRange(entities);
        await seeder.SaveChangesAsync();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // These tests deliberately remove ambient filters to test the endpoint's
        // explicit tenant condition. Relational filtered-join tests are separate.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            entityType.SetQueryFilter(null);
    }
}
