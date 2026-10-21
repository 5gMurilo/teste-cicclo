using Cicclo.Api.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Cicclo.Api.Tests.Support;

/// <summary>Banco isolado de um cenário, com acesso SQL direto para preparar e conferir o estado persistido.</summary>
public sealed class TestDatabase(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    /// <summary>Identificadores de todas as migrations do assembly, em ordem de aplicação.</summary>
    public static IReadOnlyList<string> MigrationIds { get; } = LoadMigrationIds();

    /// <summary>Migrations do assembly até a indicada (inclusive), como ficam no histórico de uma base daquele incremento.</summary>
    public static IReadOnlyList<string> MigrationIdsThrough(string migrationName) =>
        [.. MigrationIds.Take(MigrationIds.ToList().FindIndex(id => id.EndsWith("_" + migrationName, StringComparison.Ordinal)) + 1)];

    public CiccloDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CiccloDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>Fábrica de contextos cujas conexões aparecem em pg_stat_activity com o nome indicado.</summary>
    public IDbContextFactory<CiccloDbContext> ContextFactory(string applicationName) =>
        new NamedContextFactory(new NpgsqlConnectionStringBuilder(ConnectionString) { ApplicationName = applicationName }.ConnectionString);

    public async Task MigrateAsync()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    /// <summary>Aplica as migrations somente até a indicada, reproduzindo uma base de um incremento anterior.</summary>
    public async Task MigrateAsync(string targetMigration)
    {
        await using var context = CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    /// <summary>Prepara a base como o bootstrap: migrations reais e depois o seed.sql distribuído, pelo mesmo executor.</summary>
    public async Task BootstrapAsync()
    {
        await MigrateAsync();
        await SeedAsync();
    }

    /// <summary>Executa somente o seed.sql distribuído, pelo executor de produção.</summary>
    public async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await new DemoSeedSql().ExecuteAsync(connection, CancellationToken.None);
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<string>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join('|', values.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    private static IReadOnlyList<string> LoadMigrationIds()
    {
        using var context = new CiccloDbContext(new DbContextOptionsBuilder<CiccloDbContext>().UseNpgsql().Options);
        return [.. context.Database.GetMigrations()];
    }

    private sealed class NamedContextFactory(string connectionString) : IDbContextFactory<CiccloDbContext>
    {
        public CiccloDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CiccloDbContext>().UseNpgsql(connectionString).Options);
    }
}
