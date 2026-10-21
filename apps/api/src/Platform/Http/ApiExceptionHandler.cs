using Cicclo.Api.Platform.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cicclo.Api.Platform.Http;

/// <summary>
/// Classificação restrita das falhas não tratadas: indisponibilidade conhecida vira 503; todo o resto é 500 sanitizado.
/// </summary>
public sealed partial class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    private static readonly HashSet<string> UnavailableStates =
    [
        PostgresErrorCodes.AdminShutdown,
        PostgresErrorCodes.CrashShutdown,
        PostgresErrorCodes.CannotConnectNow,
        PostgresErrorCodes.TooManyConnections,
        PostgresErrorCodes.InvalidCatalogName,
    ];

    private static readonly string[] InitialTables = ["wallets", "services", "machines"];

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogRequestAborted(httpContext.Request.Path);
            return true;
        }

        if (IsDatabaseUnavailable(exception))
        {
            LogDatabaseUnavailable(exception, httpContext.Request.Path);
            await ApiProblems.Unavailable(httpContext).ExecuteAsync(httpContext);
            return true;
        }

        LogUnexpected(exception, httpContext.Request.Path);
        await ApiProblems.Internal(httpContext).ExecuteAsync(httpContext);
        return true;
    }

    internal static bool IsDatabaseUnavailable(Exception exception) => exception switch
    {
        DatabaseUnavailableException => true,
        PostgresException postgres => UnavailableStates.Contains(postgres.SqlState)
            || postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
            || IsMissingInitialTable(postgres),
        NpgsqlException npgsql => npgsql.IsTransient,
        DbUpdateException { InnerException: { } inner } => IsDatabaseUnavailable(inner),
        // EF Core envolve falhas transitórias do provedor em InvalidOperationException quando não há retry configurado.
        InvalidOperationException { InnerException: NpgsqlException inner } => IsDatabaseUnavailable(inner),
        _ => false,
    };

    private static bool IsMissingInitialTable(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.UndefinedTable
        && InitialTables.Any(table => exception.MessageText.Equals($"relation \"{table}\" does not exist", StringComparison.Ordinal));

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Requisição {Path} cancelada pelo cliente.")]
    private partial void LogRequestAborted(string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Banco de dados indisponível ao atender {Path}.")]
    private partial void LogDatabaseUnavailable(Exception exception, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Falha inesperada ao atender {Path}.")]
    private partial void LogUnexpected(Exception exception, string path);
}
