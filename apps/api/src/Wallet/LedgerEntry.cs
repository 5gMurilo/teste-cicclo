namespace Cicclo.Api.Wallet;

/// <summary>Tipos de lançamento existentes nesta etapa; o estorno chega com o cancelamento.</summary>
public enum LedgerEntryKind
{
    Debit,
}

/// <summary>
/// Lançamento imutável da wallet (tabela wallet_transactions): valor sempre positivo, saldo resultante não negativo.
/// O débito pertence ao booking inteiro, por isso não aponta item.
/// </summary>
public sealed class LedgerEntry(
    Guid id,
    Guid walletId,
    Guid bookingId,
    Guid? bookingItemId,
    LedgerEntryKind kind,
    decimal amount,
    decimal balanceAfter,
    DateTime createdAt)
{
    public Guid Id { get; private set; } = id;

    public Guid WalletId { get; private set; } = walletId;

    public Guid BookingId { get; private set; } = bookingId;

    public Guid? BookingItemId { get; private set; } = bookingItemId;

    public LedgerEntryKind Kind { get; private set; } = kind;

    public decimal Amount { get; private set; } = amount;

    public decimal BalanceAfter { get; private set; } = balanceAfter;

    public DateTime CreatedAt { get; private set; } = createdAt;
}
