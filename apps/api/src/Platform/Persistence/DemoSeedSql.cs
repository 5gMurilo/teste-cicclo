using Npgsql;

namespace Cicclo.Api.Platform.Persistence;

/// <summary>
/// Executa o `init/seed.sql` distribuído junto do executável. Não define dados: o arquivo é a fonte única.
/// </summary>
public sealed class DemoSeedSql
{
    public static string PublishedPath { get; } = Path.Combine(AppContext.BaseDirectory, "Seed", "seed.sql");

    public async Task ExecuteAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var script = await File.ReadAllTextAsync(PublishedPath, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(script, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
