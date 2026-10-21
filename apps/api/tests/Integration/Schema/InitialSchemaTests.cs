using Cicclo.Api.Tests.Support;
using Npgsql;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Schema;

[Collection(PostgresCollection.Name)]
[Trait("Increment", "E01")]
[Trait("Task", "T1")]
public sealed class InitialSchemaTests(PostgresFixture postgres)
{
    private const string ValidWallet =
        "INSERT INTO wallets (id, currency, balance, created_at, updated_at) VALUES ('00000000-0000-4000-8000-000000000001', 'BRL', 50.00, now(), now());";

    private const string ValidService =
        "INSERT INTO services (code, name, machine_type, current_price, is_active, created_at) VALUES ('wash', 'Lavagem', 'washer', 18.90, true, now());";

    private const string ValidMachine =
        "INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES ('00000000-0000-4000-8000-000000000101', 'washer-01', 'washer', 'available', now());";

    // As migrations posteriores têm testes próprios; estes cenários provam a migration inicial.
    private const string InitialMigration = "InitialSchema";

    private const string TablesSql =
        "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name";

    [Fact]
    public async Task S01_MigrationOnEmptyDatabaseCreatesOnlyInitialTablesWithExactColumns()
    {
        var database = await postgres.CreateDatabaseAsync();

        await database.MigrateAsync(InitialMigration);

        Assert.Equal(["__EFMigrationsHistory", "machines", "services", "wallets"], await database.RowsAsync(TablesSql));
        Assert.Equal(
            [
                "machines|id|uuid|True",
                "machines|name|text|True",
                "machines|machine_type|text|True",
                "machines|state|text|True",
                "machines|updated_at|timestamp with time zone|True",
                "services|code|text|True",
                "services|name|text|True",
                "services|machine_type|text|True",
                "services|current_price|numeric(12,2)|True",
                "services|is_active|boolean|True",
                "services|created_at|timestamp with time zone|True",
                "wallets|id|uuid|True",
                "wallets|currency|character(3)|True",
                "wallets|balance|numeric(12,2)|True",
                "wallets|created_at|timestamp with time zone|True",
                "wallets|updated_at|timestamp with time zone|True",
            ],
            await database.RowsAsync(
                """
                SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull
                FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname IN ('wallets', 'services', 'machines')
                  AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
                ORDER BY c.relname, a.attnum
                """));
        Assert.Equal(
            [
                "machines|ix_machines_machine_type_state|CREATE INDEX ix_machines_machine_type_state ON public.machines USING btree (machine_type, state)",
                "machines|pk_machines|CREATE UNIQUE INDEX pk_machines ON public.machines USING btree (id)",
                "machines|ux_machines_name|CREATE UNIQUE INDEX ux_machines_name ON public.machines USING btree (name)",
                "services|pk_services|CREATE UNIQUE INDEX pk_services ON public.services USING btree (code)",
                "wallets|pk_wallets|CREATE UNIQUE INDEX pk_wallets ON public.wallets USING btree (id)",
            ],
            await database.RowsAsync(
                "SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' ORDER BY tablename, indexname"));
        Assert.Equal(
            "0|0|0",
            (await database.RowsAsync(
                "SELECT (SELECT count(*) FROM wallets), (SELECT count(*) FROM services), (SELECT count(*) FROM machines)")).Single());
    }

    [Fact]
    public async Task S02_ReapplyingMigrationsKeepsSchemaAndHistoryWithoutDuplicates()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync(InitialMigration);
        var tables = await database.RowsAsync(TablesSql);
        var constraints = await database.RowsAsync(
            "SELECT conrelid::regclass::text, conname FROM pg_constraint WHERE connamespace = 'public'::regnamespace ORDER BY 1, 2");

        await database.MigrateAsync(InitialMigration);

