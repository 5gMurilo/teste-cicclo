using System.Data;
using Cicclo.Api.Bookings;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Platform.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cicclo.Api.Laundry;

/// <summary>
/// Adaptador PostgreSQL da lavanderia: um DbContext e uma transação Read Committed por sessão, locks explícitos
/// (FOR UPDATE) na ordem wallet → máquinas por id → itens por id; o SQL de fila é o de <see cref="PostgresQueueSession"/>.
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
        private readonly PostgresQueueSession queue = new(db);

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

        public Task<ItemLocation?> LocateItemAsync(Guid itemId, CancellationToken cancellationToken) => queue.LocateItemAsync(itemId, cancellationToken);

        public Task<IReadOnlyList<MachineSlot>> LockMachinesAsync(MachineType type, CancellationToken cancellationToken) =>
            queue.LockMachinesAsync(type, cancellationToken);

        public Task<IReadOnlyList<LaundryItem>> LockItemsAsync(MachineType type, Guid? includeItemId, CancellationToken cancellationToken) =>
            queue.LockItemsAsync(type, includeItemId, cancellationToken);

        public Task<long> NextQueueTicketAsync(CancellationToken cancellationToken) => queue.NextQueueTicketAsync(cancellationToken);

        public Task EnqueueAsync(Guid itemId, long ticket, DateTime queuedAt, CancellationToken cancellationToken) =>
            queue.EnqueueAsync(itemId, ticket, queuedAt, cancellationToken);

        public Task RequeueExpiredAsync(Guid itemId, Guid machineId, long ticket, DateTime requeuedAt, CancellationToken cancellationToken) =>
            queue.RequeueExpiredAsync(itemId, machineId, ticket, requeuedAt, cancellationToken);

        public Task OfferAsync(Guid itemId, Guid machineId, DateTime offeredAt, DateTime expiresAt, CancellationToken cancellationToken) =>
            queue.OfferAsync(itemId, machineId, offeredAt, expiresAt, cancellationToken);

        public async Task<MachineType?> LocateMachineAsync(Guid machineId, CancellationToken cancellationToken)
        {
            var types = await db.Machines.AsNoTracking().Where(machine => machine.Id == machineId).Select(machine => machine.MachineType).ToListAsync(cancellationToken);
            return types.Count == 1 ? types[0] : null;
        }

        public async Task OccupyAsync(Guid machineId, DateTime occupiedAt, CancellationToken cancellationToken) =>
            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE machines SET state = 'running', is_demo_occupied = true, updated_at = {occupiedAt}
                    WHERE id = {machineId} AND state = 'available' AND active_item_id IS NULL
                    """,
                    cancellationToken),
                "máquina livre para ocupar");

        public async Task CompleteRunAsync(Guid machineId, Guid? itemId, DateTime completedAt, CancellationToken cancellationToken)
        {
            if (itemId is not { } item)
            {
                PostgresQueueSession.Expect(
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        UPDATE machines SET state = 'available', is_demo_occupied = false, updated_at = {completedAt}
                        WHERE id = {machineId} AND state = 'running' AND active_item_id IS NULL AND is_demo_occupied
                        """,
                        cancellationToken),
                    "máquina em ocupação sintética");
                return;
            }

            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE service_request_items SET status = 'completed'
                    WHERE id = {item} AND status = 'in_progress' AND machine_id = {machineId}
                    """,
                    cancellationToken),
                "item em execução para concluir");
            queue.RecordItemChange(item);
            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE machines SET state = 'available', active_item_id = NULL, updated_at = {completedAt}
                    WHERE id = {machineId} AND state = 'running' AND active_item_id = {item}
                    """,
                    cancellationToken),
                "máquina em execução do item");
        }

        public async Task StartAsync(Guid itemId, Guid machineId, DateTime startedAt, CancellationToken cancellationToken)
        {
            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE service_request_items SET status = 'in_progress', started_at = {startedAt}
                    WHERE id = {itemId} AND status = 'to_be_confirmed' AND machine_id = {machineId}
                    """,
                    cancellationToken),
                "oferta corrente para iniciar");
            queue.RecordItemChange(itemId);
            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE machines SET state = 'running', updated_at = {startedAt}
                    WHERE id = {machineId} AND state = 'reserved' AND active_item_id = {itemId}
                    """,
                    cancellationToken),
                "máquina reservada para o item");
        }

        public async Task<RefundableItem> ReadRefundableAsync(Guid itemId, CancellationToken cancellationToken)
        {
            var rows = await db.Database
                .SqlQuery<RefundableItem>(
                    $"""
                    SELECT i.unit_price AS "UnitPrice",
                           (SELECT t.amount FROM wallet_transactions t WHERE t.request_item_id = i.id AND t.kind = 'refund') AS "RefundedAmount"
                    FROM service_request_items i WHERE i.id = {itemId}
                    """)
                .ToListAsync(cancellationToken);
            return rows.Single();
        }

        public async Task<decimal> CancelWithRefundAsync(
            Guid walletId, Guid bookingId, Guid itemId, BookingItemStatus from, Guid? machineId, decimal amount, DateTime cancelledAt, CancellationToken cancellationToken)
        {
            var fromText = BookingItemConfiguration.ToText(from);
            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE service_request_items
                    SET status = 'cancelled', cancelled_at = {cancelledAt}, machine_id = NULL, offered_at = NULL, expires_at = NULL
                    WHERE id = {itemId} AND request_id = {bookingId} AND status = {fromText}
                    """,
                    cancellationToken),
                "item não iniciado para cancelar");
            queue.RecordItemChange(itemId);
            if (machineId is { } machine)
            {
                PostgresQueueSession.Expect(
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        UPDATE machines SET state = 'available', active_item_id = NULL, updated_at = {cancelledAt}
                        WHERE id = {machine} AND state = 'reserved' AND active_item_id = {itemId}
                        """,
                        cancellationToken),
                    "máquina reservada para o item cancelado");
            }

            return await CreditRefundAsync(walletId, bookingId, itemId, amount, cancelledAt, cancellationToken);
        }

        public async Task FailRunAsync(Guid machineId, Guid? itemId, DateTime failedAt, CancellationToken cancellationToken)
        {
            if (itemId is not { } item)
            {
                PostgresQueueSession.Expect(
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        UPDATE machines SET state = 'out_of_service', is_demo_occupied = false, updated_at = {failedAt}
                        WHERE id = {machineId} AND state = 'running' AND active_item_id IS NULL AND is_demo_occupied
                        """,
                        cancellationToken),
                    "máquina em ocupação sintética");
                return;
            }

            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE service_request_items SET status = 'failed'
                    WHERE id = {item} AND status = 'in_progress' AND machine_id = {machineId}
                    """,
                    cancellationToken),
                "item em execução para falhar");
            queue.RecordItemChange(item);
            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE machines SET state = 'out_of_service', active_item_id = NULL, updated_at = {failedAt}
                    WHERE id = {machineId} AND state = 'running' AND active_item_id = {item}
                    """,
                    cancellationToken),
                "máquina em execução do item");
        }

        public async Task RestoreAsync(Guid machineId, DateTime restoredAt, CancellationToken cancellationToken) =>
            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE machines SET state = 'available', updated_at = {restoredAt}
                    WHERE id = {machineId} AND state = 'out_of_service' AND active_item_id IS NULL AND NOT is_demo_occupied
                    """,
                    cancellationToken),
                "máquina fora de serviço para restaurar");

        public async Task<decimal> CreditRefundAsync(Guid walletId, Guid bookingId, Guid itemId, decimal amount, DateTime creditedAt, CancellationToken cancellationToken)
        {
            var balance = (await db.Database
                .SqlQuery<decimal>(
                    $"""
                    UPDATE wallets SET balance = balance + {amount}, updated_at = {creditedAt}
                    WHERE id = {walletId}
                    RETURNING balance AS "Value"
                    """)
                .ToListAsync(cancellationToken)).Single();
            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO wallet_transactions (id, wallet_id, request_id, request_item_id, kind, amount, balance_after, created_at)
                    VALUES ({Guid.CreateVersion7(new DateTimeOffset(creditedAt, TimeSpan.Zero))}, {walletId}, {bookingId}, {itemId}, 'refund', {amount}, {balance}, {creditedAt})
                    """,
                    cancellationToken),
                "lançamento de refund");
            return balance;
        }

        public async Task<Guid?> LockOpenIncidentAsync(Guid itemId, CancellationToken cancellationToken)
        {
            var incidents = await db.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM machine_incidents WHERE request_item_id = {itemId} AND status = 'reported' FOR UPDATE")
                .ToListAsync(cancellationToken);
            return incidents.Count == 0 ? null : incidents.Single();
        }

        public async Task RecordIncidentAsync(Guid incidentId, Guid machineId, Guid itemId, string? description, DateTime reportedAt, CancellationToken cancellationToken) =>
            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO machine_incidents (id, machine_id, request_item_id, status, description, reported_at)
                    VALUES ({incidentId}, {machineId}, {itemId}, 'reported', {description}, {reportedAt})
                    """,
                    cancellationToken),
                "relato do item");

        public async Task<IncidentLocation?> LocateIncidentAsync(Guid incidentId, CancellationToken cancellationToken)
        {
            var incidents = await db.Database
                .SqlQuery<IncidentLocation>($"SELECT machine_id AS \"MachineId\", request_item_id AS \"ItemId\" FROM machine_incidents WHERE id = {incidentId}")
                .ToListAsync(cancellationToken);
            return incidents.Count == 0 ? null : incidents.Single();
        }

        public async Task ConfirmIncidentAsync(Guid incidentId, CancellationToken cancellationToken) =>
            PostgresQueueSession.Expect(
                await db.Database.ExecuteSqlAsync($"UPDATE machine_incidents SET status = 'confirmed' WHERE id = {incidentId} AND status = 'reported'", cancellationToken),
                "relato aberto para confirmar");

        public async Task CommitAsync()
        {
            // A revisão dos bookings alterados confirma junto da mutação, ou não confirma.
            await queue.IncrementRevisionsAsync([], CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            // Sem commit, descartar a transação faz rollback; não depende do token de quem chamou.
            await transaction.DisposeAsync();
            await db.DisposeAsync();
        }
    }
}
