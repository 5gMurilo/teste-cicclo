using Cicclo.Api.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cicclo.Api.Laundry.GetMachines;

/// <summary>
/// Adaptador de leitura das máquinas persistidas: uma consulta sem lock nem escrita, em ordem de nome. Estado, item ativo
/// e marca sintética vêm como estão no banco; GET não expira ofertas nem promove a fila.
/// </summary>
public sealed class PostgresMachinesQuery(CiccloDbContext db)
{
    public async Task<IReadOnlyList<Machine>> ListAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(db.Database.GetConnectionString()))
        {
            throw new DatabaseUnavailableException("Conexão com o PostgreSQL não configurada.");
        }

        var machines = await db.Machines.AsNoTracking().OrderBy(machine => machine.Name).ToListAsync(cancellationToken);
        return machines.Count == 0 ? throw new DatabaseUnavailableException("Máquinas demo ausentes: execute o bootstrap.") : machines;
    }

    /// <summary>Estado atual de uma máquina, lido depois do commit de um comando; null se ela não existe.</summary>
    public async Task<Machine?> FindAsync(Guid machineId, CancellationToken cancellationToken) =>
        await db.Machines.AsNoTracking().SingleOrDefaultAsync(machine => machine.Id == machineId, cancellationToken);
}
