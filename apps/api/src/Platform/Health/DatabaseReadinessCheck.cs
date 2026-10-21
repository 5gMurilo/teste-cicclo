using System.Data.Common;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Cicclo.Api.Platform.Health;

/// <summary>
/// Pronto somente quando o PostgreSQL responde e a base demo foi preparada: wallet demo, catálogo e máquinas existem.
/// Não compara saldo, preço ou estado com os valores iniciais.
/// </summary>
public sealed class DatabaseReadinessCheck(CiccloDbContext db) : IHealthCheck
{
    public const string Tag = "ready";

    private const string Sql =
        """
        SELECT EXISTS (SELECT 1 FROM wallets WHERE id = @wallet)
           AND EXISTS (SELECT 1 FROM services)
           AND EXISTS (SELECT 1 FROM machines)
        """;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(db.Database.GetConnectionString()))
        {
            return HealthCheckResult.Unhealthy("Conexão com o PostgreSQL não configurada.");
        }

        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(Sql, (NpgsqlConnection)db.Database.GetDbConnection());
            command.Parameters.AddWithValue("wallet", DemoWallet.Id);
            var prepared = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
            return prepared
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Base demo incompleta: execute o bootstrap.");
        }
        catch (DbException exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL inacessível ou sem migrations.", exception);
        }
    }
}
