using System.Globalization;
using System.Text.Json;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Operation;

/// <summary>
/// Container PostgreSQL 18.1 exclusivo dos testes de reset, com nome único. Cada cenário recebe um banco `cicclo` novo:
/// o anterior é apenas renomeado (nada é apagado) e tudo some com o container ao final.
/// </summary>
public sealed class ResetDemoDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(PostgresFixture.Image)
        .WithName($"cicclo-reset-tests-{Guid.NewGuid():N}")
        .WithPassword(Password)
        .Build();

    public static string Password { get; } = $"pw-reset-{Guid.NewGuid():N}-$!";

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    /// <summary>Banco com o nome indicado, novo; um homônimo anterior é renomeado para manter o nome livre.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(string name = ResetDemoCommand.DemoDatabase)
    {
        await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM pg_database WHERE datname = '{name}') THEN
                        EXECUTE 'ALTER DATABASE "{name}" RENAME TO "{name}_used_{Guid.NewGuid():N}"';
                    END IF;
                END $$;
                """,
                connection);
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new TestDatabase(new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString);
    }
}

/// <summary>
/// Executa o artefato publicado em `--reset-demo` contra bancos do container exclusivo acima. Nenhum cenário
/// aponta para o banco, volume ou Compose pessoal.
/// </summary>
[Trait("Increment", "E07")]
[Trait("Task", "T1")]
public sealed class ResetDemoCommandTests(ResetDemoDatabaseFixture postgres, PublishedApiFixture api)
    : IClassFixture<ResetDemoDatabaseFixture>, IClassFixture<PublishedApiFixture>
{
    private const string HttpListener = "Now listening on";
    private const string Completed = "Reset demo concluído: dados iniciais restaurados pelo seed.sql.";
    private const string Started = "Reset demo: esvaziando as tabelas da aplicação e reaplicando o seed.sql";
    private const string RolledBack = "Reset demo falhou e foi revertido; nenhum dado foi alterado.";

    private const string StateSql =
        """
        SELECT 'counts', (SELECT count(*) FROM service_requests)::text, (SELECT count(*) FROM service_request_items)::text,
               (SELECT count(*) FROM wallet_transactions)::text, (SELECT count(*) FROM machine_incidents)::text
        UNION ALL
        SELECT * FROM (SELECT 'machine', id::text, state, coalesce(active_item_id::text, '-'), to_char(updated_at, 'YYYY-MM-DD HH24:MI:SS.US') FROM machines ORDER BY id) AS machines
        """;

    [Fact]
    public async Task R01_AuthorizedResetRestoresSeedValuesAndFreeMachines()
    {
        var database = await DirtyDatabaseAsync();

        var result = await api.ResetAsync(database.ConnectionString, Target(database));

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains(Completed, result.Output);
        Assert.DoesNotContain(HttpListener, result.Output);
        Assert.DoesNotContain(ResetDemoDatabaseFixture.Password, result.Output);
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
        Assert.Equal(["0|0|0|0"], await database.RowsAsync("SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM service_request_items), (SELECT count(*) FROM wallet_transactions), (SELECT count(*) FROM machine_incidents)"));
        Assert.Equal(
            [$"{DemoData.WasherId}|available|True", $"{DemoData.DryerId}|available|True"],
            await database.RowsAsync("SELECT id, state, active_item_id IS NULL FROM machines ORDER BY id"));

        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();
        using var wallet = JsonDocument.Parse(await client.GetStringAsync("/api/v1/wallet"));
        Assert.Equal(5000L, wallet.RootElement.GetProperty("balanceCents").GetInt64());
        using var services = JsonDocument.Parse(await client.GetStringAsync("/api/v1/services"));
        Assert.Equal(
            [("dry", 2090L, true), ("wash", 1890L, true)],
            services.RootElement.GetProperty("services").EnumerateArray()
                .Select(item => (item.GetProperty("code").GetString()!, item.GetProperty("unitPriceCents").GetInt64(), item.GetProperty("canBeBooked").GetBoolean()))
                .ToList());
    }

    [Theory]
    [InlineData("Production", "true")]
    [InlineData("Development", "false")]
    [InlineData("Development", null)]
    public async Task R02_R03_ResetRequiresDevelopmentAndExplicitDemoMode(string environment, string? demoMode)
    {
        var database = await DirtyDatabaseAsync();
        var before = await StateAsync(database);

        var result = await api.ResetAsync(database.ConnectionString, Target(database), environment, demoMode);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Reset demo recusado: exige ambiente Development e Laundry:DemoMode=true", result.Output);
        Assert.DoesNotContain(Started, result.Output);
        Assert.Equal(before, await StateAsync(database));
    }

    [Theory]
    [InlineData("db.example.com")]
    [InlineData("127.0.0.1,localhost")]
    public async Task R04_RemoteOrMultipleHostsAreRefusedEvenWhenConfirmed(string host)
    {
        var database = await DirtyDatabaseAsync();
        var before = await StateAsync(database);
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Host = host, Timeout = 3 }.ConnectionString;
        var target = $"{host}:{new NpgsqlConnectionStringBuilder(database.ConnectionString).Port}/cicclo";

        var result = await api.ResetAsync(connectionString, target);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"Reset demo recusado: o alvo {target} não é o banco cicclo em localhost, 127.0.0.1, ::1 ou no serviço Compose postgres.", result.Output);
        Assert.DoesNotContain(Started, result.Output);
        Assert.DoesNotContain(ResetDemoDatabaseFixture.Password, result.Output);
        Assert.Equal(before, await StateAsync(database));
    }

    [Fact]
    public async Task R05_DatabaseOtherThanCiccloIsRefused()
    {
        var other = await postgres.CreateDatabaseAsync("cicclo_outro");
        await DirtyAsync(other);
        var before = await StateAsync(other);

        var result = await api.ResetAsync(other.ConnectionString, Target(other));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"Reset demo recusado: o alvo {Target(other)} não é o banco cicclo", result.Output);
        Assert.EndsWith("/cicclo_outro", Target(other), StringComparison.Ordinal);
        Assert.Equal(before, await StateAsync(other));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{0}:{1}")]
    [InlineData("{0}:{2}/cicclo")]
    [InlineData("{0}:{1}/cicclo ")]
    [InlineData("{0}:{1}/postgres")]
    public async Task R06_MissingOrInexactConfirmationIsRefused(string? confirmationFormat)
    {
        var database = await DirtyDatabaseAsync();
        var before = await StateAsync(database);
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        var confirmation = confirmationFormat is null
            ? null
            : string.Format(CultureInfo.InvariantCulture, confirmationFormat, builder.Host, builder.Port, builder.Port + 1);

        var result = await api.ResetAsync(database.ConnectionString, confirmation);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"Reset demo recusado: confirme o alvo exato com --confirm-reset {Target(database)}.", result.Output);
        Assert.DoesNotContain(Started, result.Output);
        Assert.Equal(before, await StateAsync(database));
    }

    [Fact]
    public async Task R07_ActiveConnectionOfTheApiHostBlocksWithoutTerminatingIt()
    {
        var database = await DirtyDatabaseAsync();
        var before = await StateAsync(database);
        ProcessResult refused;
        await using (var factory = ApiFactory.Create(database.ConnectionString))
        {
            using var scope = factory.Services.CreateScope();
            await using (var worker = await factory.Services.GetRequiredService<IDbContextFactory<CiccloDbContext>>().CreateDbContextAsync())
            {
                Assert.Equal(["cicclo.api"], await worker.Database.SqlQueryRaw<string>("SELECT current_setting('application_name') AS \"Value\"").ToListAsync());
            }

            var context = scope.ServiceProvider.GetRequiredService<CiccloDbContext>();
            await context.Database.OpenConnectionAsync();
            var connection = context.Database.GetDbConnection();
            await using (var identity = connection.CreateCommand())
            {
                identity.CommandText = "SELECT current_setting('application_name')";
                Assert.Equal("cicclo.api", await identity.ExecuteScalarAsync());
            }

            refused = await api.ResetAsync(database.ConnectionString, Target(database));

            await using var alive = connection.CreateCommand();
            alive.CommandText = "SELECT 1";
            Assert.Equal(1, await alive.ExecuteScalarAsync());
        }

        Assert.Equal(2, refused.ExitCode);
        Assert.Matches(@"Reset demo recusado: [1-9][0-9]* conexão\(ões\) ativa\(s\) da API/worker \(cicclo\.api\) no alvo\. Pare a API \(docker compose stop api\)", refused.Output);
        Assert.DoesNotContain(Started, refused.Output);
        Assert.Equal(before, await StateAsync(database));

        var afterStop = await api.ResetAsync(database.ConnectionString, Target(database));

        Assert.True(afterStop.ExitCode == 0, afterStop.Output);
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
    }

    [Fact]
    public async Task R08_ForeignDataSchemaAndMigrationHistoryArePreserved()
    {
        var database = await DirtyDatabaseAsync();
        await database.ExecuteAsync(
            """
            CREATE TABLE notas_operador (id integer PRIMARY KEY, texto text NOT NULL);
            INSERT INTO notas_operador VALUES (1, 'manter'), (2, 'também manter');
            CREATE SCHEMA relatorios;
            CREATE TABLE relatorios.fechamento (dia date NOT NULL);
            INSERT INTO relatorios.fechamento VALUES ('2026-10-20');
            """);
        const string TablesSql = "SELECT table_schema || '.' || table_name FROM information_schema.tables WHERE table_schema IN ('public', 'relatorios') ORDER BY 1";
        var tables = await database.RowsAsync(TablesSql);

        var result = await api.ResetAsync(database.ConnectionString, Target(database));

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
        Assert.Equal(["1|manter", "2|também manter"], await database.RowsAsync("SELECT id, texto FROM notas_operador ORDER BY id"));
        Assert.Equal(["2026-10-20"], await database.RowsAsync("SELECT to_char(dia, 'YYYY-MM-DD') FROM relatorios.fechamento"));
        Assert.Equal(TestDatabase.MigrationIds, await database.RowsAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1"));
        Assert.Equal(tables, await database.RowsAsync(TablesSql));
    }

    [Theory]
    [InlineData("removal")]
    [InlineData("reseed")]
    public async Task R09_FailureDuringRemovalOrReseedRollsEverythingBack(string stage)
    {
        var database = await DirtyDatabaseAsync();
        await database.ExecuteAsync(stage == "removal"
            ? $"CREATE TABLE notas_carteira (wallet_id uuid NOT NULL REFERENCES wallets (id)); INSERT INTO notas_carteira VALUES ('{DemoData.WalletId}');"
            : """
              CREATE FUNCTION recusa_maquina() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha de teste no reseed'; END $$;
              CREATE TRIGGER recusa_maquina BEFORE INSERT ON machines FOR EACH ROW EXECUTE FUNCTION recusa_maquina();
              """);
        var before = await StateAsync(database);

        var result = await api.ResetAsync(database.ConnectionString, Target(database));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(Started, result.Output);
        Assert.Contains(RolledBack, result.Output);
        Assert.DoesNotContain(Completed, result.Output);
        Assert.DoesNotContain(ResetDemoDatabaseFixture.Password, result.Output);
        Assert.Equal(before, await StateAsync(database));
        if (stage == "removal")
        {
            Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM notas_carteira"));
        }
    }

    [Fact]
    public async Task R10_SecondAuthorizedRunIsDeterministicOnTheSameDatabase()
    {
        var database = await DirtyDatabaseAsync();
        const string IdentitySql = "SELECT oid::text FROM pg_database WHERE datname = current_database()";
        var oid = await database.RowsAsync(IdentitySql);
        Assert.True((await api.ResetAsync(database.ConnectionString, Target(database))).ExitCode == 0);
        var first = await DemoData.SnapshotAsync(database);
        var firstCounts = await database.RowsAsync("SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM wallet_transactions), (SELECT count(*) FROM machine_incidents)");
        await DirtyAsync(database);

        var second = await api.ResetAsync(database.ConnectionString, Target(database));

        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Equal(DemoData.InitialSnapshot, first);
        Assert.Equal(first, await DemoData.SnapshotAsync(database));
        Assert.Equal(["0|0|0"], firstCounts);
        Assert.Equal(firstCounts, await database.RowsAsync("SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM wallet_transactions), (SELECT count(*) FROM machine_incidents)"));
        Assert.Equal(oid, await database.RowsAsync(IdentitySql));
        Assert.Equal(TestDatabase.MigrationIds, await database.RowsAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1"));
    }

    /// <summary>Alvo no formato da confirmação, montado a partir da conexão do teste (host:port/database).</summary>
    private static string Target(TestDatabase database)
    {
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        return $"{builder.Host}:{builder.Port}/{builder.Database}";
    }

    private async Task<TestDatabase> DirtyDatabaseAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await DirtyAsync(database);
        return database;
    }

    /// <summary>Base usada: compra real wash+dry (saldo 1020), preço alterado e um relato aberto, como depois de uma demo.</summary>
    private static async Task DirtyAsync(TestDatabase database)
    {
        if (await database.ScalarAsync<bool>("SELECT to_regclass('public.wallets') IS NULL"))
        {
            await database.BootstrapAsync();
        }

        await BookingApi.PurchaseAsync(database, TimeProvider.System, ["wash", "dry"]);
        await database.ExecuteAsync(
            $"""
            UPDATE services SET current_price = 25.00 WHERE code = 'wash';
            INSERT INTO machine_incidents (id, machine_id, request_item_id, status, description, reported_at)
            SELECT gen_random_uuid(), '{DemoData.WasherId}', id, 'reported', 'relato de teste', now() FROM service_request_items ORDER BY id LIMIT 1;
            """);
        Assert.Equal(10.20m, await database.ScalarAsync<decimal>("SELECT balance FROM wallets"));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM machine_incidents"));
    }

    private static async Task<IReadOnlyList<string>> StateAsync(TestDatabase database) =>
        [.. await DemoData.SnapshotAsync(database), .. await database.RowsAsync(StateSql)];
}
