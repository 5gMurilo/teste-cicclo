using Cicclo.Api.Bookings;

namespace Cicclo.Api.Laundry;

/// <summary>Booking dono do item e tipo de máquina que o atende, descobertos sem lock.</summary>
public sealed record ItemLocation(Guid BookingId, MachineType MachineType);

/// <summary>Item de um tipo de máquina lido sob lock: fila (ticket/queued_at) e oferta corrente (máquina/prazo).</summary>
public sealed record LaundryItem(Guid ItemId, BookingItemStatus Status, long? Ticket, DateTime? QueuedAt, Guid? MachineId, DateTime? ExpiresAt);

/// <summary>
/// Operações de fila/oferta dentro de uma transação já aberta (a do caso de uso que a chama). Contrato comum:
/// máquinas são bloqueadas antes dos itens, cada mutação valida o estado de origem e falha sem efeito parcial
/// visível, porque nada é confirmado antes do commit de quem abriu a transação.
/// </summary>
public interface IQueueSession
{
    /// <summary>
    /// Descoberta sem lock do booking e do tipo de máquina do item; null se o item não existe. Só orienta quais locks
    /// adquirir: o código de serviço e seu tipo de máquina são estáveis (wash↔washer, dry↔dryer).
    /// </summary>
    Task<ItemLocation?> LocateItemAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Bloqueia as máquinas do tipo em ordem de id e devolve seu estado atual.</summary>
    Task<IReadOnlyList<MachineSlot>> LockMachinesAsync(MachineType type, CancellationToken cancellationToken);

    /// <summary>Bloqueia, em ordem de id, os itens queued/to_be_confirmed do tipo e o item indicado.</summary>
    Task<IReadOnlyList<LaundryItem>> LockItemsAsync(MachineType type, Guid? includeItemId, CancellationToken cancellationToken);

    /// <summary>Próximo valor de queue_ticket_seq.</summary>
    Task<long> NextQueueTicketAsync(CancellationToken cancellationToken);

    /// <summary>pending_activation → queued com ticket e queued_at.</summary>
    Task EnqueueAsync(Guid itemId, long ticket, DateTime queuedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Oferta vencida: máquina reserved do item → available, e item to_be_confirmed → queued com ticket e queued_at novos,
    /// sem máquina nem tempos da oferta antiga.
    /// </summary>
    Task RequeueExpiredAsync(Guid itemId, Guid machineId, long ticket, DateTime requeuedAt, CancellationToken cancellationToken);

    /// <summary>queued → to_be_confirmed na máquina, e máquina available → reserved apontando o item.</summary>
    Task OfferAsync(Guid itemId, Guid machineId, DateTime offeredAt, DateTime expiresAt, CancellationToken cancellationToken);
}

/// <summary>Preço histórico do item e valor do refund já lançado para ele (null se nenhum).</summary>
public sealed record RefundableItem(decimal UnitPrice, decimal? RefundedAmount);

/// <summary>Abre uma transação nova de lavanderia por chamada.</summary>
public interface ILaundrySessions
{
    Task<ILaundrySession> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Uma transação de lavanderia na ordem comum de locks: wallet demo primeiro (mutex das escritas), depois máquinas e
/// itens. Descartar sem commit desfaz tudo.
/// </summary>
public interface ILaundrySession : IQueueSession, IAsyncDisposable
{
    /// <summary>Bloqueia a wallet até o fim da transação; ausência dela é base não preparada.</summary>
    Task LockWalletAsync(Guid walletId, CancellationToken cancellationToken);

    /// <summary>
    /// Descoberta sem lock do tipo de uma máquina; null se ela não existe. Só orienta quais locks adquirir: o inventário é
    /// fixo e o tipo de uma máquina não muda.
    /// </summary>
    Task<MachineType?> LocateMachineAsync(Guid machineId, CancellationToken cancellationToken);

    /// <summary>Ocupação sintética da simulação: máquina available sem item → running com is_demo_occupied, sem item nem wallet.</summary>
    Task OccupyAsync(Guid machineId, DateTime occupiedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Conclusão da execução: com item, item in_progress nesta máquina → completed (histórico de máquina e tempos
    /// preservado) e máquina running dele → available; sem item, ocupação sintética → available sem a marca.
    /// </summary>
    Task CompleteRunAsync(Guid machineId, Guid? itemId, DateTime completedAt, CancellationToken cancellationToken);

    /// <summary>Oferta confirmada: to_be_confirmed → in_progress com started_at, e máquina reserved → running.</summary>
    Task StartAsync(Guid itemId, Guid machineId, DateTime startedAt, CancellationToken cancellationToken);

    /// <summary>Preço histórico e refund existente de um item já bloqueado por <see cref="IQueueSession.LockItemsAsync"/>.</summary>
    Task<RefundableItem> ReadRefundableAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Cancelamento com refund, tudo nesta transação: item (no estado de origem) → cancelled sem atribuição; máquina
    /// reservada para ele → available; wallet creditada com o valor; um lançamento refund do item. Devolve o saldo após o crédito.
    /// </summary>
    Task<decimal> CancelWithRefundAsync(
        Guid walletId, Guid bookingId, Guid itemId, BookingItemStatus from, Guid? machineId, decimal amount, DateTime cancelledAt, CancellationToken cancellationToken);

    /// <summary>Bloqueia (por último na ordem de locks) o relato aberto do item e devolve seu id; null se não há.</summary>
    Task<Guid?> LockOpenIncidentAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Novo relato reported do item na máquina da execução; não toca item, máquina, wallet nem lançamentos.</summary>
    Task RecordIncidentAsync(Guid incidentId, Guid machineId, Guid itemId, string? description, DateTime reportedAt, CancellationToken cancellationToken);

    Task CommitAsync();
}
