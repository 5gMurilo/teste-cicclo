using Cicclo.Api.Tests.Support;
using Npgsql;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Bookings;

/// <summary>Schema financeiro da E02: booking, itens com preço histórico e débito único no ledger.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E02")]
[Trait("Task", "T1")]
public sealed class BookingSchemaTests(PostgresFixture postgres)
{
    private const string BookingId = "00000000-0000-4000-8000-00000000b001";
    private const string ItemA = "00000000-0000-4000-8000-00000000c001";
    private const string ItemB = "00000000-0000-4000-8000-00000000c002";
    private const string DebitId = "00000000-0000-4000-8000-00000000d001";
    private const string Key = "11111111-1111-4111-8111-111111111111";
    private static readonly string Fingerprint = new('a', 64);
    private const string LedgerMigration = "BookingLedger";

    private static readonly string BookingWithDebit =
        $"""
        INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at)
        VALUES ('{BookingId}', '{DemoData.WalletId}', '{Key}', '{Fingerprint}', 37.80, now());
        INSERT INTO service_request_items (id, request_id, service_code, unit_price, status)
        VALUES ('{ItemA}', '{BookingId}', 'wash', 18.90, 'pending_activation'),
               ('{ItemB}', '{BookingId}', 'wash', 18.90, 'pending_activation');
        INSERT INTO wallet_transactions (id, wallet_id, request_id, request_item_id, kind, amount, balance_after, created_at)
        VALUES ('{DebitId}', '{DemoData.WalletId}', '{BookingId}', NULL, 'debit', 37.80, 12.20, now());
        """;

    private const string CountsSql =
        """
        SELECT (SELECT count(*) FROM wallets), (SELECT count(*) FROM services), (SELECT count(*) FROM service_requests),
               (SELECT count(*) FROM service_request_items), (SELECT count(*) FROM wallet_transactions)
        """;

    private const string NewTablesColumnsSql =
        """
        SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        WHERE c.relnamespace = 'public'::regnamespace
          AND c.relname IN ('service_requests', 'service_request_items', 'wallet_transactions')
          AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
        ORDER BY c.relname, a.attnum
        """;

    private const string ConstraintsSql =
        "SELECT conrelid::regclass::text, conname, contype FROM pg_constraint WHERE connamespace = 'public'::regnamespace ORDER BY 1, 2";

    private const string IndexesSql =
        "SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' ORDER BY tablename, indexname";

    [Fact]
    public async Task F01_MigrationOverE01DatabaseAddsFinancialTablesWithoutLosingData()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync("InitialSchema");
        await database.SeedAsync();
        await database.ExecuteAsync(
            $"""
            UPDATE wallets SET balance = 31.10 WHERE id = '{DemoData.WalletId}';
            UPDATE services SET current_price = 25.00 WHERE code = 'wash';
            UPDATE machines SET state = 'out_of_service' WHERE id = '{DemoData.WasherId}';
            """);
        var before = await DemoData.SnapshotAsync(database);
        var stamps = await database.RowsAsync("SELECT created_at, updated_at FROM wallets UNION ALL SELECT updated_at, updated_at FROM machines ORDER BY 1");

        // Prova a migration da E02 sobre a base E01; as migrations posteriores têm testes próprios (Integration/Laundry).
        await database.MigrateAsync(LedgerMigration);

        Assert.Equal(before, await DemoData.SnapshotAsync(database));
        Assert.Contains($"wallet|{DemoData.WalletId}|BRL|31.10", before);
        Assert.Equal(stamps, await database.RowsAsync("SELECT created_at, updated_at FROM wallets UNION ALL SELECT updated_at, updated_at FROM machines ORDER BY 1"));
        Assert.Equal(TestDatabase.MigrationIdsThrough(LedgerMigration), await database.RowsAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1"));
        Assert.Equal(
            [
                "service_request_items|id|uuid|True",
                "service_request_items|request_id|uuid|True",
                "service_request_items|service_code|text|True",
                "service_request_items|unit_price|numeric(12,2)|True",
                "service_request_items|status|text|True",
                "service_requests|id|uuid|True",
                "service_requests|wallet_id|uuid|True",
                "service_requests|idempotency_key|uuid|True",
                "service_requests|request_fingerprint|text|True",
                "service_requests|total_amount|numeric(12,2)|True",
                "service_requests|created_at|timestamp with time zone|True",
                "wallet_transactions|id|uuid|True",
                "wallet_transactions|wallet_id|uuid|True",
                "wallet_transactions|request_id|uuid|True",
                "wallet_transactions|request_item_id|uuid|False",
                "wallet_transactions|kind|text|True",
                "wallet_transactions|amount|numeric(12,2)|True",
                "wallet_transactions|balance_after|numeric(12,2)|True",
                "wallet_transactions|created_at|timestamp with time zone|True",
            ],
            await database.RowsAsync(NewTablesColumnsSql));
        Assert.Equal("1|2|0|0|0", (await database.RowsAsync(CountsSql)).Single());
    }

