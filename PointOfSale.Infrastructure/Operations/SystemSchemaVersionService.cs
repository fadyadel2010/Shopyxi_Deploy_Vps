using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;

namespace PointOfSale.Infrastructure.Operations;

public interface ISystemSchemaVersionService
{
    Task RecordAsync(
        string component,
        string version,
        object? details = null,
        CancellationToken cancellationToken = default);
}

public class SystemSchemaVersionService : ISystemSchemaVersionService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly RuntimeAdmissionPolicy _admission;
    private readonly IHostEnvironment? _environment;

    public SystemSchemaVersionService(ApplicationDbContext dbContext, RuntimeAdmissionPolicy admission,
        IHostEnvironment? environment = null)
    {
        _dbContext = dbContext;
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _environment = environment;
    }

    public async Task RecordAsync(
        string component,
        string version,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(component) || string.IsNullOrWhiteSpace(version))
        {
            if (_admission.PrivateInternalOnly || _environment?.IsProduction() == true)
                throw new InvalidOperationException("Read-only schema metadata admission requires an explicit component and version.");
            return;
        }

        var normalizedComponent = component.Trim();
        var normalizedVersion = version.Trim();

        if (_admission.PrivateInternalOnly || _environment?.IsProduction() == true)
        {
            // Observing a deployed schema must not rewrite AppliedAt/UpdatedAt,
            // revive rows, or provision metadata during production/private admission.
            var recorded = await _dbContext.Set<SystemSchemaVersion>()
                .IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Component == normalizedComponent, cancellationToken);
            if (recorded == null || recorded.IsDeleted || recorded.Version != normalizedVersion)
                throw new InvalidOperationException("Read-only schema metadata validation failed; no rows were changed.");
            return;
        }

        var detailsJson = details == null ? null : JsonSerializer.Serialize(details);

        var existing = await _dbContext.Set<SystemSchemaVersion>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Component == normalizedComponent, cancellationToken);

        if (existing == null)
        {
            await _dbContext.Set<SystemSchemaVersion>().AddAsync(
                new SystemSchemaVersion
                {
                    Id = Guid.NewGuid(),
                    Component = normalizedComponent,
                    Version = normalizedVersion,
                    AppliedAt = DateTime.UtcNow,
                    DetailsJson = detailsJson,
                    CreatedAt = DateTime.UtcNow
                },
                cancellationToken);
        }
        else
        {
            existing.Version = normalizedVersion;
            existing.AppliedAt = DateTime.UtcNow;
            existing.DetailsJson = detailsJson;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.IsDeleted = false;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
