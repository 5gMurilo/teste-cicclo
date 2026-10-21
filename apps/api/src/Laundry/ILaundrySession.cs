using Cicclo.Api.Bookings;

namespace Cicclo.Api.Laundry;

/// <summary>Item de um tipo de máquina lido sob lock: fila (ticket/queued_at) e oferta corrente (máquina/prazo).</summary>
public sealed record LaundryItem(Guid ItemId, BookingItemStatus Status, long? Ticket, DateTime? QueuedAt, Guid? MachineId, DateTime? ExpiresAt);

/// <summary>
/// Operações de fila/oferta dentro de uma transação já aberta (a do caso de uso que a chama). Contrato comum:
/// máquinas são bloqueadas antes dos itens, cada mutação valida o estado de origem e falha sem efeito parcial
/// visível, porque nada é confirmado antes do commit de quem abriu a transação.
/// </summary>
public interface IQueueSession
{
    /// <summary>Descoberta sem lock do tipo de máquina que atende o item; null se o item não existe.</summary>
    Task<MachineType?> FindItemMachineTypeAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Bloqueia as máquinas do tipo em ordem de id e devolve seu estado atual.</summary>
    Task<IReadOnlyList<MachineSlot>> LockMachinesAsync(MachineType type, CancellationToken cancellationToken);

    /// <summary>Bloqueia, em ordem de id, os itens queued/to_be_confirmed do tipo e o item indicado.</summary>
    Task<IReadOnlyList<LaundryItem>> LockItemsAsync(MachineType type, Guid? includeItemId, CancellationToken cancellationToken);

    /// <summary>Próximo valor de queue_ticket_seq.</summary>
    Task<long> NextQueueTicketAsync(CancellationToken cancellationToken);

    /// <summary>pending_activation → queued com ticket e queued_at.</summary>
    Task EnqueueAsync(Guid itemId, long ticket, DateTime queuedAt, CancellationToken cancellationToken);

    /// <summary>queued → to_be_confirmed na máquina, e máquina available → reserved apontando o item.</summary>
    Task OfferAsync(Guid itemId, Guid machineId, DateTime offeredAt, DateTime expiresAt, CancellationToken cancellationToken);
}

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

    Task CommitAsync();
}
