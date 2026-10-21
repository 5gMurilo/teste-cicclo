namespace Cicclo.Api.Wallet;

/// <summary>Carteira demonstrativa persistida; o saldo é decimal em BRL, nunca centavos de contrato.</summary>
public sealed class WalletAccount(Guid id, string currency, decimal balance, DateTime createdAt, DateTime updatedAt)
{
    public Guid Id { get; private set; } = id;

    public string Currency { get; private set; } = currency;

    public decimal Balance { get; private set; } = balance;

    public DateTime CreatedAt { get; private set; } = createdAt;

    public DateTime UpdatedAt { get; private set; } = updatedAt;
}
