using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Platform.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Cicclo.Api.Wallet.ListTransactions;

/// <summary>Lançamento do ledger como foi gravado: valor positivo e saldo resultante no instante do lançamento.</summary>
public sealed record LedgerEntryView(
    Guid TransactionId, Guid BookingId, Guid? ItemId, LedgerEntryKind Kind, decimal Amount, decimal BalanceAfter, DateTime CreatedAt);

/// <summary>
/// Adaptador de leitura do ledger da wallet demo: uma instrução SQL por página, em ordem created_at desc, id desc, com um
/// lançamento a mais para indicar a próxima página. Não recalcula saldo nem escreve nada.
/// </summary>
public sealed class PostgresLedgerQuery(CiccloDbContext db)
{
    public async Task<(IReadOnlyList<LedgerEntryView> Entries, bool HasNextPage)> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(db.Database.GetConnectionString()))
        {
            throw new DatabaseUnavailableException("Conexão com o PostgreSQL não configurada.");
        }

        var offset = Platform.Http.Paging.Offset(page, pageSize);
        var rows = await db.Database
            .SqlQuery<Row>(
                $"""
                SELECT id AS "TransactionId", request_id AS "BookingId", request_item_id AS "ItemId", kind AS "Kind",
                       amount AS "Amount", balance_after AS "BalanceAfter", created_at AS "CreatedAt"
                FROM wallet_transactions
                WHERE wallet_id = {DemoWallet.Id}
                ORDER BY created_at DESC, id DESC
                OFFSET {offset} LIMIT {pageSize + 1}
                """)
            .ToListAsync(cancellationToken);
        return (
            [
                .. rows.Take(pageSize).Select(row => new LedgerEntryView(
                    row.TransactionId, row.BookingId, row.ItemId, LedgerEntryConfiguration.ParseKind(row.Kind), row.Amount, row.BalanceAfter, row.CreatedAt)),
            ],
            rows.Count > pageSize);
    }

    private sealed class Row
    {
        public Guid TransactionId { get; init; }

        public Guid BookingId { get; init; }

        public Guid? ItemId { get; init; }

        public string Kind { get; init; } = "";

        public decimal Amount { get; init; }

        public decimal BalanceAfter { get; init; }

        public DateTime CreatedAt { get; init; }
    }
}
