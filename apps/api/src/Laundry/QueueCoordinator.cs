using Cicclo.Api.Bookings;
using Cicclo.Api.Platform;
using Microsoft.Extensions.Options;

namespace Cicclo.Api.Laundry;

public enum ActivationOutcome
{
    /// <summary>O item pendente entrou na fila e, havendo máquina livre sem ninguém à frente, recebeu a oferta.</summary>
    Activated,

    /// <summary>O item já estava ativo (fila, oferta ou execução): nada mudou, nem ticket novo.</summary>
    AlreadyActive,

    NotFound,
}

/// <summary>Estado do item depois da decisão: máquina e prazo na oferta, posição derivada na fila.</summary>
public sealed record ItemPlacement(Guid ItemId, BookingItemStatus Status, Guid? MachineId, DateTime? ExpiresAt, int? QueuePosition);

/// <summary>Oferta persistida nesta transação; queued_at permite medir a espera real na fila.</summary>
public sealed record MachineOffer(Guid ItemId, Guid MachineId, DateTime QueuedAt, DateTime OfferedAt, DateTime ExpiresAt);

public sealed record ActivationResult(ActivationOutcome Outcome, ItemPlacement? Item, IReadOnlyList<MachineOffer> Offers);

/// <summary>
/// Coordenador de fila/oferta que opera na transação de quem chama, sem abrir nem confirmar transação própria:
/// bloqueia as máquinas do tipo e depois os itens, captura o instante depois dos locks, dá ticket novo ao item ativado
/// e oferta as máquinas livres aos primeiros da fila (FIFO por ticket/id), com prazo now + timeout.
/// </summary>
public sealed class QueueCoordinator(TimeProvider time, IOptions<LaundryOptions> options)
{
    private TimeSpan OfferTimeout => TimeSpan.FromSeconds(options.Value.ConfirmationTimeoutSeconds);

    public async Task<ActivationResult> ActivateAsync(IQueueSession session, Guid itemId, CancellationToken cancellationToken)
    {
        if (await session.LocateItemAsync(itemId, cancellationToken) is not { } location)
        {
            return new(ActivationOutcome.NotFound, null, []);
        }

        var type = location.MachineType;
        var machines = await session.LockMachinesAsync(type, cancellationToken);
        var items = (await session.LockItemsAsync(type, itemId, cancellationToken)).ToList();
        var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
        var target = items.SingleOrDefault(item => item.ItemId == itemId);
        if (target is null)
        {
            return new(ActivationOutcome.NotFound, null, []);
        }

        if (target.Status != BookingItemStatus.PendingActivation)
        {
            return new(ActivationOutcome.AlreadyActive, Placement(itemId, items), []);
        }

        var ticket = await session.NextQueueTicketAsync(cancellationToken);
        await session.EnqueueAsync(itemId, ticket, now, cancellationToken);
        items[items.IndexOf(target)] = target with { Status = BookingItemStatus.Queued, Ticket = ticket, QueuedAt = now };
        var offers = await OfferFreeMachinesAsync(session, machines, items, now, cancellationToken);
        return new(ActivationOutcome.Activated, Placement(itemId, items), offers);
    }

    /// <summary>Oferta as máquinas livres do tipo aos primeiros da fila; usado quando uma máquina é liberada.</summary>
    public async Task<IReadOnlyList<MachineOffer>> PromoteAsync(IQueueSession session, MachineType type, CancellationToken cancellationToken)
    {
        var machines = await session.LockMachinesAsync(type, cancellationToken);
        var items = (await session.LockItemsAsync(type, null, cancellationToken)).ToList();
        var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
        return await OfferFreeMachinesAsync(session, machines, items, now, cancellationToken);
    }

    /// <summary>Aplica as ofertas e atualiza o retrato em memória dos itens bloqueados.</summary>
    private async Task<IReadOnlyList<MachineOffer>> OfferFreeMachinesAsync(
        IQueueSession session, IReadOnlyList<MachineSlot> machines, List<LaundryItem> items, DateTime now, CancellationToken cancellationToken)
    {
        var offers = new List<MachineOffer>();
        foreach (var pairing in OfferPolicy.Pair(machines, Queue(items)))
        {
            var expiresAt = OfferPolicy.ExpiresAt(now, OfferTimeout);
            await session.OfferAsync(pairing.ItemId, pairing.MachineId, now, expiresAt, cancellationToken);
            var index = items.FindIndex(item => item.ItemId == pairing.ItemId);
            items[index] = items[index] with { Status = BookingItemStatus.ToBeConfirmed, MachineId = pairing.MachineId, ExpiresAt = expiresAt };
            offers.Add(new(pairing.ItemId, pairing.MachineId, items[index].QueuedAt!.Value, now, expiresAt));
        }

        return offers;
    }

    private static ItemPlacement Placement(Guid itemId, IReadOnlyList<LaundryItem> items)
    {
        var item = items.Single(candidate => candidate.ItemId == itemId);
        return new(item.ItemId, item.Status, item.MachineId, item.ExpiresAt, OfferPolicy.Position(itemId, Queue(items)));
    }

    private static IEnumerable<QueuedItem> Queue(IEnumerable<LaundryItem> items) =>
        items.Where(item => item.Status == BookingItemStatus.Queued).Select(item => new QueuedItem(item.ItemId, item.Ticket!.Value));
}
