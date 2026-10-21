using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Platform.Persistence.Configurations;
using Cicclo.Api.Wallet;
using Microsoft.EntityFrameworkCore;

namespace Cicclo.Api.Bookings;

/// <summary>
/// Adaptador de leitura da projeção de booking: uma única instrução SQL (um snapshot do PostgreSQL) junta booking,
/// saldo da wallet, itens e a posição derivada da fila por tipo de máquina (ticket, depois id). Somente a wallet demo;
/// nunca escreve, expira ou promove nada.
/// </summary>
public sealed class PostgresBookingProjection(CiccloDbContext db)
{
    public async Task<BookingView?> FindAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(db.Database.GetConnectionString()))
        {
            throw new DatabaseUnavailableException("Conexão com o PostgreSQL não configurada.");
        }

        var rows = await db.Database
            .SqlQuery<Row>(
                $"""
                SELECT r.id AS "BookingId", r.total_amount AS "ChargedAmount", r.created_at AS "CreatedAt", w.balance AS "Balance",
                       i.id AS "ItemId", i.service_code AS "ServiceCode", i.unit_price AS "UnitPrice", i.status AS "Status",
                       i.machine_id AS "MachineId", i.expires_at AS "ExpiresAt", q.position::int AS "QueuePosition"
                FROM service_requests r
                JOIN wallets w ON w.id = r.wallet_id
                JOIN service_request_items i ON i.request_id = r.id
                LEFT JOIN (
                    SELECT qi.id, row_number() OVER (PARTITION BY s.machine_type ORDER BY qi.queue_ticket, qi.id) AS position
                    FROM service_request_items qi JOIN services s ON s.code = qi.service_code
                    WHERE qi.status = 'queued'
                ) q ON q.id = i.id
                WHERE r.id = {bookingId} AND r.wallet_id = {DemoWallet.Id}
                ORDER BY i.service_code COLLATE "C", i.id
                """)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var first = rows[0];
        return new BookingView(
            first.BookingId,
            first.ChargedAmount,
            first.Balance,
            first.CreatedAt,
            [
                .. rows.Select(row => BookingItemView.FromPersisted(
                    row.ItemId,
                    row.ServiceCode,
                    row.UnitPrice,
                    BookingItemConfiguration.ParseStatus(row.Status),
                    row.MachineId,
                    row.QueuePosition,
                    row.ExpiresAt)),
            ]);
    }

    private sealed class Row
    {
        public Guid BookingId { get; init; }

        public decimal ChargedAmount { get; init; }

        public DateTime CreatedAt { get; init; }

        public decimal Balance { get; init; }

        public Guid ItemId { get; init; }

        public string ServiceCode { get; init; } = "";

        public decimal UnitPrice { get; init; }

        public string Status { get; init; } = "";

        public Guid? MachineId { get; init; }

        public DateTime? ExpiresAt { get; init; }

        public int? QueuePosition { get; init; }
    }
}
