using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Cicclo.Api.Tests.Support;

/// <summary>
/// Um container PostgreSQL 18.1 descartável e exclusivo desta execução; cada cenário recebe um banco novo.
/// Nunca aponta para o banco local pessoal nem para volumes nomeados.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:18.1";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(Image)
        .WithName($"cicclo-api-tests-{Guid.NewGuid():N}")
        .WithPassword(Password)
        .Build();

    /// <summary>Senha exclusiva do container de teste, usada para provar que não vaza em saídas.</summary>
    public static string Password { get; } = $"pw-{Guid.NewGuid():N}-$!";

    public Task InitializeAsync() => container.StartAsync();

    /// <summary>Conexão do mesmo banco vista por outro container na rede bridge do Docker.</summary>
    public string ContainerNetworkConnectionString(TestDatabase database) =>
        new NpgsqlConnectionStringBuilder(database.ConnectionString) { Host = container.IpAddress, Port = 5432 }.ConnectionString;

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = $"cicclo_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = name,
            Pooling = false,
        }.ConnectionString;
        return new TestDatabase(connectionString);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL";
}
