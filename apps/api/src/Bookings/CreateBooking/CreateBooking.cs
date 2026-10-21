using System.Diagnostics;
using Cicclo.Api.Laundry;
using Cicclo.Api.Wallet;
using Cicclo.Api.Wallet.ReadCaching;

namespace Cicclo.Api.Bookings.CreateBooking;

public enum CreateBookingOutcome
{
    /// <summary>Esta chamada confirmou a compra e o débito.</summary>
    Created,

    /// <summary>A chave já tinha a mesma intenção confirmada; nada foi cobrado de novo.</summary>
    Replayed,

    /// <summary>Recusa de negócio sem nenhuma mutação; a chave não é consumida.</summary>
    Rejected,

    /// <summary>Não há evidência de que a compra foi persistida: falha recuperável, repetir com a mesma chave.</summary>
    Inconclusive,
}

/// <summary>Projeção comum de criação e replay: cobrança histórica, itens atuais e saldo atual da wallet.</summary>
public sealed record BookingSnapshot(
    Guid BookingId,
    decimal ChargedAmount,
    decimal RemainingBalance,
    DateTime CreatedAt,
    IReadOnlyList<BookingItemSnapshot> Items);

public sealed record CreateBookingResult(
    CreateBookingOutcome Outcome,
    BookingSnapshot? Booking = null,
    BookingRejection? Rejection = null,
    long? CalculatedTotalCents = null);