    [Fact]
    public async Task F02_ReapplyingAllMigrationsIsIdempotentAndKeepsRestrictiveForeignKeys()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync();
        var constraints = await database.RowsAsync(ConstraintsSql);
        var indexes = await database.RowsAsync(IndexesSql);

        await database.MigrateAsync();

        Assert.Equal(constraints, await database.RowsAsync(ConstraintsSql));
        Assert.Equal(indexes, await database.RowsAsync(IndexesSql));
        Assert.Equal(TestDatabase.MigrationIds, await database.RowsAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1"));
        Assert.Equal(
            [
                "machine_incidents|fk_machine_incidents_machines|machines|r",
                "machine_incidents|fk_machine_incidents_service_request_items|service_request_items|r",
                "machines|fk_machines_service_request_items|service_request_items|r",
                "service_request_items|fk_service_request_items_machines|machines|r",
                "service_request_items|fk_service_request_items_service_requests|service_requests|r",
                "service_request_items|fk_service_request_items_services|services|r",
                "service_requests|fk_service_requests_wallets|wallets|r",
                "wallet_transactions|fk_wallet_transactions_service_request_items|service_request_items|r",
                "wallet_transactions|fk_wallet_transactions_service_requests|service_requests|r",
                "wallet_transactions|fk_wallet_transactions_wallets|wallets|r",
            ],
            await database.RowsAsync(
                "SELECT conrelid::regclass::text, conname, confrelid::regclass::text, confdeltype FROM pg_constraint WHERE connamespace = 'public'::regnamespace AND contype = 'f' ORDER BY 1, 2"));
        Assert.Contains(
            "wallet_transactions|ux_wallet_transactions_request_debit|CREATE UNIQUE INDEX ux_wallet_transactions_request_debit ON public.wallet_transactions USING btree (request_id) WHERE (kind = 'debit'::text)",
            indexes);
        Assert.Contains(
            "service_requests|ux_service_requests_wallet_idempotency_key|CREATE UNIQUE INDEX ux_service_requests_wallet_idempotency_key ON public.service_requests USING btree (wallet_id, idempotency_key)",
            indexes);
    }

