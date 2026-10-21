using System.Runtime.CompilerServices;
using Cicclo.Api.Bookings;
using Cicclo.Api.Services.GetCatalog;

namespace Cicclo.Api.Laundry.Notifications;

/// <summary>
/// Nomes wire aprovados dos avisos realtime (E09). Com ponto e snake_case, nunca derivados de nomes de método C#.
/// </summary>
public static class LaundryEventNames
{
    public const string BookingUpdated = "booking.updated";
    public const string QueueUpdated = "queue.updated";
    public const string MachineOffered = "machine.offered";
    public const string MachineOfferExpired = "machine.offer_expired";
    public const string WalletUpdated = "wallet.updated";
    public const string MachineFailed = "machine.failed";
}

/// <summary>
/// Aviso de uma mudança já confirmada: nome wire e payload tipado, criados somente pelas fábricas abaixo, que fixam o par
/// nome/payload da tabela de eventos E09 e recusam datas fora de UTC. Não é snapshot, comando nem evento persistido; o
/// REST continua a fonte autoritativa para recuperar o estado.
/// </summary>
public sealed class LaundryNotification
{
    private LaundryNotification(string eventName, object payload)
    {
        EventName = eventName;
        Payload = payload;
    }

    public string EventName { get; }

    public object Payload { get; }

    /// <summary>Booking criado ou projeção alterada; version é a revisão confirmada no commit (D09), nova compra em 1.</summary>
    public static LaundryNotification BookingUpdated(Guid bookingId, long version, DateTime occurredAt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, BookingRevision.Initial);
        return new(LaundryEventNames.BookingUpdated, new BookingUpdatedPayload(Guid.CreateVersion7(), Id(bookingId), version, Utc(occurredAt)));
    }

    /// <summary>Entrada, saída ou reordenação de itens queued do tipo; posições são recuperadas por REST.</summary>
    public static LaundryNotification QueueUpdated(MachineType machineType, DateTime occurredAt) =>
        new(LaundryEventNames.QueueUpdated, new QueueUpdatedPayload(Contract(machineType), Utc(occurredAt)));

    public static LaundryNotification MachineOffered(Guid itemId, Guid machineId, DateTime expiresAt, DateTime occurredAt) =>
        new(LaundryEventNames.MachineOffered, new MachineOfferedPayload(Id(itemId), Id(machineId), Utc(expiresAt), Utc(occurredAt)));

    public static LaundryNotification MachineOfferExpired(Guid itemId, DateTime occurredAt) =>
        new(LaundryEventNames.MachineOfferExpired, new MachineOfferExpiredPayload(Id(itemId), Utc(occurredAt)));

    /// <summary>Débito ou refund confirmado; sem saldo, para que nenhum valor transmitido vire comando ou verdade.</summary>
    public static LaundryNotification WalletUpdated(DateTime occurredAt) =>
        new(LaundryEventNames.WalletUpdated, new WalletUpdatedPayload(Utc(occurredAt)));

    /// <summary>Falha confirmada e persistida, real ou sintética.</summary>
    public static LaundryNotification MachineFailed(Guid machineId, DateTime occurredAt) =>
        new(LaundryEventNames.MachineFailed, new MachineFailedPayload(Id(machineId), Utc(occurredAt)));

    private static Guid Id(Guid id, [CallerArgumentExpression(nameof(id))] string? name = null) =>
        id == Guid.Empty ? throw new ArgumentException("Identificador vazio não descreve uma mudança persistida.", name) : id;

    private static DateTime Utc(DateTime value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException("Datas dos avisos são sempre UTC.", name);

    private static MachineTypeContract Contract(MachineType machineType) =>
        machineType switch
        {
            MachineType.Washer => MachineTypeContract.Washer,
            MachineType.Dryer => MachineTypeContract.Dryer,
            _ => throw new ArgumentOutOfRangeException(nameof(machineType), machineType, "Tipo de máquina sem contrato de aviso."),
        };
}

public sealed record BookingUpdatedPayload(Guid EventId, Guid BookingId, long Version, DateTime OccurredAt);

public sealed record QueueUpdatedPayload(MachineTypeContract MachineType, DateTime OccurredAt);

public sealed record MachineOfferedPayload(Guid ItemId, Guid MachineId, DateTime ExpiresAt, DateTime OccurredAt);

public sealed record MachineOfferExpiredPayload(Guid ItemId, DateTime OccurredAt);

public sealed record WalletUpdatedPayload(DateTime OccurredAt);

public sealed record MachineFailedPayload(Guid MachineId, DateTime OccurredAt);

/// <summary>
/// Porta de saída dos avisos. Publica na ordem recebida, depois do commit e fora de transação/lock (responsabilidade do
/// chamador); a primeira falha de transporte interrompe o lote e chega ao chamador, que decide registrar e degradar.
/// </summary>
public interface ILaundryNotifications
{
    Task PublishAsync(IReadOnlyList<LaundryNotification> notifications, CancellationToken cancellationToken);
}
