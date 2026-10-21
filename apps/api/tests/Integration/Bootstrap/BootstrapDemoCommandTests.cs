using System.Net;
using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Bootstrap;

[Collection(PostgresCollection.Name)]
[Trait("Increment", "E01")]
[Trait("Task", "T2")]
public sealed class BootstrapDemoCommandTests(PostgresFixture postgres, PublishedApiFixture api)
    : IClassFixture<PublishedApiFixture>
{
    private const string MigrationsCompleted = "Bootstrap demo: migrations concluídas.";
    private const string SeedStarted = "Bootstrap demo: seed iniciado.";
    private const string HttpListener = "Now listening on";
    private const string ConflictingDryerId = "00000000-0000-4000-8000-000000000199";

    [Fact]
    public async Task B01_FirstRunCreatesStructureThenInsertsInitialData()
    {
        var database = await postgres.CreateDatabaseAsync();

        var result = await api.BootstrapAsync(database.ConnectionString);

        Assert.Equal(0, result.ExitCode);
        var migrated = result.Output.IndexOf(MigrationsCompleted, StringComparison.Ordinal);
        Assert.True(migrated >= 0, result.Output);
        Assert.True(result.Output.IndexOf(SeedStarted, StringComparison.Ordinal) > migrated, result.Output);
        Assert.DoesNotContain(HttpListener, result.Output);
        Assert.DoesNotContain(PostgresFixture.Password, result.Output);
        Assert.Equal(TestDatabase.MigrationIds, await database.RowsAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1"));
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
        Assert.True(await database.ScalarAsync<bool>(
            """
            SELECT bool_and(stamp BETWEEN now() - interval '5 minutes' AND now()) FROM (
                SELECT created_at AS stamp FROM wallets UNION ALL SELECT updated_at FROM wallets
                UNION ALL SELECT created_at FROM services UNION ALL SELECT updated_at FROM machines) AS stamps
            """));
    }

    [Fact]
    public async Task B02_SecondRunKeepsTheSameFiveIdentities()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await api.BootstrapAsync(database.ConnectionString)).ExitCode);
        var createdAt = await database.RowsAsync("SELECT created_at FROM wallets UNION ALL SELECT created_at FROM services ORDER BY 1");

        var second = await api.BootstrapAsync(database.ConnectionString);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
        Assert.Equal("1|2|2", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM wallets), (SELECT count(*) FROM services), (SELECT count(*) FROM machines)")).Single());
        Assert.Equal(createdAt, await database.RowsAsync("SELECT created_at FROM wallets UNION ALL SELECT created_at FROM services ORDER BY 1"));
    }

    [Fact]
    public async Task B03_RerunPreservesChangedBalancePriceStateAndTimestamps()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await api.BootstrapAsync(database.ConnectionString)).ExitCode);
        await database.ExecuteAsync(
            $"""
            UPDATE wallets SET balance = 31.10, updated_at = '2001-02-03T04:05:06Z' WHERE id = '{DemoData.WalletId}';
            UPDATE services SET current_price = 25.00 WHERE code = 'wash';
            UPDATE machines SET state = 'out_of_service', updated_at = '2001-02-03T04:05:06Z' WHERE id = '{DemoData.WasherId}';
            """);
        var stamps = await database.RowsAsync(
            $"SELECT updated_at FROM wallets UNION ALL SELECT updated_at FROM machines WHERE id = '{DemoData.WasherId}'");

        var result = await api.BootstrapAsync(database.ConnectionString);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                $"wallet|{DemoData.WalletId}|BRL|31.10",
                "service|dry|Secagem|dryer|20.90|True",
                "service|wash|Lavagem|washer|25.00|True",
                $"machine|{DemoData.WasherId}|washer-01|washer|out_of_service",
                $"machine|{DemoData.DryerId}|dryer-01|dryer|available",
            ],
            await DemoData.SnapshotAsync(database));
        Assert.Equal(
            stamps,
            await database.RowsAsync($"SELECT updated_at FROM wallets UNION ALL SELECT updated_at FROM machines WHERE id = '{DemoData.WasherId}'"));
        Assert.True(await database.ScalarAsync<bool>(
            $"SELECT bool_and(updated_at = '2001-02-03T04:05:06Z') FROM (SELECT updated_at FROM wallets UNION ALL SELECT updated_at FROM machines WHERE id = '{DemoData.WasherId}') AS stamps"));
    }

    [Fact]
    public async Task B04_MigrationFailureStopsBeforeSeed()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.ExecuteAsync("CREATE TABLE wallets (legacy integer);");

        var result = await api.BootstrapAsync(database.ConnectionString);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Bootstrap demo: migrations falharam; seed não executado.", result.Output);
        Assert.DoesNotContain(SeedStarted, result.Output);
        Assert.DoesNotContain(PostgresFixture.Password, result.Output);
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM wallets"));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\""));
        Assert.True(await database.ScalarAsync<bool>("SELECT to_regclass('public.services') IS NULL AND to_regclass('public.machines') IS NULL"));
    }

    [Fact]
    public async Task B05_SeedFailureInTheMiddleRollsBackAllSeedInsertsAndKeepsMigrations()
    {
        var database = await DatabaseWithConflictingDryerAsync();

        var result = await api.BootstrapAsync(database.ConnectionString);

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Output.IndexOf(SeedStarted, StringComparison.Ordinal) > result.Output.IndexOf(MigrationsCompleted, StringComparison.Ordinal));
        Assert.Contains("Bootstrap demo: seed falhou e foi revertido.", result.Output);
        Assert.DoesNotContain(PostgresFixture.Password, result.Output);
        Assert.Equal([$"machine|{ConflictingDryerId}|dryer-01|dryer|available"], await DemoData.SnapshotAsync(database));
        Assert.Equal(TestDatabase.MigrationIds, await database.RowsAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1"));
    }

    [Fact]
    public async Task B06_FixingTheConflictAndRepeatingRecovers()
    {
        var database = await DatabaseWithConflictingDryerAsync();
        Assert.Equal(1, (await api.BootstrapAsync(database.ConnectionString)).ExitCode);
        await database.ExecuteAsync($"DELETE FROM machines WHERE id = '{ConflictingDryerId}';");

        var result = await api.BootstrapAsync(database.ConnectionString);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
    }

    [Theory]
    [InlineData("Production", "true")]
    [InlineData("Development", "false")]
    [InlineData("Development", null)]
    public async Task B07_B08_BootstrapRequiresDevelopmentAndExplicitDemoMode(string environment, string? demoMode)
    {
        var database = await postgres.CreateDatabaseAsync();

        var result = await api.BootstrapAsync(database.ConnectionString, environment, demoMode);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Bootstrap demo recusado: exige ambiente Development e Laundry:DemoMode=true", result.Output);
        Assert.DoesNotContain(HttpListener, result.Output);
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
    }

    [Fact]
    public async Task B09_BootstrapRequiresAConfiguredConnection()
    {
        var result = await api.BootstrapAsync(connectionString: null);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Bootstrap demo recusado: ConnectionStrings:Cicclo não configurada.", result.Output);
        Assert.DoesNotContain(HttpListener, result.Output);
    }

    [Fact]
    public async Task B10_NormalWebStartupNeitherMigratesNorInserts()
    {
        var empty = await postgres.CreateDatabaseAsync();
        var migrated = await postgres.CreateDatabaseAsync();
        await migrated.MigrateAsync();

        foreach (var database in new[] { empty, migrated })
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
                .UseEnvironment("Development")
                .UseSetting("Laundry:DemoMode", "true")
                .UseSetting("ConnectionStrings:Cicclo", database.ConnectionString));
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(0L, await empty.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
        Assert.Empty(await DemoData.SnapshotAsync(migrated));
    }

    [Fact]
    public async Task B11_PublishedArtifactOutsideTheRepositoryCarriesAndFindsTheSeed()
    {
        var repository = Path.GetFullPath(Path.Combine(RepositoryPaths.ApiRoot, "..", ".."));
        var published = Path.Combine(api.Directory, "Seed", "seed.sql");
        var database = await postgres.CreateDatabaseAsync();

        var result = await api.BootstrapAsync(database.ConnectionString);

        Assert.False(api.Directory.StartsWith(repository, StringComparison.Ordinal));
        Assert.Equal(await File.ReadAllTextAsync(RepositoryPaths.Api("init", "seed.sql")), await File.ReadAllTextAsync(published));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "Seed", "seed.sql")));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
    }

    private async Task<TestDatabase> DatabaseWithConflictingDryerAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync();
        await database.ExecuteAsync(
            $"INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES ('{ConflictingDryerId}', 'dryer-01', 'dryer', 'available', now());");
        return database;
    }
}