/// <summary>
/// Caso de uso interno de compra com a wallet demo, numa única transação:
/// lock da wallet → replay pela chave → catálogo/capacidade sob lock → preço autoritativo →
/// débito condicional → booking/itens/débito → ativação inicial → commit. O replay precede toda regra dinâmica.
/// Itens nascem pending_activation; na mesma transação, compra de um item ativa esse item, lote com preferência ativa
/// a primeira ocorrência do serviço preferido e lote sem preferência fica todo pendente. Falha na ativação reverte tudo.
/// Só uma compra criada (inclusive a reconhecida depois de commit desconhecido) invalida o cache de leitura da wallet,
/// depois que a sessão foi descartada; replay e recusa não mudaram saldo e não invalidam.
/// </summary>
public sealed partial class CreateBooking(
    ICreateBookingSessions sessions,
    QueueCoordinator coordinator,
    LaundryMetrics laundryMetrics,
    TimeProvider time,
    WalletCacheInvalidation walletCache,
    ILogger<CreateBooking> logger)
{
    /// <summary>Tentativas totais da unidade inteira quando o rollback é conhecido.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Fonte dos spans de negócio da compra (booking.create).</summary>
    public static readonly ActivitySource Activities = new(BookingMetrics.MeterName);

    public async Task<CreateBookingResult> ExecuteAsync(Guid idempotencyKey, BookingIntent intent, CancellationToken cancellationToken)
    {
        using var activity = Activities.StartActivity("booking.create");
        var result = await ExecuteWithRetriesAsync(idempotencyKey, intent, cancellationToken);
        if (result.Outcome == CreateBookingOutcome.Created)
        {
            await walletCache.AfterCommitAsync();
        }

        activity?.SetTag("booking.outcome", result.Outcome.ToString());
        activity?.SetTag("booking.rejection", result.Rejection?.ToString());
        activity?.SetTag("booking.id", result.Booking?.BookingId.ToString());
        return result;
    }

    private async Task<CreateBookingResult> ExecuteWithRetriesAsync(Guid idempotencyKey, BookingIntent intent, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AttemptAsync(idempotencyKey, intent, cancellationToken);
            }
            catch (CreateBookingStoreException exception) when (
                exception.Failure is CreateBookingStoreFailure.RetryableConflict or CreateBookingStoreFailure.IdempotencyKeyTaken
                && attempt < MaxAttempts)
            {
                // A sessão já foi descartada (rollback conhecido); a próxima tentativa recomeça em contexto novo.
                LogRetry(exception.Failure, attempt);
            }
        }
    }

    private async Task<CreateBookingResult> AttemptAsync(Guid idempotencyKey, BookingIntent intent, CancellationToken cancellationToken)
    {
        Booking booking;
        BookingSnapshot snapshot;
        ActivationResult? activation;
        var commitOutcomeUnknown = false;
        await using (var session = await sessions.OpenAsync(cancellationToken))
        {
            var balance = await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
            var existing = await session.FindByKeyAsync(DemoWallet.Id, idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                return Replay(existing, intent, balance);
            }

            var catalog = await session.LockCatalogAsync([.. intent.ServiceCodes.Distinct(StringComparer.Ordinal)], cancellationToken);
            var pricing = BookingPricing.Price(intent, catalog);
            if (pricing.Price is not { } price)
            {
                LogRejected(pricing.Rejection!.Value);
                return new(CreateBookingOutcome.Rejected, Rejection: pricing.Rejection, CalculatedTotalCents: pricing.CalculatedTotalCents);
            }

            // Instante da decisão: depois dos locks, truncado à precisão do timestamptz.
            var instant = time.GetUtcNow();
            var now = instant.UtcDateTime.AddTicks(-(instant.UtcDateTime.Ticks % 10));
            var remaining = await session.TryDebitAsync(DemoWallet.Id, price.Total, now, cancellationToken);
            if (remaining is null)
            {
                LogRejected(BookingRejection.InsufficientBalance);
                return new(CreateBookingOutcome.Rejected, Rejection: BookingRejection.InsufficientBalance);
            }

            booking = new Booking(Guid.CreateVersion7(instant), DemoWallet.Id, idempotencyKey, intent.Fingerprint, price.Total, now);
            var items = price.Items
                .Select(item => new BookingItem(Guid.CreateVersion7(instant), booking.Id, item.ServiceCode, item.UnitPrice, BookingItemStatus.PendingActivation))
                .ToList();
            var debit = new LedgerEntry(Guid.CreateVersion7(instant), DemoWallet.Id, booking.Id, null, LedgerEntryKind.Debit, price.Total, remaining.Value, now);
            await session.AddAsync(booking, items, debit, cancellationToken);
            activation = await ActivateInitialAsync(session, intent, items, cancellationToken);
            snapshot = new(
                booking.Id,
                booking.TotalAmount,
                remaining.Value,
                now,
                Ordered(items.Select(item => new BookingItemSnapshot(
                    item.Id, item.ServiceCode, item.UnitPrice, item.Id == activation?.Item!.ItemId ? activation.Item.Status : item.Status))));

            try
            {
                await session.CommitAsync();
            }
            catch (CreateBookingStoreException exception) when (exception.Failure == CreateBookingStoreFailure.CommitOutcomeUnknown)
            {
                LogCommitUnknown(exception);
                commitOutcomeUnknown = true;
            }
        }

        // Rollback antes de reconsultar: a sessão original já foi descartada (desfaz a transação se o commit
        // não chegou ao servidor e libera o lock da wallet), então a reconciliação não espera por ela mesma.
        if (commitOutcomeUnknown)
        {
            return await ReconcileAsync(idempotencyKey, intent, booking.Id, cancellationToken);
        }

        if (activation?.Changes is { } changes)
        {
            laundryMetrics.RecordCommitted([changes]);
        }

        LogCreated(booking.Id, snapshot.Items.Count);
        return new(CreateBookingOutcome.Created, snapshot);
    }

    /// <summary>
    /// Ativação inicial na transação da compra: o único item, ou a primeira ocorrência (ordem canônica) do serviço
    /// preferido; sem preferência num lote, nada é ativado. Usa o mesmo coordenador de fila dos comandos e do worker.
    /// </summary>
    private async Task<ActivationResult?> ActivateInitialAsync(
        ICreateBookingSession session, BookingIntent intent, IReadOnlyList<BookingItem> items, CancellationToken cancellationToken)
    {
        var target = items.Count == 1
            ? items[0]
            : items.FirstOrDefault(item => string.Equals(item.ServiceCode, intent.PreferredFirstServiceCode, StringComparison.Ordinal));
        if (target is null)
        {
            return null;
        }

        var activation = await coordinator.ActivateAsync(session.Queue, target.Id, cancellationToken);
        return activation.Outcome == ActivationOutcome.Activated
            ? activation
            : throw new InvalidOperationException($"Ativação inicial do item recém-criado recusada: {activation.Outcome}.");
    }

    /// <summary>
    /// Commit sem confirmação: reconsulta a chave em sessão nova, aberta só depois de a sessão original ser
    /// descartada. O lock da wallet só é obtido depois que a transação original terminou, então a leitura é a decisão final. Sem a compra persistida, devolve
    /// Inconclusive: nunca fabrica sucesso nem tenta de novo com outra chave.
    /// </summary>
    private async Task<CreateBookingResult> ReconcileAsync(Guid idempotencyKey, BookingIntent intent, Guid attemptedBookingId, CancellationToken cancellationToken)
    {
        try
        {
            await using var session = await sessions.OpenAsync(cancellationToken);
            var balance = await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
            var existing = await session.FindByKeyAsync(DemoWallet.Id, idempotencyKey, cancellationToken);
            if (existing is null)
            {
                LogInconclusive();
                return new(CreateBookingOutcome.Inconclusive);
            }

            if (existing.BookingId != attemptedBookingId)
            {
                return Replay(existing, intent, balance);
            }

            LogCreated(existing.BookingId, existing.Items.Count);
            return new(CreateBookingOutcome.Created, ToSnapshot(existing, balance));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReconciliationFailed(exception);
            return new(CreateBookingOutcome.Inconclusive);
        }
    }

    private CreateBookingResult Replay(StoredBooking existing, BookingIntent intent, decimal balance)
    {
        if (!string.Equals(existing.RequestFingerprint, intent.Fingerprint, StringComparison.Ordinal))
        {
            LogRejected(BookingRejection.IdempotencyConflict);
            return new(CreateBookingOutcome.Rejected, Rejection: BookingRejection.IdempotencyConflict);
        }

        LogReplayed(existing.BookingId);
        return new(CreateBookingOutcome.Replayed, ToSnapshot(existing, balance));
    }

    private static BookingSnapshot ToSnapshot(StoredBooking booking, decimal balance) =>
        new(booking.BookingId, booking.TotalAmount, balance, booking.CreatedAt, Ordered(booking.Items));

    /// <summary>Ordem estável e idêntica na criação e no replay: código ordinal, depois id.</summary>
    private static List<BookingItemSnapshot> Ordered(IEnumerable<BookingItemSnapshot> items) =>
        [.. items.OrderBy(item => item.ServiceCode, StringComparer.Ordinal).ThenBy(item => item.ItemId)];

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Compra {BookingId} confirmada com {ItemCount} itens.")]
    private partial void LogCreated(Guid bookingId, int itemCount);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Compra {BookingId} recuperada por replay, sem novo débito.")]
    private partial void LogReplayed(Guid bookingId);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Compra recusada: {Rejection}.")]
    private partial void LogRejected(BookingRejection rejection);

    [LoggerMessage(EventId = 103, Level = LogLevel.Warning, Message = "Tentativa {Attempt} de compra revertida ({Failure}); repetindo a unidade inteira.")]
    private partial void LogRetry(CreateBookingStoreFailure failure, int attempt);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning, Message = "Commit da compra sem confirmação; reconciliando pela chave.")]
    private partial void LogCommitUnknown(Exception exception);

    [LoggerMessage(EventId = 105, Level = LogLevel.Warning, Message = "Compra sem evidência de persistência após commit desconhecido; resultado inconclusivo.")]
    private partial void LogInconclusive();

    [LoggerMessage(EventId = 106, Level = LogLevel.Error, Message = "Falha ao reconciliar compra após commit desconhecido; resultado inconclusivo.")]
    private partial void LogReconciliationFailed(Exception exception);
}
