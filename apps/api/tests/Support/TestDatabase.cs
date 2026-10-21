using Cicclo.Api.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cicclo.Api.Tests.Support;

/// <summary>Banco isolado de um cenário, com acesso SQL direto para preparar e conferir o estado persistido.</summary>
public sealed class TestDatabase(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public CiccloDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CiccloDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task MigrateAsync()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    /// <summary>Prepara a base como o bootstrap: migrations reais e depois o seed.sql distribuído, pelo mesmo executor.</summary>
    public async Task BootstrapAsync()
    {
        await MigrateAsync();
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
}