        Assert.Equal(tables, await database.RowsAsync(TablesSql));
        Assert.Equal(
            constraints,
            await database.RowsAsync(
                "SELECT conrelid::regclass::text, conname FROM pg_constraint WHERE connamespace = 'public'::regnamespace ORDER BY 1, 2"));
        var history = await database.RowsAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"");
        Assert.Single(history);
        Assert.EndsWith("_InitialSchema", history[0]);
    }

    [Fact]
    public async Task S03_NegativeWalletBalanceIsRejected()
    {
        var database = await MigratedAsync();

        await AssertViolationAsync(
            database,
            "INSERT INTO wallets (id, currency, balance, created_at, updated_at) VALUES (gen_random_uuid(), 'BRL', -0.01, now(), now());",
            PostgresErrorCodes.CheckViolation,
            "ck_wallets_balance_non_negative");
        await database.ExecuteAsync(ValidWallet);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("brl")]
    public async Task S04_WalletCurrencyOtherThanBrlIsRejected(string currency)
    {
        var database = await MigratedAsync();

        await AssertViolationAsync(
            database,
            $"INSERT INTO wallets (id, currency, balance, created_at, updated_at) VALUES (gen_random_uuid(), '{currency}', 50.00, now(), now());",
            PostgresErrorCodes.CheckViolation,
            "ck_wallets_currency_brl");
        await database.ExecuteAsync(ValidWallet);
    }

    [Fact]
    public async Task S05_ZeroServicePriceIsRejected()
    {
        var database = await MigratedAsync();

        await AssertViolationAsync(
            database,
            "INSERT INTO services (code, name, machine_type, current_price, is_active, created_at) VALUES ('wash', 'Lavagem', 'washer', 0.00, true, now());",
            PostgresErrorCodes.CheckViolation,
            "ck_services_current_price_positive");
        await database.ExecuteAsync(ValidService);
    }

    [Fact]
    public async Task S06_NegativeServicePriceIsRejected()
    {
        var database = await MigratedAsync();

        await AssertViolationAsync(
            database,
            "INSERT INTO services (code, name, machine_type, current_price, is_active, created_at) VALUES ('dry', 'Secagem', 'dryer', -20.90, true, now());",
            PostgresErrorCodes.CheckViolation,
            "ck_services_current_price_positive");
        await database.ExecuteAsync(ValidService);
    }

    [Theory]
    [InlineData("wash", "dryer")]
    [InlineData("dry", "washer")]
    [InlineData("iron", "washer")]
    public async Task S07_IncoherentServiceCodeAndMachineTypeAreRejected(string code, string machineType)
    {
        var database = await MigratedAsync();

        await AssertViolationAsync(
            database,
            $"INSERT INTO services (code, name, machine_type, current_price, is_active, created_at) VALUES ('{code}', 'Serviço', '{machineType}', 10.00, true, now());",
            PostgresErrorCodes.CheckViolation,
            "ck_services_code_machine_type");
        await database.ExecuteAsync(ValidService);
    }

    [Fact]
    public async Task S08_InvalidMachineTypeIsRejected()
    {
        var database = await MigratedAsync();

        await AssertViolationAsync(
            database,
            "INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES (gen_random_uuid(), 'ironer-01', 'ironer', 'available', now());",
            PostgresErrorCodes.CheckViolation,
            "ck_machines_machine_type");
        await database.ExecuteAsync(ValidMachine);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("busy")]
    public async Task S09_InvalidMachineStateIsRejected(string state)
    {
        var database = await MigratedAsync();

        await AssertViolationAsync(
            database,
            $"INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES (gen_random_uuid(), 'washer-02', 'washer', '{state}', now());",
            PostgresErrorCodes.CheckViolation,
            "ck_machines_state");
        await database.ExecuteAsync(ValidMachine);
    }

    [Theory]
    [InlineData(ValidWallet, ValidWallet, "pk_wallets")]
    [InlineData(ValidService, ValidService, "pk_services")]
    [InlineData(ValidMachine, ValidMachine, "pk_machines")]
    [InlineData(
        ValidMachine,
        "INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES ('00000000-0000-4000-8000-000000000199', 'washer-01', 'washer', 'available', now());",
        "ux_machines_name")]
    public async Task S10_PrimaryKeysAndMachineNameRejectDuplicates(string first, string duplicate, string constraint)
    {
        var database = await MigratedAsync();
        await database.ExecuteAsync(first);

        await AssertViolationAsync(database, duplicate, PostgresErrorCodes.UniqueViolation, constraint);
    }

    private async Task<TestDatabase> MigratedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync();
        return database;
    }

    private static async Task AssertViolationAsync(TestDatabase database, string sql, string sqlState, string constraint)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql));
        Assert.Equal(sqlState, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }
}
