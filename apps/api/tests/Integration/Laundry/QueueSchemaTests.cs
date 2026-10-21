using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Laundry;

/// <summary>
/// Schema de atribuição/FIFO da E03 em PostgreSQL 18.1 real: ticket e queued_at da fila, vínculo item↔máquina da
/// oferta com prazo, início da execução e as proteções do banco (exclusividade por máquina, estados compatíveis).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E03")]
[Trait("Task", "T1")]
public sealed class QueueSchemaTests(PostgresFixture postgres)
{
    private const string Washer2Id = "00000000-0000-4000-8000-000000000103";
    private const string BookingId = "00000000-0000-4000-8000-00000000b301";
    private const string Pending = "00000000-0000-4000-8000-00000000c301";
    private const string Pending2 = "00000000-0000-4000-8000-00000000c302";
    private const string Queued = "00000000-0000-4000-8000-00000000c303";
    private const string Offered = "00000000-0000-4000-8000-00000000c304";
    private const string Running = "00000000-0000-4000-8000-00000000c305";
    private const string Unknown = "00000000-0000-4000-8000-00000000ffff";

    /// <summary>
    /// Uma compra com cinco execuções: duas pendentes, uma na fila (ticket 3), uma oferta corrente na washer-01 (ticket 1)
    /// e uma execução na dryer-01 (ticket 2). washer-02 está livre. Tempos com microssegundos.
    /// </summary>
    private static readonly string Fixture =
        $"""
        INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES ('{Washer2Id}', 'washer-02', 'washer', 'available', now());
        INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at)
        VALUES ('{BookingId}', '{DemoData.WalletId}', '33333333-3333-4333-8333-333333333333', '{new string('c', 64)}', 96.50, '2026-10-21 09:59:00+00');
        INSERT INTO service_request_items (id, request_id, service_code, unit_price, status)
        VALUES ('{Pending}', '{BookingId}', 'wash', 18.90, 'pending_activation'), ('{Pending2}', '{BookingId}', 'wash', 18.90, 'pending_activation');
        INSERT INTO service_request_items (id, request_id, service_code, unit_price, status, queue_ticket, queued_at, machine_id, offered_at, expires_at)
        VALUES ('{Offered}', '{BookingId}', 'wash', 18.90, 'to_be_confirmed', nextval('queue_ticket_seq'), '2026-10-21 10:00:00.123456+00', '{DemoData.WasherId}', '2026-10-21 10:00:30.123456+00', '2026-10-21 10:01:30.123456+00');
        INSERT INTO service_request_items (id, request_id, service_code, unit_price, status, queue_ticket, queued_at, machine_id, offered_at, expires_at, started_at)
        VALUES ('{Running}', '{BookingId}', 'dry', 20.90, 'in_progress', nextval('queue_ticket_seq'), '2026-10-21 10:00:00+00', '{DemoData.DryerId}', '2026-10-21 10:00:20+00', '2026-10-21 10:01:20+00', '2026-10-21 10:01:00.654321+00');
        INSERT INTO service_request_items (id, request_id, service_code, unit_price, status, queue_ticket, queued_at)
        VALUES ('{Queued}', '{BookingId}', 'wash', 18.90, 'queued', nextval('queue_ticket_seq'), '2026-10-21 10:00:10.000001+00');
        UPDATE machines SET state = 'reserved', active_item_id = '{Offered}' WHERE id = '{DemoData.WasherId}';
        UPDATE machines SET state = 'running', active_item_id = '{Running}' WHERE id = '{DemoData.DryerId}';
        """;

    private const string QueueRowsSql =
        """
        SELECT line FROM (
            SELECT 1 AS rank, id::text AS k, 'item|' || id || '|' || status || '|' || coalesce(queue_ticket::text, 'null') || '|'
                || coalesce(to_char(queued_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'), 'null') || '|' || coalesce(machine_id::text, 'null') || '|'
                || coalesce(to_char(offered_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'), 'null') || '|'
                || coalesce(to_char(expires_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'), 'null') || '|'
                || coalesce(to_char(started_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'), 'null') AS line
            FROM service_request_items
            UNION ALL SELECT 2, id::text, 'machine|' || name || '|' || state || '|' || coalesce(active_item_id::text, 'null') FROM machines
        ) AS rows ORDER BY rank, k
        """;

