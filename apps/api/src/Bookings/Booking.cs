namespace Cicclo.Api.Bookings;

/// <summary>
/// Contratação confirmada (tabela service_requests): só existe junto do débito; a chave de idempotência
/// e o fingerprint da intenção permitem recuperar a compra sem repetir a cobrança.
/// </summary>
public sealed class Booking(Guid id, Guid walletId, Guid idempotencyKey, string requestFingerprint, decimal totalAmount, DateTime createdAt)
{
    public Guid Id { get; private set; } = id;

    public Guid WalletId { get; private set; } = walletId;

    public Guid IdempotencyKey { get; private set; } = idempotencyKey;

    public string RequestFingerprint { get; private set; } = requestFingerprint;

    public decimal TotalAmount { get; private set; } = totalAmount;

    public DateTime CreatedAt { get; private set; } = createdAt;
}
