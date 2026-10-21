using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Cicclo.Api.Platform.Persistence;

/// <summary>
/// Modo explícito `--bootstrap-demo`: aplica migrations e, somente depois do sucesso, executa o seed.sql.
/// Não inicia listener HTTP e nunca é chamado pelo startup normal da API.
/// </summary>
public sealed partial class BootstrapDemoCommand(
    CiccloDbContext db,
    DemoSeedSql seed,
    IHostEnvironment environment,
    IOptions<LaundryOptions> laundry,
    IConfiguration configuration,
    ILogger<BootstrapDemoCommand> logger)
{
    public const string Option = "--bootstrap-demo";

    public const int Succeeded = 0;

    public const int Failed = 1;

    public const int Refused = 2;

    public static bool IsRequested(string[] args) => args.Contains(Option, StringComparer.Ordinal);

    public static string[] WithoutOption(string[] args) => [.. args.Where(arg => !string.Equals(arg, Option, StringComparison.Ordinal))];

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() || !laundry.Value.DemoMode)
        {
            LogRefusedEnvironment(environment.EnvironmentName, laundry.Value.DemoMode);
            return Refused;
        }

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(CiccloDbContext.ConnectionStringName)))
        {
            LogRefusedConnection(CiccloDbContext.ConnectionStringName);
            return Refused;
        }

        try
        {
            LogMigrationStarted();
            await db.Database.MigrateAsync(cancellationToken);
            LogMigrationCompleted();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogMigrationFailed(exception);
            return Failed;
        }

        try
        {
            LogSeedStarted();
            await db.Database.OpenConnectionAsync(cancellationToken);
            await seed.ExecuteAsync((NpgsqlConnection)db.Database.GetDbConnection(), cancellationToken);
            LogSeedCompleted();
            return Succeeded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSeedFailed(exception);
            return Failed;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Bootstrap demo recusado: exige ambiente Development e Laundry:DemoMode=true (ambiente {Environment}, DemoMode {DemoMode}).")]
    private partial void LogRefusedEnvironment(string environment, bool demoMode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Bootstrap demo recusado: ConnectionStrings:{ConnectionName} não configurada.")]
    private partial void LogRefusedConnection(string connectionName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Bootstrap demo: migrations iniciadas.")]
    private partial void LogMigrationStarted();

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Bootstrap demo: migrations concluídas.")]
    private partial void LogMigrationCompleted();

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Bootstrap demo: migrations falharam; seed não executado.")]
    private partial void LogMigrationFailed(Exception exception);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Bootstrap demo: seed iniciado.")]
    private partial void LogSeedStarted();

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Bootstrap demo: seed concluído.")]
    private partial void LogSeedCompleted();

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Bootstrap demo: seed falhou e foi revertido.")]
    private partial void LogSeedFailed(Exception exception);
}
