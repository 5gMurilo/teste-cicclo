namespace Cicclo.Api.Wallet.GetBalance;

/// <summary>Projeção interna do saldo persistido, ainda em decimal.</summary>
public sealed record WalletBalance(decimal Balance, string Currency, DateTime UpdatedAt);
