using Cicclo.Api.Bookings;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Platform.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Cicclo.Api.Laundry;

/// <summary>
/// SQL de fila/oferta (porta <see cref="IQueueSession"/>) sobre o DbContext e a transação já abertos por quem chama:
/// a sessão de lavanderia e a sessão de compra usam a mesma implementação, então a ativação inicial confirma junto do
/// débito. Locks FOR UPDATE em ordem de id e mutações que conferem o estado de origem.
/// </summary>
public sealed class PostgresQueueSession(CiccloDbContext db) : IQueueSession
{
    /// <summary>Itens mutados nesta transação; seus bookings distintos recebem a revisão em <see cref="IncrementRevisionsAsync"/>.</summary>
    private readonly HashSet<Guid> changedItems = [];

    /// <summary>Registra um item cujo estado persistido esta transação alterou.</summary>
    internal void RecordItemChange(Guid itemId) => changedItems.Add(itemId);

    /// <summary>
    /// Imediatamente antes do commit, na mesma transação: +1 em cada booking distinto dos itens alterados, devolvendo a
    /// revisão atribuída pelo próprio UPDATE (sem releitura que pudesse ver outro commit). Itens inseridos nesta transação
    /// são ignorados, porque seu booking nasce nela e confirma na revisão inicial. Sem outro item alterado, não envia nada
    /// ao banco. O resultado só é fato depois que o commit de quem chamou retornar com sucesso.
    /// </summary>
    internal async Task<IReadOnlyList<RevisedBooking>> IncrementRevisionsAsync(IReadOnlyCollection<Guid> createdItemIds, CancellationToken cancellationToken)
    {
        changedItems.ExceptWith(createdItemIds);
        if (changedItems.Count == 0)
        {
            return [];
        }

        var itemIds = changedItems.ToArray();
        var revised = await db.Database
            .SqlQuery<RevisedBooking>(
                $"""
                UPDATE service_requests SET revision = revision + 1
                WHERE id IN (SELECT request_id FROM service_request_items WHERE id = ANY({itemIds}))
                RETURNING id AS "BookingId", revision AS "Revision"
                """)
            .ToListAsync(cancellationToken);
        changedItems.Clear();
        return [.. revised.OrderBy(booking => booking.BookingId)];
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

    public async Task EnqueueAsync(Guid itemId, long ticket, DateTime queuedAt, CancellationToken cancellationToken)
    {
        Expect(
            await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE service_request_items SET status = 'queued', queue_ticket = {ticket}, queued_at = {queuedAt}
                WHERE id = {itemId} AND status = 'pending_activation'
                """,
                cancellationToken),
            "item pendente para enfileirar");
        RecordItemChange(itemId);
    }

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
        RecordItemChange(itemId);
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
        RecordItemChange(itemId);
        Expect(
            await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE machines SET state = 'reserved', active_item_id = {itemId}, updated_at = {offeredAt}
                WHERE id = {machineId} AND state = 'available' AND active_item_id IS NULL
                """,
                cancellationToken),
            "máquina livre para reservar");
    }

    /// <summary>Uma linha exata: zero significa que o estado de origem mudou, e a transação inteira deve ser revertida.</summary>
    internal static void Expect(int affected, string what)
    {
        if (affected != 1)
        {
            throw new InvalidOperationException($"Transição de fila recusada: {what} não encontrado no estado esperado ({affected} linhas).");
        }
    }
}
