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

/// <summary>Oferta vencida devolvida ao fim da fila nesta transação, com ticket novo e sem efeito financeiro.</summary>
public sealed record ExpiredOffer(Guid ItemId, Guid MachineId, DateTime ExpiresAt, long NewTicket, DateTime RequeuedAt);

/// <summary>Mudanças de fila de um tipo de máquina numa transação; só viram fato depois do commit de quem chamou.</summary>
public sealed record QueueChanges(MachineType Type, IReadOnlyList<ExpiredOffer> Expired, IReadOnlyList<MachineOffer> Offers);

public sealed record ActivationResult(
    ActivationOutcome Outcome, ItemPlacement? Item, IReadOnlyList<MachineOffer> Offers, IReadOnlyList<ExpiredOffer> Expired, MachineType? Type = null)
{
    /// <summary>Mudanças de fila do tipo do item, para métricas registradas depois do commit; null se o item não existe.</summary>
    public QueueChanges? Changes => Type is { } type ? new(type, Expired, Offers) : null;
}

/// <summary>
/// Coordenador de fila/oferta que opera na transação de quem chama, sem abrir nem confirmar transação própria:
/// bloqueia as máquinas e depois os itens, captura o instante depois dos locks, devolve ao fim da fila as ofertas
/// vencidas (now &gt;= expiresAt), dá ticket novo ao item ativado e oferta as máquinas livres aos primeiros da fila
/// (FIFO por ticket/id), com prazo now + timeout. Ativação, confirmação vencida e worker compartilham este algoritmo.
/// </summary>
public sealed class QueueCoordinator(TimeProvider time, IOptions<LaundryOptions> options)
{
    private TimeSpan OfferTimeout => TimeSpan.FromSeconds(options.Value.ConfirmationTimeoutSeconds);

    public async Task<ActivationResult> ActivateAsync(IQueueSession session, Guid itemId, CancellationToken cancellationToken)
    {
        if (await session.LocateItemAsync(itemId, cancellationToken) is not { } location)
        {
            return new(ActivationOutcome.NotFound, null, [], []);
        }

        var type = location.MachineType;
        var machines = (await session.LockMachinesAsync(type, cancellationToken)).ToList();
        var items = (await session.LockItemsAsync(type, itemId, cancellationToken)).ToList();
        var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
        var target = items.SingleOrDefault(item => item.ItemId == itemId);
        if (target is null)
        {
            return new(ActivationOutcome.NotFound, null, [], []);
        }

        if (target.Status != BookingItemStatus.PendingActivation)
        {
            return new(ActivationOutcome.AlreadyActive, Placement(itemId, items), [], [], type);
        }

        // A oferta vencida volta à fila antes de o item recém-ativado entrar: ela venceu antes desta chegada.
        var expired = await ExpireDueAsync(session, machines, items, now, cancellationToken);
        var ticket = await session.NextQueueTicketAsync(cancellationToken);
        await session.EnqueueAsync(itemId, ticket, now, cancellationToken);
        items[items.FindIndex(item => item.ItemId == itemId)] = target with { Status = BookingItemStatus.Queued, Ticket = ticket, QueuedAt = now };
        var offers = await OfferFreeMachinesAsync(session, machines, items, now, cancellationToken);
        return new(ActivationOutcome.Activated, Placement(itemId, items), offers, expired, type);
    }

    /// <summary>
    /// Reconcilia os tipos indicados: bloqueia as máquinas de todos (tipo, id) antes dos itens, devolve ao fim da fila
    /// as ofertas vencidas e oferta as máquinas livres. Sem nada vencido nem máquina livre com fila, não grava nada.
    /// </summary>
    public async Task<IReadOnlyList<QueueChanges>> ReconcileAsync(IQueueSession session, IReadOnlyList<MachineType> types, CancellationToken cancellationToken)
    {
        var ordered = types.Distinct().OrderBy(type => type.ToString(), StringComparer.Ordinal).ToList();
        var machines = new Dictionary<MachineType, List<MachineSlot>>();
        foreach (var type in ordered)
        {
            machines[type] = [.. await session.LockMachinesAsync(type, cancellationToken)];
        }

        var items = new Dictionary<MachineType, List<LaundryItem>>();
        foreach (var type in ordered)
        {
            items[type] = [.. await session.LockItemsAsync(type, null, cancellationToken)];
        }

        var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
        var changes = new List<QueueChanges>();
        foreach (var type in ordered)
        {
            var expired = await ExpireDueAsync(session, machines[type], items[type], now, cancellationToken);
            var offers = await OfferFreeMachinesAsync(session, machines[type], items[type], now, cancellationToken);
            changes.Add(new(type, expired, offers));
        }

        return changes;
    }

