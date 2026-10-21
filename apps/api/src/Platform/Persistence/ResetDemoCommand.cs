using Microsoft.Extensions.Options;
using Npgsql;

namespace Cicclo.Api.Platform.Persistence;

/// <summary>
/// Modo explícito `--reset-demo --confirm-reset &lt;host:port/database&gt;`: restaura somente os dados da demo local.
/// Procedimento: parar a API (e o worker, que roda no mesmo processo), por exemplo com `docker compose stop api`, e então
/// executar o comando confirmando o alvo exato. Exige Development, Laundry:DemoMode=true e o banco cicclo em
/// localhost/127.0.0.1/::1 ou no serviço Compose postgres. A senha nunca é argumento: vem da connection string/PGPASSWORD.
/// Em uma única transação, esvazia apenas <see cref="ApplicationTables"/> (sem CASCADE) e reaplica o seed.sql; qualquer
/// falha reverte as duas etapas. Preserva schema, histórico de migrations, outras tabelas, bancos e volume; não encerra
/// sessões alheias. Não inicia listener HTTP; com `--bootstrap-demo` presente, o bootstrap tem precedência e nada é apagado.
/// </summary>
public sealed partial class ResetDemoCommand(
    DemoSeedSql seed,
    IHostEnvironment environment,
    IOptions<LaundryOptions> laundry,
    IConfiguration configuration,
    ILogger<ResetDemoCommand> logger)
{
    public const string Option = "--reset-demo";

    public const string ConfirmOption = "--confirm-reset";

    public const string DemoDatabase = "cicclo";

    public const string ApplicationName = "cicclo.reset-demo";

    /// <summary>Conjunto fechado das tabelas da aplicação; esvaziado inteiro num único TRUNCATE, sem CASCADE.</summary>
    public static readonly IReadOnlyList<string> ApplicationTables =
        ["machine_incidents", "wallet_transactions", "service_request_items", "service_requests", "machines", "services", "wallets"];

    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1", "postgres"];

    public static bool IsRequested(string[] args) => args.Contains(Option, StringComparer.Ordinal);

    /// <summary>Valor que segue `--confirm-reset`, ou null quando a opção ou o valor faltam.</summary>
    public static string? Confirmation(string[] args)
    {
        var index = Array.IndexOf(args, ConfirmOption);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Argumentos sem as opções do reset, para que não virem configuração do host.</summary>
    public static string[] WithoutOptions(string[] args)
    {
        var remaining = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], ConfirmOption, StringComparison.Ordinal))
            {
                index++;
            }
            else if (!string.Equals(args[index], Option, StringComparison.Ordinal))
            {
                remaining.Add(args[index]);
            }
        }

        return [.. remaining];
    }

    /// <summary>Alvo sanitizado resolvido pelo driver, no formato exigido pela confirmação (sem usuário nem senha).</summary>
    public static string Describe(NpgsqlConnectionStringBuilder target)
    {
        var host = target.Host ?? string.Empty;
        return $"{(host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host)}:{target.Port}/{target.Database}";
    }

    /// <summary>Somente o banco cicclo num único host local ou no serviço Compose postgres.</summary>
    public static bool IsAllowedTarget(NpgsqlConnectionStringBuilder target) =>
        string.Equals(target.Database, DemoDatabase, StringComparison.Ordinal)
        && LocalHosts.Contains(target.Host, StringComparer.OrdinalIgnoreCase);

    public async Task<int> RunAsync(string? confirmation, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() || !laundry.Value.DemoMode)
        {
            LogRefusedEnvironment(environment.EnvironmentName, laundry.Value.DemoMode);
            return BootstrapDemoCommand.Refused;
        }

        var connectionString = configuration.GetConnectionString(CiccloDbContext.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            LogRefusedConnection(CiccloDbContext.ConnectionStringName);
            return BootstrapDemoCommand.Refused;
        }

        var target = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = ApplicationName };
        var described = Describe(target);
        if (!IsAllowedTarget(target))
        {
            LogRefusedTarget(described);
            return BootstrapDemoCommand.Refused;
        }

        if (!string.Equals(confirmation, described, StringComparison.Ordinal))
        {
            LogRefusedConfirmation(described);
            return BootstrapDemoCommand.Refused;
        }

        try
        {
            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var apiSessions = await ApiSessionsAsync(transaction, cancellationToken);
            if (apiSessions > 0)
            {
                LogRefusedActiveApi(apiSessions);
                return BootstrapDemoCommand.Refused;
            }

            LogResetStarted(described);
            await using (var truncate = new NpgsqlCommand(
                $"SET LOCAL lock_timeout = '5s'; TRUNCATE TABLE {string.Join(", ", ApplicationTables.Select(table => $"\"{table}\""))};",
                connection,
                transaction))
            {
                await truncate.ExecuteNonQueryAsync(cancellationToken);
            }

            await seed.ExecuteAsync(transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogResetCompleted();
            return BootstrapDemoCommand.Succeeded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogResetFailed(exception);
            return BootstrapDemoCommand.Failed;
        }
    }

    private static async Task<long> ApiSessionsAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name = @application AND pid <> pg_backend_pid()",
            transaction.Connection,
            transaction);
        command.Parameters.AddWithValue("application", CiccloDbContext.ApiApplicationName);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Reset demo recusado: exige ambiente Development e Laundry:DemoMode=true (ambiente {Environment}, DemoMode {DemoMode}).")]
    private partial void LogRefusedEnvironment(string environment, bool demoMode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Reset demo recusado: ConnectionStrings:{ConnectionName} não configurada.")]
    private partial void LogRefusedConnection(string connectionName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Reset demo recusado: o alvo {Target} não é o banco cicclo em localhost, 127.0.0.1, ::1 ou no serviço Compose postgres.")]
    private partial void LogRefusedTarget(string target);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Reset demo recusado: confirme o alvo exato com --confirm-reset {Target}.")]
    private partial void LogRefusedConfirmation(string target);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Reset demo recusado: {Sessions} conexão(ões) ativa(s) da API/worker (cicclo.api) no alvo. Pare a API (docker compose stop api) e execute novamente; nenhuma sessão foi encerrada.")]
    private partial void LogRefusedActiveApi(long sessions);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Reset demo: esvaziando as tabelas da aplicação e reaplicando o seed.sql em {Target}.")]
    private partial void LogResetStarted(string target);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Reset demo concluído: dados iniciais restaurados pelo seed.sql.")]
    private partial void LogResetCompleted();

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Reset demo falhou e foi revertido; nenhum dado foi alterado.")]
    private partial void LogResetFailed(Exception exception);
}
