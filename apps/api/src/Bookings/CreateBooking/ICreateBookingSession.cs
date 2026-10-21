using Cicclo.Api.Laundry;
using Cicclo.Api.Wallet;

namespace Cicclo.Api.Bookings.CreateBooking;

/// <summary>Item persistido de uma compra, com o preço histórico cobrado e o estado atual.</summary>
public sealed record BookingItemSnapshot(Guid ItemId, string ServiceCode, decimal UnitPrice, BookingItemStatus Status);

/// <summary>Compra confirmada encontrada pela chave de idempotência.</summary>
public sealed record StoredBooking(
    Guid BookingId,
    string RequestFingerprint,
    decimal TotalAmount,
    DateTime CreatedAt,
    IReadOnlyList<BookingItemSnapshot> Items);

/// <summary>Abre uma sessão transacional nova por tentativa; nunca reaproveita uma transação abortada.</summary>
public interface ICreateBookingSessions
{
    Task<ICreateBookingSession> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Uma transação de compra. Contrato comum a toda implementação: as chamadas seguem a ordem de locks
/// wallet → serviços por código → máquinas por tipo/id → itens; nada é visível a outras transações antes de
/// <see cref="CommitAsync"/>; descartar a sessão sem commit desfaz tudo. Falhas com significado para o
/// caso de uso chegam como <see cref="CreateBookingStoreException"/>; as demais propagam sem tradução.
/// </summary>
public interface ICreateBookingSession : IAsyncDisposable
{
    /// <summary>Operações de fila/oferta na mesma transação, para a ativação inicial confirmar junto do débito.</summary>
    IQueueSession Queue { get; }

    /// <summary>Bloqueia a linha da wallet até o fim da transação e devolve o saldo atual.</summary>
    Task<decimal> LockWalletAsync(Guid walletId, CancellationToken cancellationToken);

    /// <summary>Compra confirmada da wallet com a chave, ou null. Chamado com a wallet já bloqueada.</summary>
    Task<StoredBooking?> FindByKeyAsync(Guid walletId, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Bloqueia os serviços pedidos e as máquinas dos seus tipos e devolve o catálogo protegido; códigos inexistentes são omitidos.</summary>
    Task<IReadOnlyList<CatalogEntry>> LockCatalogAsync(IReadOnlyCollection<string> serviceCodes, CancellationToken cancellationToken);

    /// <summary>Débito condicional ao saldo; devolve o novo saldo ou null sem alterar nada quando insuficiente.</summary>
    Task<decimal?> TryDebitAsync(Guid walletId, decimal amount, DateTime updatedAt, CancellationToken cancellationToken);

    /// <summary>Grava booking, itens e o débito do ledger na mesma transação.</summary>
    Task AddAsync(Booking booking, IReadOnlyList<BookingItem> items, LedgerEntry debit, CancellationToken cancellationToken);

    /// <summary>Confirma a transação. Falha aqui não prova rollback: chega como CommitOutcomeUnknown.</summary>
    Task CommitAsync();

    /// <summary>
    /// Bookings anteriores que esta compra revisou (oferta vencida devolvida pela ativação), com a revisão atribuída na
    /// transação; vazio até <see cref="CommitAsync"/> retornar com sucesso. A compra nova não entra: nasce na revisão inicial.
    /// </summary>
    IReadOnlyList<RevisedBooking> CommittedRevisions { get; }
}

public enum CreateBookingStoreFailure
{
    /// <summary>Deadlock ou falha de serialização: a transação foi abortada e repetir a unidade inteira é seguro.</summary>
    RetryableConflict,

    /// <summary>A unique wallet+chave recusou a inserção: a transação foi abortada; outra confirmou a chave.</summary>
    IdempotencyKeyTaken,

    /// <summary>O commit foi tentado sem confirmação: a compra pode ou não ter sido persistida.</summary>
    CommitOutcomeUnknown,
}

public sealed class CreateBookingStoreException(CreateBookingStoreFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public CreateBookingStoreFailure Failure { get; } = failure;
}