    /// <summary>Estado atual de um item entre os itens bloqueados do seu tipo, com a posição derivada da fila.</summary>
    public static ItemPlacement Placement(Guid itemId, IReadOnlyList<LaundryItem> items)
    {
        var item = items.Single(candidate => candidate.ItemId == itemId);
        return new(item.ItemId, item.Status, item.MachineId, item.ExpiresAt, OfferPolicy.Position(itemId, Queue(items)));
    }

    /// <summary>
    /// Ofertas vencidas, na ordem dos tickets antigos, ganham ticket novo no fim da fila; a máquina volta a available.
    /// Nada é cobrado nem cancelado.
    /// </summary>
    private static async Task<IReadOnlyList<ExpiredOffer>> ExpireDueAsync(
        IQueueSession session, List<MachineSlot> machines, List<LaundryItem> items, DateTime now, CancellationToken cancellationToken)
    {
        var due = items
            .Where(item => item.Status == BookingItemStatus.ToBeConfirmed && OfferPolicy.IsExpired(item.ExpiresAt!.Value, now))
            .OrderBy(item => item.Ticket)
            .ToList();
        var expired = new List<ExpiredOffer>();
        foreach (var item in due)
        {
            var machineId = item.MachineId!.Value;
            var ticket = await session.NextQueueTicketAsync(cancellationToken);
            await session.RequeueExpiredAsync(item.ItemId, machineId, ticket, now, cancellationToken);
            items[items.IndexOf(item)] = item with { Status = BookingItemStatus.Queued, Ticket = ticket, QueuedAt = now, MachineId = null, ExpiresAt = null };
            var machine = machines.FindIndex(slot => slot.MachineId == machineId);
            machines[machine] = machines[machine] with { State = MachineState.Available, ActiveItemId = null };
            expired.Add(new(item.ItemId, machineId, item.ExpiresAt!.Value, ticket, now));
        }

        return expired;
    }

    /// <summary>Aplica as ofertas e atualiza o retrato em memória de máquinas e itens bloqueados.</summary>
    private async Task<IReadOnlyList<MachineOffer>> OfferFreeMachinesAsync(
        IQueueSession session, List<MachineSlot> machines, List<LaundryItem> items, DateTime now, CancellationToken cancellationToken)
    {
        var offers = new List<MachineOffer>();
        foreach (var pairing in OfferPolicy.Pair(machines, Queue(items)))
        {
            var expiresAt = OfferPolicy.ExpiresAt(now, OfferTimeout);
            await session.OfferAsync(pairing.ItemId, pairing.MachineId, now, expiresAt, cancellationToken);
            var index = items.FindIndex(item => item.ItemId == pairing.ItemId);
            items[index] = items[index] with { Status = BookingItemStatus.ToBeConfirmed, MachineId = pairing.MachineId, ExpiresAt = expiresAt };
            var machine = machines.FindIndex(slot => slot.MachineId == pairing.MachineId);
            machines[machine] = machines[machine] with { State = MachineState.Reserved, ActiveItemId = pairing.ItemId };
            offers.Add(new(pairing.ItemId, pairing.MachineId, items[index].QueuedAt!.Value, now, expiresAt));
        }

        return offers;
    }

    private static IEnumerable<QueuedItem> Queue(IEnumerable<LaundryItem> items) =>
        items.Where(item => item.Status == BookingItemStatus.Queued).Select(item => new QueuedItem(item.ItemId, item.Ticket!.Value));
}
