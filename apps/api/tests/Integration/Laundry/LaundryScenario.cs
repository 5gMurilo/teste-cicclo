using System.Globalization;
using Cicclo.Api.Laundry;
using Cicclo.Api.Platform;
using Cicclo.Api.Tests.Support;
using Cicclo.Api.Wallet;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Cicclo.Api.Tests.Integration.Laundry;

/// <summary>
/// Apoio dos cenários de fila/oferta em PostgreSQL real: compras com itens pendentes criadas por SQL (sem débito,
/// porque a fila não depende do dinheiro), transações completas como um caso de uso as abriria e sinais do próprio
/// banco (espera de lock em pg_stat_activity, NOWAIT) para fixar interleavings sem pausas fixas.
/// </summary>
internal static class LaundryScenario
{
    public const string WasherId = DemoData.WasherId;
    public const string DryerId = DemoData.DryerId;
    public const string Washer2Id = "00000000-0000-4000-8000-000000000103";
    public static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(20);
    public static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Itens e máquinas, com tempos em microssegundos UTC; ordem estável por id.</summary>
    public const string StateSql =
        """
        SELECT line FROM (
            SELECT 1 AS rank, id::text AS k, 'item|' || id || '|' || status || '|' || coalesce(queue_ticket::text, 'null') || '|'
                || coalesce(to_char(queued_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US'), 'null') || '|' || coalesce(machine_id::text, 'null') || '|'
                || coalesce(to_char(offered_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US'), 'null') || '|'
                || coalesce(to_char(expires_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US'), 'null') || '|'
                || coalesce(to_char(started_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US'), 'null') AS line
            FROM service_request_items
            UNION ALL SELECT 2, id::text, 'machine|' || name || '|' || state || '|' || coalesce(active_item_id::text, 'null') || '|'
                || to_char(updated_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM machines
            UNION ALL SELECT 3, '', 'wallet|' || balance || '|' || (SELECT count(*) FROM wallet_transactions) FROM wallets
        ) AS rows ORDER BY rank, k
        """;

    public static FakeTimeProvider Clock(DateTimeOffset? start = null) => new(start ?? T0);

    public static QueueCoordinator Coordinator(TimeProvider time, int timeoutSeconds = 60) =>
        new(time, Options.Create(new LaundryOptions { ConfirmationTimeoutSeconds = timeoutSeconds }));

    public static PostgresLaundrySessions Sessions(TestDatabase database, string applicationName) =>
        new(database.ContextFactory(applicationName));

    public static async Task<TestDatabase> ReadyAsync(PostgresFixture postgres, string? fixture = null)
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        if (fixture is not null)
        {
            await database.ExecuteAsync(fixture);
        }

        return database;
    }

    /// <summary>Uma compra com os itens pendentes indicados (ids explícitos permitem fixar a ordem por id).</summary>
    public static async Task InsertPendingAsync(TestDatabase database, params (Guid Id, string Code)[] items)
    {
        var bookingId = Guid.NewGuid();
        var total = items.Sum(item => item.Code == "wash" ? 18.90m : 20.90m);
        var values = string.Join(", ", items.Select(item =>
            string.Create(CultureInfo.InvariantCulture, $"('{item.Id}', '{bookingId}', '{item.Code}', {(item.Code == "wash" ? "18.90" : "20.90")}, 'pending_activation')")));
        await database.ExecuteAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at)
                VALUES ('{bookingId}', '{DemoData.WalletId}', gen_random_uuid(), repeat('f', 64), {total}, now());
                INSERT INTO service_request_items (id, request_id, service_code, unit_price, status) VALUES {values};
                """));
    }

    public static Task<Guid> PendingAsync(TestDatabase database, string code = "wash") => PendingWithIdAsync(database, Guid.NewGuid(), code);

    public static async Task<Guid> PendingWithIdAsync(TestDatabase database, Guid id, string code = "wash")
    {
        await InsertPendingAsync(database, (id, code));
        return id;
    }

    /// <summary>Transação completa de ativação: wallet (mutex), coordenador e commit.</summary>
    public static async Task<ActivationResult> ActivateAsync(ILaundrySessions sessions, QueueCoordinator coordinator, Guid itemId, bool lockWallet = true)
    {
        await using var session = await sessions.OpenAsync(CancellationToken.None);
        if (lockWallet)
        {
            await session.LockWalletAsync(DemoWallet.Id, CancellationToken.None);
        }

        var result = await coordinator.ActivateAsync(session, itemId, CancellationToken.None);
        await session.CommitAsync();
        return result;
    }

    public static async Task<IReadOnlyList<MachineOffer>> PromoteAsync(ILaundrySessions sessions, QueueCoordinator coordinator, MachineType type)
    {
        await using var session = await sessions.OpenAsync(CancellationToken.None);
        await session.LockWalletAsync(DemoWallet.Id, CancellationToken.None);
        var offers = await coordinator.PromoteAsync(session, type, CancellationToken.None);
        await session.CommitAsync();
        return offers;
    }

    public static string Named(TestDatabase database, string applicationName) =>
        new NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = applicationName }.ConnectionString;

    /// <summary>Conexão com transação aberta que segura locks até ser descartada (rollback) ou confirmada.</summary>
    public static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldAsync(TestDatabase database, string applicationName, string sql)
    {
        var connection = new NpgsqlConnection(Named(database, applicationName));
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        return (connection, transaction);
    }

    /// <summary>Sinal do PostgreSQL: pelo menos <paramref name="count"/> conexões com o nome esperam lock de linha.</summary>
    public static async Task WaitUntilWaitingOnLockAsync(TestDatabase database, string applicationName, int count = 1)
    {
        var deadline = DateTime.UtcNow + SignalTimeout;
        while (await database.ScalarAsync<long>(
                   $"SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name = '{applicationName}' AND wait_event_type = 'Lock'") < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"A conexão {applicationName} não ficou aguardando lock.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Tenta bloquear as linhas sem esperar: false quando outra transação já as segura (55P03).</summary>
    public static async Task<bool> CanLockNowAsync(TestDatabase database, string lockSql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(lockSql + " NOWAIT", connection, transaction);
        try
        {
            await command.ExecuteNonQueryAsync();
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return false;
        }
    }

    public static string Micros(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
}
