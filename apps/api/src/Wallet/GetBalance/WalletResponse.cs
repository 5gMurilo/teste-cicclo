using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Cicclo.Api.Wallet.GetBalance;

/// <summary>Saldo atual da wallet demo.</summary>
public sealed record WalletResponse(
    [property: Required, Range(0, 999_999_999_999), Description("Saldo em centavos inteiros (int64), nunca negativo.")] long BalanceCents,
    [property: Required, Description("Moeda ISO 4217; sempre BRL.")] string Currency,
    [property: Required, Description("Última alteração do saldo registrada no banco, em UTC.")] DateTime UpdatedAt);
