namespace Cicclo.Api.Laundry;

/// <summary>Máquina do tipo, lida sob lock: só available sem item ativo recebe oferta.</summary>
public sealed record MachineSlot(Guid MachineId, MachineState State, Guid? ActiveItemId);

/// <summary>Item na fila de um tipo: o ticket ordena, o id desempata.</summary>
public sealed record QueuedItem(Guid ItemId, long Ticket);

/// <summary>Par item→máquina escolhido pela promoção FIFO.</summary>
public sealed record OfferPairing(Guid ItemId, Guid MachineId);

/// <summary>
/// Regras puras de fila e oferta: FIFO por ticket (id como desempate), máquina livre por id, prazo da oferta e
/// posição derivada. Sem I/O: quem chama já leu máquinas e itens sob lock.
/// </summary>
public static class OfferPolicy
{
    /// <summary>Instante da decisão em UTC, truncado ao microssegundo do timestamptz.</summary>
    public static DateTime DecisionInstant(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    public static DateTime ExpiresAt(DateTime offeredAt, TimeSpan timeout) => offeredAt + timeout;

    /// <summary>
    /// Pareia as máquinas livres (por id) com os primeiros da fila (por ticket, depois id). Nenhum item é pulado:
    /// o k-ésimo da fila só recebe oferta se houver k máquinas livres.
    /// </summary>
    public static IReadOnlyList<OfferPairing> Pair(IEnumerable<MachineSlot> machines, IEnumerable<QueuedItem> queue) =>
        [.. machines
            .Where(machine => machine.State == MachineState.Available && machine.ActiveItemId is null)
            .OrderBy(machine => Key(machine.MachineId), StringComparer.Ordinal)
            .Zip(Ordered(queue), (machine, item) => new OfferPairing(item.ItemId, machine.MachineId))];

    /// <summary>Posição 1-based do item na fila do tipo, derivada de ticket/id; null se não está na fila.</summary>
    public static int? Position(Guid itemId, IEnumerable<QueuedItem> queue)
    {
        var index = Ordered(queue).FindIndex(item => item.ItemId == itemId);
        return index < 0 ? null : index + 1;
    }

    private static List<QueuedItem> Ordered(IEnumerable<QueuedItem> queue) =>
        [.. queue.OrderBy(item => item.Ticket).ThenBy(item => Key(item.ItemId), StringComparer.Ordinal)];

    // Mesma ordem do uuid no PostgreSQL: bytes na ordem canônica = texto hexadecimal minúsculo.
    private static string Key(Guid id) => id.ToString("D");
}
