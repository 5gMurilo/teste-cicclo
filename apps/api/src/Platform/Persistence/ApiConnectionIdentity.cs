using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Cicclo.Api.Platform.Persistence;

/// <summary>
/// Antes de abrir, nomeia as conexões do host (API e worker) com <see cref="CiccloDbContext.ApiApplicationName"/> quando a
/// connection string configurada não define Application Name, para que o reset-demo detecte a API ativa em
/// pg_stat_activity. Não altera a connection string configurada exposta pelo contexto nem a senha.
/// </summary>
public sealed class ApiConnectionIdentity : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Identify(connection);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Identify(connection);
        return ValueTask.FromResult(result);
    }

    public static void Identify(DbConnection connection)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        if (string.IsNullOrEmpty(builder.ApplicationName))
        {
            builder.ApplicationName = CiccloDbContext.ApiApplicationName;
            connection.ConnectionString = builder.ConnectionString;
        }
    }
}
