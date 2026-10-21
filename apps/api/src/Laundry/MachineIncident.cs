namespace Cicclo.Api.Laundry;

/// <summary>Estado de um incidente: relatado pelo usuário ou confirmado como defeito pela simulação.</summary>
public enum IncidentStatus
{
    Reported,
    Confirmed,
}

/// <summary>
/// Relato de defeito de uma execução (tabela machine_incidents): fato do usuário, sem efeito financeiro. No máximo um
/// relato aberto (reported) por item; a descrição é opcional e limitada a 1000 caracteres.
/// </summary>
public sealed class MachineIncident(Guid id, Guid machineId, Guid bookingItemId, IncidentStatus status, string? description, DateTime reportedAt)
{
    public Guid Id { get; private set; } = id;

    public Guid MachineId { get; private set; } = machineId;

    public Guid BookingItemId { get; private set; } = bookingItemId;

    public IncidentStatus Status { get; private set; } = status;

    public string? Description { get; private set; } = description;

    /// <summary>Instante UTC do relato.</summary>
    public DateTime ReportedAt { get; private set; } = reportedAt;
}
