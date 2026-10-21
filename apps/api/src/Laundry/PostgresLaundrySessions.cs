using System.Data;
using Cicclo.Api.Bookings;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Platform.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cicclo.Api.Laundry;

/// <summary>
/// Adaptador PostgreSQL da lavanderia: um DbContext e uma transação Read Committed por sessão, locks explícitos
/// (FOR UPDATE) na ordem wallet → máquinas por id → itens por id e mutações que conferem o estado de origem.
/// </summary>
public sealed class PostgresLaundrySessions(IDbContextFactory<CiccloDbContext> contexts) : ILaundrySessions
{
    public async Task<ILaundrySession> OpenAsync(CancellationToken cancellationToken)
    {
        var db = await contexts.CreateDbContextAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(db.Database.GetConnectionString()))
            {
                throw new DatabaseUnavailableException("Conexão com o PostgreSQL não configurada.");
            }

            var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            return new Session(db, transaction);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private sealed class Session(CiccloDbContext db, IDbContextTransaction transaction) : ILaundrySession
    {
        public async Task LockWalletAsync(Guid walletId, CancellationToken cancellationToken)
        {
            var wallets = await db.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM wallets WHERE id = {walletId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            if (wallets.Count != 1)
            {
                throw new DatabaseUnavailableException("Wallet demo ausente: execute o bootstrap.");
            }
        }

        public async Task<ItemLocation?> LocateItemAsync(Guid itemId, CancellationToken cancellationToken)
        {
            var locations = await db.BookingItems
                .AsNoTracking()
                .Where(item => item.Id == itemId)
                .Join(db.Services, item => item.ServiceCode, service => service.Code, (item, service) => new ItemLocation(item.BookingId, service.MachineType))
                .ToListAsync(cancellationToken);
            return locations.Count == 1 ? locations[0] : null;
        }

        public async Task<IReadOnlyList<MachineSlot>> LockMachinesAsync(MachineType type, CancellationToken cancellationToken)
        {
            var typeText = LaundryColumns.ToText(type);
            var machines = await db.Machines
                .FromSql($"SELECT * FROM machines WHERE machine_type = {typeText} ORDER BY id FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            return [.. machines.Select(machine => new MachineSlot(machine.Id, machine.State, machine.ActiveItemId))];
        }

        public async Task<IReadOnlyList<LaundryItem>> LockItemsAsync(MachineType type, Guid? includeItemId, CancellationToken cancellationToken)
        {
            var typeText = LaundryColumns.ToText(type);
            var include = includeItemId ?? Guid.Empty;
            var items = await db.BookingItems
                .FromSql(
                    $"""
                    SELECT i.* FROM service_request_items i JOIN services s ON s.code = i.service_code
                    WHERE (s.machine_type = {typeText} AND i.status IN ('queued', 'to_be_confirmed')) OR i.id = {include}
                    ORDER BY i.id FOR UPDATE OF i
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            return [.. items.Select(item => new LaundryItem(item.Id, item.Status, item.QueueTicket, item.QueuedAt, item.MachineId, item.ExpiresAt))];
        }

        public async Task<long> NextQueueTicketAsync(CancellationToken cancellationToken) =>
            (await db.Database
                .SqlQuery<long>($"SELECT nextval('queue_ticket_seq') AS \"Value\"")
                .ToListAsync(cancellationToken)).Single();

        public async Task EnqueueAsync(Guid itemId, long ticket, DateTime queuedAt, CancellationToken cancellationToken) =>
            Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE service_request_items SET status = 'queued', queue_ticket = {ticket}, queued_at = {queuedAt}
                    WHERE id = {itemId} AND status = 'pending_activation'
                    """,
                    cancellationToken),
                "item pendente para enfileirar");

        public async Task RequeueExpiredAsync(Guid itemId, Guid machineId, long ticket, DateTime requeuedAt, CancellationToken cancellationToken)
        {
            Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE machines SET state = 'available', active_item_id = NULL, updated_at = {requeuedAt}
                    WHERE id = {machineId} AND state = 'reserved' AND active_item_id = {itemId}
                    """,
                    cancellationToken),
                "máquina reservada para a oferta vencida");
            Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE service_request_items
                    SET status = 'queued', queue_ticket = {ticket}, queued_at = {requeuedAt}, machine_id = NULL, offered_at = NULL, expires_at = NULL
                    WHERE id = {itemId} AND status = 'to_be_confirmed' AND machine_id = {machineId} AND expires_at <= {requeuedAt}
                    """,
                    cancellationToken),
                "oferta vencida para devolver à fila");
        }

        public async Task OfferAsync(Guid itemId, Guid machineId, DateTime offeredAt, DateTime expiresAt, CancellationToken cancellationToken)
        {
            Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE service_request_items SET status = 'to_be_confirmed', machine_id = {machineId}, offered_at = {offeredAt}, expires_at = {expiresAt}
                    WHERE id = {itemId} AND status = 'queued'
                    """,
                    cancellationToken),
                "item da fila para ofertar");
            Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE machines SET state = 'reserved', active_item_id = {itemId}, updated_at = {offeredAt}
                    WHERE id = {machineId} AND state = 'available' AND active_item_id IS NULL
                    """,
                    cancellationToken),
                "máquina livre para reservar");
        }

        public async Task StartAsync(Guid itemId, Guid machineId, DateTime startedAt, CancellationToken cancellationToken)
        {
            Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE service_request_items SET status = 'in_progress', started_at = {startedAt}
                    WHERE id = {itemId} AND status = 'to_be_confirmed' AND machine_id = {machineId}
                    """,
                    cancellationToken),
                "oferta corrente para iniciar");
            Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE machines SET state = 'running', updated_at = {startedAt}
                    WHERE id = {machineId} AND state = 'reserved' AND active_item_id = {itemId}
                    """,
                    cancellationToken),
                "máquina reservada para o item");
        }

        public Task CommitAsync() => transaction.CommitAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            // Sem commit, descartar a transação faz rollback; não depende do token de quem chamou.
            await transaction.DisposeAsync();
            await db.DisposeAsync();
        }

        /// <summary>Uma linha exata: zero significa que o estado de origem mudou, e a transação inteira deve ser revertida.</summary>
        private static void Expect(int affected, string what)
        {
            if (affected != 1)
            {
                throw new InvalidOperationException($"Transição de fila recusada: {what} não encontrado no estado esperado ({affected} linhas).");
            }
        }
    }
}
