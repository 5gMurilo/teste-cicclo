namespace Cicclo.Api.Tests.Support;

/// <summary>Valores iniciais definidos no design (tabela "Dados iniciais"), escritos aqui de forma independente do seed.sql.</summary>
public static class DemoData
{
    public const string WalletId = "00000000-0000-4000-8000-000000000001";

    public const string WasherId = "00000000-0000-4000-8000-000000000101";

    public const string DryerId = "00000000-0000-4000-8000-000000000102";

    public static readonly IReadOnlyList<string> InitialSnapshot =
    [
        $"wallet|{WalletId}|BRL|50.00",
        "service|dry|Secagem|dryer|20.90|True",
        "service|wash|Lavagem|washer|18.90|True",
        $"machine|{WasherId}|washer-01|washer|available",
        $"machine|{DryerId}|dryer-01|dryer|available",
    ];

    private const string SnapshotSql =
        """
        SELECT kind, key, a, b, c FROM (
            SELECT 1 AS rank, 'wallet' AS kind, id::text AS key, currency::text AS a, balance::text AS b, '' AS c FROM wallets
            UNION ALL
            SELECT 2, 'service', code, name, machine_type, current_price::text || '|' || CASE WHEN is_active THEN 'True' ELSE 'False' END FROM services
            UNION ALL
            SELECT 3, 'machine', id::text, name, machine_type, state FROM machines
        ) AS snapshot
        ORDER BY rank, key
        """;

    public static async Task<IReadOnlyList<string>> SnapshotAsync(TestDatabase database) =>
        [.. (await database.RowsAsync(SnapshotSql)).Select(row => row.EndsWith('|') ? row[..^1] : row)];
}
