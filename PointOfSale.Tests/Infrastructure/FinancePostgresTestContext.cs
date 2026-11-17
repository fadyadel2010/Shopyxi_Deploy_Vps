using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Infrastructure;

/// <summary>
/// Isolated PostgreSQL for financial writers: InMemory cannot exercise the
/// production accounting-period/transaction guard. This fixture refuses
/// non-loopback servers and owns only randomly named finance_audit_* databases.
/// Production guards are not disabled or replaced.
/// </summary>
internal sealed class FinancePostgresTestContext : ApplicationDbContext
{
    private static readonly object Gate = new();
    private static string? _template;
    private readonly string _database;
    private bool _disposed;

    private FinancePostgresTestContext(DbContextOptions<ApplicationDbContext> options, string database) : base(options)
        => _database = database;

    private static NpgsqlConnectionStringBuilder Settings()
    {
        var value = Environment.GetEnvironmentVariable("FINANCE_TEST_POSTGRES")
            ?? throw new InvalidOperationException("FINANCE_TEST_POSTGRES must identify an isolated loopback PostgreSQL server.");
        var settings = new NpgsqlConnectionStringBuilder(value);
        if (settings.Host is not ("127.0.0.1" or "localhost") || settings.Port != 55432)
            throw new InvalidOperationException("Finance fixtures only permit loopback PostgreSQL on isolated port 55432.");
        settings.Database = "postgres";
        settings.Pooling = false;
        return settings;
    }

    public static FinancePostgresTestContext Create()
    {
        var settings = Settings();
        lock (Gate)
        {
            if (_template == null)
            {
                var template = $"finance_audit_template_{Guid.NewGuid():N}";
                using var admin = new NpgsqlConnection(settings.ConnectionString);
                admin.Open();
                using (var create = new NpgsqlCommand($"CREATE DATABASE \"{template}\"", admin)) create.ExecuteNonQuery();
                settings.Database = template;
                using (var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                           .UseNpgsql(settings.ConnectionString).Options))
                    db.Database.EnsureCreated();
                _template = template;
            }
        }
        settings.Database = "postgres";
        var database = $"finance_audit_{Guid.NewGuid():N}";
        using (var admin = new NpgsqlConnection(settings.ConnectionString))
        {
            admin.Open();
            using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\" TEMPLATE \"{_template}\"", admin);
            create.ExecuteNonQuery();
        }
        settings.Database = database;
        return new FinancePostgresTestContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(settings.ConnectionString).Options, database);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var tenantIds = ChangeTracker.Entries<AccountingOutboxMessage>()
            .Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity.TenantId).Distinct().ToList();
        if (tenantIds.Count > 0)
        {
            // Test metadata only, in the exact writer transaction. The real
            // PostgreSQL period lock, open-period check and outbox commit run.
            await using var metadata = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(Database.GetDbConnection()).Options);
            if (Database.CurrentTransaction != null)
                await metadata.Database.UseTransactionAsync(Database.CurrentTransaction.GetDbTransaction(), cancellationToken);
            foreach (var tenantId in tenantIds)
            {
                if (!await metadata.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
                    metadata.Tenants.Add(new Tenant
                    {
                        Id = tenantId, Name = "Isolated finance fixture", Slug = $"finance-{tenantId:N}",
                        Currency = "EGP", IsActive = true, Status = TenantStatus.Active, CreatedAtUtc = DateTime.UtcNow
                    });
                if (!await metadata.AccountingPeriods.IgnoreQueryFilters().AnyAsync(p => p.TenantId == tenantId, cancellationToken))
                {
                    metadata.AccountingPeriodAuthorities.Add(new AccountingPeriodAuthority { TenantId = tenantId, Revision = 1 });
                    metadata.AccountingPeriods.Add(new AccountingPeriod
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, Code = "FINANCE-TEST", Name = "Isolated open test period",
                        StartDate = new DateOnly(2020, 1, 1), EndDate = new DateOnly(2099, 12, 31),
                        Status = AccountingPeriodStatus.Open, Reason = "Isolated finance regression fixture",
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
            using var authority = new AccountingPeriodMutationScope();
            await metadata.SaveChangesAsync(cancellationToken);
        }
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override void Dispose()
    {
        base.Dispose();
        if (_disposed) return;
        _disposed = true;
        if (!_database.StartsWith("finance_audit_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup target.");
        using var admin = new NpgsqlConnection(Settings().ConnectionString);
        admin.Open();
        using var drop = new NpgsqlCommand($"DROP DATABASE \"{_database}\" WITH (FORCE)", admin);
        drop.ExecuteNonQuery();
    }
}
