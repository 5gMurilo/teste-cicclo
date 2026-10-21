using Cicclo.Api.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cicclo.Api.Services.GetCatalog;

/// <summary>
/// Adaptador de leitura do catálogo: uma única consulta com EXISTS por tipo de máquina operacional, sem escrita nem N+1.
/// </summary>
public sealed class PostgresServiceCatalogQuery(CiccloDbContext db)
{
    public async Task<IReadOnlyList<ServiceCatalogEntry>> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(db.Database.GetConnectionString()))
        {
            throw new DatabaseUnavailableException("Conexão com o PostgreSQL não configurada.");
        }

        var operational = ServiceAvailability.OperationalStates;
        var rows = await db.Services
            .AsNoTracking()
            .OrderBy(service => service.Code)
            .Select(service => new
            {
                service.Code,
                service.Name,
                service.MachineType,
                service.CurrentPrice,
                service.IsActive,
                HasOperationalMachine = db.Machines.Any(machine =>
                    machine.MachineType == service.MachineType && operational.Contains(machine.State)),
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            throw new DatabaseUnavailableException("Catálogo demo ausente: execute o bootstrap.");
        }

        return
        [
            .. rows.Select(row => new ServiceCatalogEntry(
                row.Code,
                row.Name,
                row.MachineType,
                row.CurrentPrice,
                row.IsActive,
                ServiceAvailability.Evaluate(row.IsActive, row.HasOperationalMachine))),
        ];
    }
}