    [Theory]
    [InlineData("UPDATE service_requests SET total_amount = 0.00", PostgresErrorCodes.CheckViolation, "ck_service_requests_total_amount_positive")]
    [InlineData("UPDATE service_requests SET request_fingerprint = 'not-a-sha256'", PostgresErrorCodes.CheckViolation, "ck_service_requests_request_fingerprint_sha256")]
    [InlineData("UPDATE service_requests SET request_fingerprint = upper(request_fingerprint)", PostgresErrorCodes.CheckViolation, "ck_service_requests_request_fingerprint_sha256")]
    [InlineData("UPDATE service_requests SET request_fingerprint = request_fingerprint || 'a'", PostgresErrorCodes.CheckViolation, "ck_service_requests_request_fingerprint_sha256")]
    [InlineData("UPDATE service_request_items SET unit_price = -18.90", PostgresErrorCodes.CheckViolation, "ck_service_request_items_unit_price_positive")]
    [InlineData("UPDATE service_request_items SET unit_price = 0.00", PostgresErrorCodes.CheckViolation, "ck_service_request_items_unit_price_positive")]
    [InlineData("UPDATE service_request_items SET status = 'expired', queue_ticket = nextval('queue_ticket_seq'), queued_at = now()", PostgresErrorCodes.CheckViolation, "ck_service_request_items_status")]
    [InlineData("UPDATE wallet_transactions SET amount = 0.00", PostgresErrorCodes.CheckViolation, "ck_wallet_transactions_amount_positive")]
    [InlineData("UPDATE wallet_transactions SET balance_after = -0.01", PostgresErrorCodes.CheckViolation, "ck_wallet_transactions_balance_after_non_negative")]
    [InlineData("UPDATE wallet_transactions SET kind = 'credit'", PostgresErrorCodes.CheckViolation, "ck_wallet_transactions_kind")]
    [InlineData($"UPDATE wallet_transactions SET request_item_id = '{ItemA}'", PostgresErrorCodes.CheckViolation, "ck_wallet_transactions_debit_without_item")]
    [InlineData("UPDATE wallet_transactions SET amount = 10000000000.00", PostgresErrorCodes.NumericValueOutOfRange, null)]
    [InlineData("UPDATE service_requests SET total_amount = 10000000000.00", PostgresErrorCodes.NumericValueOutOfRange, null)]
    public async Task F03_FinancialValuesAndTypesAreProtected(string sql, string sqlState, string? constraint)
    {
        var database = await BookedAsync();
        var before = await database.RowsAsync(FinancialRowsSql);

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql));

        Assert.Equal(sqlState, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
        Assert.Equal(before, await database.RowsAsync(FinancialRowsSql));
        Assert.Equal(
            [$"{BookingId}|37.80|{Fingerprint}|", "wash|18.90|pending_activation|", "wash|18.90|pending_activation|", "debit|37.80|12.20|"],
            before);
    }

    [Fact]
    public async Task F04_DuplicateIdempotencyKeyForTheSameWalletIsRejected()
    {
        var database = await BookedAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            $"INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at) VALUES (gen_random_uuid(), '{DemoData.WalletId}', '{Key}', '{new string('b', 64)}', 18.90, now());"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ux_service_requests_wallet_idempotency_key", error.ConstraintName);
        Assert.Equal("1|2|1|2|1", (await database.RowsAsync(CountsSql)).Single());

        // A unicidade é por wallet: outra wallet pode usar a mesma chave.
        await database.ExecuteAsync(
            $"""
            INSERT INTO wallets (id, currency, balance, created_at, updated_at) VALUES ('00000000-0000-4000-8000-000000000002', 'BRL', 10.00, now(), now());
            INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at) VALUES (gen_random_uuid(), '00000000-0000-4000-8000-000000000002', '{Key}', '{Fingerprint}', 18.90, now());
            """);
        Assert.Equal(2L, await database.ScalarAsync<long>($"SELECT count(*) FROM service_requests WHERE idempotency_key = '{Key}'"));
    }

    [Fact]
    public async Task F05_SecondDebitForTheSameBookingIsRejected()
    {
        var database = await BookedAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            $"INSERT INTO wallet_transactions (id, wallet_id, request_id, request_item_id, kind, amount, balance_after, created_at) VALUES (gen_random_uuid(), '{DemoData.WalletId}', '{BookingId}', NULL, 'debit', 37.80, 0.00, now());"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ux_wallet_transactions_request_debit", error.ConstraintName);
        Assert.Equal([$"debit|37.80|12.20|"], await database.RowsAsync("SELECT kind, amount, balance_after, request_item_id FROM wallet_transactions"));
    }

    [Theory]
    [InlineData("DELETE FROM services WHERE code = 'wash'", "fk_service_request_items_services")]
    [InlineData("DELETE FROM wallet_transactions; DELETE FROM service_requests", "fk_service_request_items_service_requests")]
    [InlineData("DELETE FROM service_request_items; DELETE FROM service_requests", "fk_wallet_transactions_service_requests")]
    [InlineData($"DELETE FROM wallet_transactions; DELETE FROM wallets WHERE id = '{DemoData.WalletId}'", "fk_service_requests_wallets")]
    public async Task F06_DeletingReferencedHistoryIsRejectedAndTheWholeTransactionRollsBack(string statements, string constraint)
    {
        var database = await BookedAsync();
        var counts = (await database.RowsAsync(CountsSql)).Single();
        var rows = await database.RowsAsync(FinancialRowsSql);

        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var statement in statements.Split(';'))
            {
                await using var command = new NpgsqlCommand(statement, connection, transaction);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        });

        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
        Assert.Equal("1|2|1|2|1", counts);
        Assert.Equal(counts, (await database.RowsAsync(CountsSql)).Single());
        Assert.Equal(rows, await database.RowsAsync(FinancialRowsSql));
    }

    private const string FinancialRowsSql =
        """
        SELECT a, b, c, d FROM (
            SELECT 1 AS rank, id::text AS a, total_amount::text AS b, request_fingerprint AS c, NULL::text AS d FROM service_requests
            UNION ALL SELECT 2, service_code, unit_price::text, status, NULL FROM service_request_items
            UNION ALL SELECT 3, kind, amount::text, balance_after::text, coalesce(request_item_id::text, '') FROM wallet_transactions
        ) AS rows ORDER BY rank, a, b
        """;

    private async Task<TestDatabase> BookedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await database.ExecuteAsync(BookingWithDebit);
        return database;
    }
}