    private static readonly string[] FixtureRows =
    [
        $"item|{Pending}|pending_activation|null|null|null|null|null|null",
        $"item|{Pending2}|pending_activation|null|null|null|null|null|null",
        $"item|{Queued}|queued|3|2026-10-21 10:00:10.000001|null|null|null|null",
        $"item|{Offered}|to_be_confirmed|1|2026-10-21 10:00:00.123456|{DemoData.WasherId}|2026-10-21 10:00:30.123456|2026-10-21 10:01:30.123456|null",
        $"item|{Running}|in_progress|2|2026-10-21 10:00:00.000000|{DemoData.DryerId}|2026-10-21 10:00:20.000000|2026-10-21 10:01:20.000000|2026-10-21 10:01:00.654321",
        $"machine|washer-01|reserved|{Offered}",
        $"machine|dryer-01|running|{Running}",
        "machine|washer-02|available|null",
    ];

    [Fact]
    public async Task Q01_MigrationOverE02DatabasePreservesPurchasesAndAddsEmptyQueueColumns()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync("BookingLedger");
        await database.SeedAsync();
        await database.ExecuteAsync(
            $"""
            UPDATE wallets SET balance = 12.20, updated_at = '2026-10-21 08:00:00+00';
            INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at)
            VALUES ('{BookingId}', '{DemoData.WalletId}', '44444444-4444-4444-8444-444444444444', '{new string('d', 64)}', 37.80, '2026-10-21 08:00:00+00');
            INSERT INTO service_request_items (id, request_id, service_code, unit_price, status)
            VALUES ('{Pending}', '{BookingId}', 'wash', 18.90, 'pending_activation'), ('{Pending2}', '{BookingId}', 'wash', 18.90, 'pending_activation');
            INSERT INTO wallet_transactions (id, wallet_id, request_id, request_item_id, kind, amount, balance_after, created_at)
            VALUES (gen_random_uuid(), '{DemoData.WalletId}', '{BookingId}', NULL, 'debit', 37.80, 12.20, '2026-10-21 08:00:00+00');
            """);
        const string financialSql =
            """
            SELECT 'wallet|' || balance || '|' || updated_at FROM wallets
            UNION ALL SELECT 'request|' || id || '|' || idempotency_key || '|' || total_amount || '|' || request_fingerprint || '|' || created_at FROM service_requests
            UNION ALL SELECT 'item|' || id || '|' || request_id || '|' || service_code || '|' || unit_price || '|' || status FROM service_request_items
            UNION ALL SELECT 'ledger|' || request_id || '|' || kind || '|' || amount || '|' || balance_after || '|' || created_at FROM wallet_transactions
            UNION ALL SELECT 'machine|' || id || '|' || state || '|' || updated_at FROM machines
            ORDER BY 1
            """;
        var before = await database.RowsAsync(financialSql);

        await database.MigrateAsync();

        Assert.Equal(before, await database.RowsAsync(financialSql));
        Assert.Equal(7, before.Count);
        Assert.Equal(TestDatabase.MigrationIds, await database.RowsAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1"));
        Assert.EndsWith("_QueueOffers", TestDatabase.MigrationIds[^1]);
        Assert.Equal(
            [
                $"item|{Pending}|pending_activation|null|null|null|null|null|null",
                $"item|{Pending2}|pending_activation|null|null|null|null|null|null",
                "machine|washer-01|available|null",
                "machine|dryer-01|available|null",
            ],
            await database.RowsAsync(QueueRowsSql));
        Assert.Equal(
            [
                "machines|active_item_id|uuid|False",
                "service_request_items|expires_at|timestamp with time zone|False",
                "service_request_items|machine_id|uuid|False",
                "service_request_items|offered_at|timestamp with time zone|False",
                "service_request_items|queue_ticket|bigint|False",
                "service_request_items|queued_at|timestamp with time zone|False",
                "service_request_items|started_at|timestamp with time zone|False",
            ],
            await database.RowsAsync(
                """
                SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull
                FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
                WHERE c.relnamespace = 'public'::regnamespace AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
                  AND ((c.relname = 'machines' AND a.attname = 'active_item_id')
                       OR (c.relname = 'service_request_items' AND a.attname IN ('queue_ticket', 'queued_at', 'machine_id', 'offered_at', 'expires_at', 'started_at')))
                ORDER BY c.relname, a.attnum
                """));
    }

