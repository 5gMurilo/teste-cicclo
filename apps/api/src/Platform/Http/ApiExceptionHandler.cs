using System.Text.Json;
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

        // Binding inválido (RouteHandlerOptions.ThrowOnBadRequest): erro do cliente, não falha do sistema.
        if (exception is BadHttpRequestException bad && BadRequest(httpContext, bad) is { } problem)
        {
            LogBadRequest(httpContext.Request.Path, bad.StatusCode);
            await problem.ExecuteAsync(httpContext);
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

    /// <summary>
    /// JSON malformado, campo desconhecido ou tipo incorreto vira VALIDATION_ERROR com o caminho do campo (sem a mensagem
    /// do serializador, que expõe tipos .NET); parâmetro inválido, 413 e 415 usam os problemas padronizados.
    /// </summary>
    private static IResult? BadRequest(HttpContext context, BadHttpRequestException exception) => exception.StatusCode switch
    {
        StatusCodes.Status400BadRequest when exception.InnerException is JsonException json => ApiProblems.Validation(
            context,
            new Dictionary<string, string[]> { [JsonField(json.Path)] = ["JSON malformado, campo não aceito ou valor com tipo incorreto."] }),
        StatusCodes.Status400BadRequest => ApiProblems.InvalidParameter(context),
        StatusCodes.Status413PayloadTooLarge => ApiProblems.TooLarge(context),
        StatusCodes.Status415UnsupportedMediaType => ApiProblems.Unsupported(context),
        _ => null,
    };

    private static string JsonField(string? path) =>
        string.IsNullOrEmpty(path) || path == "$" ? "body" : path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path;

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

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Requisição {Path} recusada na leitura de parâmetros ou corpo ({Status}).")]
    private partial void LogBadRequest(string path, int status);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Banco de dados indisponível ao atender {Path}.")]
    private partial void LogDatabaseUnavailable(Exception exception, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Falha inesperada ao atender {Path}.")]
    private partial void LogUnexpected(Exception exception, string path);
}
