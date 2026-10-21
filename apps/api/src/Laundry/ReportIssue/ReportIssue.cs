using Cicclo.Api.Bookings;
using Cicclo.Api.Wallet;

namespace Cicclo.Api.Laundry.ReportIssue;

public enum ReportIssueOutcome
{
    /// <summary>Relato novo registrado nesta chamada.</summary>
    Reported,

    /// <summary>O item já tinha um relato aberto: ele é devolvido como estava, sem trocar a descrição original.</summary>
    AlreadyReported,

    /// <summary>Item fora de in_progress (não iniciado ou com desfecho): nada mudou.</summary>
    InvalidState,

    /// <summary>Item inexistente ou de outro booking.</summary>
    NotFound,
}

/// <summary>Resultado do relato; IncidentId é o relato aberto do item (novo ou existente).</summary>
public sealed record ReportIssueResult(ReportIssueOutcome Outcome, Guid? IncidentId = null);

/// <summary>
/// Registra o relato de defeito de uma execução em andamento numa transação, na ordem comum de locks: wallet (mutex das
/// escritas) → máquinas do tipo → itens → incidentes. Só in_progress aceita relato; um relato aberto existente é
/// devolvido sem alteração. O relato é só um fato do usuário: não muda item, máquina, saldo nem ledger, e não tem
/// capacidade de confirmar defeito ou conceder estorno.
/// </summary>
public sealed partial class ReportIssue(ILaundrySessions sessions, TimeProvider time, ILogger<ReportIssue> logger)
{
    public async Task<ReportIssueResult> ExecuteAsync(Guid bookingId, Guid itemId, string? description, CancellationToken cancellationToken)
    {
        Guid incidentId;
        await using (var session = await sessions.OpenAsync(cancellationToken))
        {
            await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
            if (await session.LocateItemAsync(itemId, cancellationToken) is not { } location || location.BookingId != bookingId)
            {
                return new(ReportIssueOutcome.NotFound);
            }

            await session.LockMachinesAsync(location.MachineType, cancellationToken);
            var item = (await session.LockItemsAsync(location.MachineType, itemId, cancellationToken)).Single(candidate => candidate.ItemId == itemId);
            var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
            if (item.Status != BookingItemStatus.InProgress)
            {
                LogRefused(itemId, item.Status);
                return new(ReportIssueOutcome.InvalidState);
            }

            if (await session.LockOpenIncidentAsync(itemId, cancellationToken) is { } open)
            {
                return new(ReportIssueOutcome.AlreadyReported, open);
            }

            incidentId = Guid.CreateVersion7(time.GetUtcNow());
            await session.RecordIncidentAsync(
                incidentId,
                item.MachineId ?? throw new InvalidOperationException($"Item {itemId} em execução sem máquina."),
                itemId,
                description,
                now,
                cancellationToken);
            await session.CommitAsync();
        }

        LogReported(itemId, incidentId);
        return new(ReportIssueOutcome.Reported, incidentId);
    }

    [LoggerMessage(EventId = 620, Level = LogLevel.Information, Message = "Relato {IncidentId} registrado para o item {ItemId}, sem efeito financeiro.")]
    private partial void LogReported(Guid itemId, Guid incidentId);

    [LoggerMessage(EventId = 621, Level = LogLevel.Information, Message = "Relato do item {ItemId} recusado no estado {Status}.")]
    private partial void LogRefused(Guid itemId, BookingItemStatus status);
}
