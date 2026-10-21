using System.Data;
using Cicclo.Api.Platform.Persistence;
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
            PostgresQueueSession.Expect(
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
    }
}