    [Theory]
    [InlineData("offer on a running machine", $"UPDATE service_request_items SET status = 'to_be_confirmed', machine_id = '{DemoData.DryerId}', offered_at = '2026-10-21 10:00:40+00', expires_at = '2026-10-21 10:01:40+00' WHERE id = '{Queued}'", "ux_service_request_items_active_machine")]
    [InlineData("second offer on a reserved machine", $"UPDATE service_request_items SET status = 'to_be_confirmed', machine_id = '{DemoData.WasherId}', offered_at = '2026-10-21 10:00:40+00', expires_at = '2026-10-21 10:01:40+00' WHERE id = '{Queued}'", "ux_service_request_items_active_machine")]
    [InlineData("second execution on a running machine", $"UPDATE service_request_items SET status = 'in_progress', machine_id = '{DemoData.DryerId}', offered_at = '2026-10-21 10:00:40+00', expires_at = '2026-10-21 10:01:40+00', started_at = '2026-10-21 10:00:50+00' WHERE id = '{Queued}'", "ux_service_request_items_active_machine")]
    [InlineData("execution on a reserved machine", $"UPDATE service_request_items SET status = 'in_progress', machine_id = '{DemoData.WasherId}', offered_at = '2026-10-21 10:00:40+00', expires_at = '2026-10-21 10:01:40+00', started_at = '2026-10-21 10:00:50+00' WHERE id = '{Queued}'", "ux_service_request_items_active_machine")]
    [InlineData("one item active on two machines", $"UPDATE machines SET state = 'reserved', active_item_id = '{Offered}' WHERE id = '{Washer2Id}'", "ux_machines_active_item_id")]
    [InlineData("reused queue ticket", $"UPDATE service_request_items SET queue_ticket = 1 WHERE id = '{Queued}'", "ux_service_request_items_queue_ticket")]
    public async Task Q02_TwoActiveAssignmentsAreRefusedByTheDatabase(string scenario, string sql, string index)
    {
        var database = await FixtureAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql));

        Assert.True(error.SqlState == PostgresErrorCodes.UniqueViolation, scenario);
        Assert.Equal(index, error.ConstraintName);
        Assert.Equal(FixtureRows, await database.RowsAsync(QueueRowsSql));
    }

    [Fact]
    public async Task Q02b_DistinctMachinesAndPendingItemsDoNotCollide()
    {
        var database = await FixtureAsync();

        // Outra lavadora livre recebe a oferta do item da fila; pendentes sem ticket não colidem entre si.
        await database.ExecuteAsync(
            $"""
            UPDATE service_request_items SET status = 'to_be_confirmed', machine_id = '{Washer2Id}', offered_at = '2026-10-21 10:00:40+00', expires_at = '2026-10-21 10:01:40+00' WHERE id = '{Queued}';
            UPDATE machines SET state = 'reserved', active_item_id = '{Queued}' WHERE id = '{Washer2Id}';
            """);

        Assert.Equal(3L, await database.ScalarAsync<long>("SELECT count(*) FROM service_request_items WHERE status IN ('to_be_confirmed', 'in_progress')"));
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(*) FROM service_request_items WHERE queue_ticket IS NULL"));
    }

    [Theory]
    [InlineData($"UPDATE service_request_items SET queue_ticket = 99 WHERE id = '{Pending}'", "ck_service_request_items_queue_entry")]
    [InlineData($"UPDATE service_request_items SET queued_at = now() WHERE id = '{Pending}'", "ck_service_request_items_queue_entry")]
    [InlineData($"UPDATE service_request_items SET queue_ticket = NULL WHERE id = '{Queued}'", "ck_service_request_items_queue_entry")]
    [InlineData($"UPDATE service_request_items SET queued_at = NULL WHERE id = '{Queued}'", "ck_service_request_items_queue_entry")]
    [InlineData($"UPDATE service_request_items SET status = 'queued' WHERE id = '{Pending}'", "ck_service_request_items_queue_entry")]
    [InlineData($"UPDATE service_request_items SET machine_id = '{Washer2Id}' WHERE id = '{Queued}'", "ck_service_request_items_assignment")]
    [InlineData($"UPDATE service_request_items SET offered_at = '2026-10-21 10:00:20+00' WHERE id = '{Queued}'", "ck_service_request_items_assignment")]
    [InlineData($"UPDATE service_request_items SET expires_at = '2026-10-21 10:01:20+00' WHERE id = '{Queued}'", "ck_service_request_items_assignment")]
    [InlineData($"UPDATE machines SET state = 'available', active_item_id = NULL WHERE id = '{DemoData.WasherId}'; UPDATE service_request_items SET machine_id = NULL WHERE id = '{Offered}'", "ck_service_request_items_assignment")]
    [InlineData($"UPDATE service_request_items SET offered_at = NULL WHERE id = '{Offered}'", "ck_service_request_items_assignment")]
    [InlineData($"UPDATE service_request_items SET expires_at = NULL WHERE id = '{Offered}'", "ck_service_request_items_assignment")]
    [InlineData($"UPDATE service_request_items SET expires_at = offered_at WHERE id = '{Offered}'", "ck_service_request_items_assignment")]
    [InlineData($"UPDATE service_request_items SET offered_at = queued_at - interval '1 microsecond' WHERE id = '{Offered}'", "ck_service_request_items_assignment")]
    [InlineData($"UPDATE service_request_items SET status = 'to_be_confirmed' WHERE id = '{Queued}'", "ck_service_request_items_assignment")]
    [InlineData($"UPDATE service_request_items SET started_at = '2026-10-21 10:01:00+00' WHERE id = '{Offered}'", "ck_service_request_items_started")]
    [InlineData($"UPDATE service_request_items SET started_at = NULL WHERE id = '{Running}'", "ck_service_request_items_started")]
    [InlineData($"UPDATE service_request_items SET started_at = expires_at WHERE id = '{Running}'", "ck_service_request_items_started")]
    [InlineData($"UPDATE service_request_items SET started_at = offered_at - interval '1 microsecond' WHERE id = '{Running}'", "ck_service_request_items_started")]
    [InlineData($"UPDATE service_request_items SET status = 'in_progress' WHERE id = '{Offered}'", "ck_service_request_items_started")]
    [InlineData($"UPDATE service_request_items SET status = 'completed' WHERE id = '{Queued}'", "ck_service_request_items_status")]
    [InlineData($"UPDATE machines SET active_item_id = '{Queued}' WHERE id = '{Washer2Id}'", "ck_machines_active_item")]
    [InlineData($"UPDATE machines SET active_item_id = NULL WHERE id = '{DemoData.WasherId}'", "ck_machines_active_item")]
    [InlineData($"UPDATE machines SET state = 'out_of_service' WHERE id = '{DemoData.WasherId}'", "ck_machines_active_item")]
    [InlineData($"UPDATE machines SET state = 'reserved' WHERE id = '{Washer2Id}'", "ck_machines_active_item")]
    public async Task Q03_IncompatibleStatesTimesAndLinksAreRefused(string sql, string constraint)
    {
        var database = await FixtureAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteInTransactionAsync(database, sql));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
        Assert.Equal(FixtureRows, await database.RowsAsync(QueueRowsSql));
    }

    [Theory]
    [InlineData($"UPDATE service_request_items SET machine_id = '{Unknown}' WHERE id = '{Offered}'", PostgresErrorCodes.ForeignKeyViolation, "fk_service_request_items_machines")]
    [InlineData($"UPDATE machines SET active_item_id = '{Unknown}' WHERE id = '{DemoData.WasherId}'", PostgresErrorCodes.ForeignKeyViolation, "fk_machines_service_request_items")]
    [InlineData($"DELETE FROM machines WHERE id = '{DemoData.DryerId}'", PostgresErrorCodes.RestrictViolation, "fk_service_request_items_machines")]
    [InlineData($"DELETE FROM service_request_items WHERE id = '{Offered}'", PostgresErrorCodes.RestrictViolation, "fk_machines_service_request_items")]
    public async Task Q03b_LinksPointToExistingRowsAndBlockDeletingReferencedHistory(string sql, string sqlState, string constraint)
    {
        var database = await FixtureAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteInTransactionAsync(database, sql));

        Assert.Equal(sqlState, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
        Assert.Equal(FixtureRows, await database.RowsAsync(QueueRowsSql));
    }

    [Theory]
    [InlineData($"UPDATE service_request_items SET offered_at = queued_at WHERE id = '{Offered}'")]
    [InlineData($"UPDATE service_request_items SET expires_at = offered_at + interval '1 microsecond', started_at = NULL WHERE id = '{Offered}'")]
    [InlineData($"UPDATE service_request_items SET started_at = offered_at WHERE id = '{Running}'")]
    [InlineData($"UPDATE service_request_items SET started_at = expires_at - interval '1 microsecond' WHERE id = '{Running}'")]
    [InlineData($"UPDATE machines SET state = 'running', active_item_id = NULL WHERE id = '{Washer2Id}'")]
    [InlineData($"UPDATE machines SET state = 'running' WHERE id = '{DemoData.WasherId}'; UPDATE service_request_items SET status = 'in_progress', started_at = expires_at - interval '1 microsecond' WHERE id = '{Offered}'")]
    public async Task Q03c_BoundaryValuesInsideTheRulesAreAccepted(string sql)
    {
        var database = await FixtureAsync();

        await ExecuteInTransactionAsync(database, sql);

        Assert.NotEqual(FixtureRows, await database.RowsAsync(QueueRowsSql));
    }

    [Fact]
    public async Task Q04_TicketSequenceIsMonotonicAcrossConnectionsAndRollbackLeavesGaps()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync();

        Assert.Equal(
            "bigint|1|1|False",
            (await database.RowsAsync("SELECT data_type::text, start_value, increment_by, cycle FROM pg_sequences WHERE schemaname = 'public' AND sequencename = 'queue_ticket_seq'")).Single());
        var first = await database.ScalarAsync<long>("SELECT nextval('queue_ticket_seq')");
        long rolledBack;
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("SELECT nextval('queue_ticket_seq')", connection, transaction);
            rolledBack = (long)(await command.ExecuteScalarAsync())!;
            await transaction.RollbackAsync();
        }

        var afterRollback = await database.ScalarAsync<long>("SELECT nextval('queue_ticket_seq')");

        Assert.Equal(1L, first);
        Assert.Equal(2L, rolledBack);
        // O valor revertido não volta: o próximo ticket é maior e a lacuna (2) é normal, não quebra o FIFO.
        Assert.Equal(3L, afterRollback);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.ScalarAsync<long>("SELECT nextval('queue_ticket_seq')")));
        Assert.Equal(Enumerable.Range(4, 8).Select(value => (long)value), concurrent.Order());
    }

    [Fact]
    public async Task Q05_QueueTimesAndLinksSurviveANewConnectionAndAHostRestart()
    {
        var database = await FixtureAsync();

        Assert.Equal(FixtureRows, await database.RowsAsync(QueueRowsSql));
        var first = await ReadThroughHostAsync(database);
        var second = await ReadThroughHostAsync(database);

        Assert.Equal(first, second);
        Assert.Equal(
            [
                $"{Pending}|PendingActivation|null|null|null|null|null|null",
                $"{Pending2}|PendingActivation|null|null|null|null|null|null",
                $"{Queued}|Queued|3|2026-10-21T10:00:10.0000010Z|null|null|null|null",
                $"{Offered}|ToBeConfirmed|1|2026-10-21T10:00:00.1234560Z|{DemoData.WasherId}|2026-10-21T10:00:30.1234560Z|2026-10-21T10:01:30.1234560Z|null",
                $"{Running}|InProgress|2|2026-10-21T10:00:00.0000000Z|{DemoData.DryerId}|2026-10-21T10:00:20.0000000Z|2026-10-21T10:01:20.0000000Z|2026-10-21T10:01:00.6543210Z",
                $"{DemoData.WasherId}|Reserved|{Offered}",
                $"{DemoData.DryerId}|Running|{Running}",
                $"{Washer2Id}|Available|null",
            ],
            second);
    }

    [Fact]
    public async Task Q06_IndexesAndForeignKeysHaveTheExpectedShape()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync();

        Assert.Equal(
            [
                "machines|ix_machines_machine_type_state|CREATE INDEX ix_machines_machine_type_state ON public.machines USING btree (machine_type, state)",
                "machines|pk_machines|CREATE UNIQUE INDEX pk_machines ON public.machines USING btree (id)",
                "machines|ux_machines_active_item_id|CREATE UNIQUE INDEX ux_machines_active_item_id ON public.machines USING btree (active_item_id)",
                "machines|ux_machines_name|CREATE UNIQUE INDEX ux_machines_name ON public.machines USING btree (name)",
                "service_request_items|ix_service_request_items_queue|CREATE INDEX ix_service_request_items_queue ON public.service_request_items USING btree (service_code, status, queue_ticket)",
                "service_request_items|ix_service_request_items_request_id|CREATE INDEX ix_service_request_items_request_id ON public.service_request_items USING btree (request_id)",
                "service_request_items|ix_service_request_items_service_code|CREATE INDEX ix_service_request_items_service_code ON public.service_request_items USING btree (service_code)",
                "service_request_items|pk_service_request_items|CREATE UNIQUE INDEX pk_service_request_items ON public.service_request_items USING btree (id)",
                "service_request_items|ux_service_request_items_active_machine|CREATE UNIQUE INDEX ux_service_request_items_active_machine ON public.service_request_items USING btree (machine_id) WHERE (status = ANY (ARRAY['to_be_confirmed'::text, 'in_progress'::text]))",
                "service_request_items|ux_service_request_items_queue_ticket|CREATE UNIQUE INDEX ux_service_request_items_queue_ticket ON public.service_request_items USING btree (queue_ticket)",
            ],
            await database.RowsAsync(
                "SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('machines', 'service_request_items') ORDER BY tablename, indexname"));
        Assert.Equal(
            [
                "machines|fk_machines_service_request_items|service_request_items|r|{active_item_id}",
                "service_request_items|fk_service_request_items_machines|machines|r|{machine_id}",
            ],
            await database.RowsAsync(
                """
                SELECT conrelid::regclass::text, conname, confrelid::regclass::text, confdeltype,
                       (SELECT array_agg(attname ORDER BY attname) FROM pg_attribute WHERE attrelid = conrelid AND attnum = ANY (conkey))::text
                FROM pg_constraint
                WHERE connamespace = 'public'::regnamespace AND contype = 'f' AND conname IN ('fk_machines_service_request_items', 'fk_service_request_items_machines')
                ORDER BY 1
                """));
    }

    private static async Task<List<string>> ReadThroughHostAsync(TestDatabase database)
    {
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiccloDbContext>();
        var items = await db.BookingItems.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
        var machines = await db.Machines.AsNoTracking().OrderBy(machine => machine.Id).ToListAsync();
        return
        [
            .. items.Select(item => string.Join('|', item.Id, item.Status, Text(item.QueueTicket), Utc(item.QueuedAt), Text(item.MachineId), Utc(item.OfferedAt), Utc(item.ExpiresAt), Utc(item.StartedAt))),
            .. machines.Select(machine => string.Join('|', machine.Id, machine.State, Text(machine.ActiveItemId))),
        ];
    }

    private static string Text<T>(T? value) where T : struct => value?.ToString() ?? "null";

    private static string Utc(DateTime? value) =>
        value is null ? "null" : value.Value.Kind == DateTimeKind.Utc ? value.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : $"kind:{value.Value.Kind}";

    private static async Task ExecuteInTransactionAsync(TestDatabase database, string statements)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var statement in statements.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private async Task<TestDatabase> FixtureAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await database.ExecuteAsync(Fixture);
        return database;
    }
}
