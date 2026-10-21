using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Cicclo.Api.Platform.Http;

namespace Cicclo.Api.Wallet.ListTransactions;

/// <summary>Lançamento financeiro da wallet demo.</summary>
public sealed record TransactionResponse(
    [property: Required, Description("Identificador do lançamento.")] Guid TransactionId,
    [property: Required, Description("Booking que originou o lançamento.")] Guid BookingId,
    [property: Required, Description("Item do booking quando o lançamento se refere a uma única execução; null no débito, que pertence ao booking inteiro.")] Guid? ItemId,
    [property: Required, Description("Tipo do lançamento: debit (cobrança de uma compra). Novos tipos só existem quando o caso de uso correspondente for entregue.")] TransactionKindContract Kind,
    [property: Required, Range(1, 999_999_999_999), Description("Valor do lançamento em centavos inteiros (int64), sempre positivo; o sentido vem de kind.")] long AmountCents,
    [property: Required, Range(0, 999_999_999_999), Description("Saldo da wallet logo depois deste lançamento, em centavos inteiros (int64); histórico, não é o saldo atual.")] long BalanceAfterCents,
    [property: Required, Description("Instante UTC do lançamento.")] DateTime CreatedAt)
{
    public static TransactionResponse From(LedgerEntryView entry) =>
        new(
            entry.TransactionId,
            entry.BookingId,
            entry.ItemId,
            entry.Kind switch
            {
                LedgerEntryKind.Debit => TransactionKindContract.Debit,
                _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Tipo de lançamento sem contrato HTTP."),
            },
            MoneyCents.FromDecimal(entry.Amount),
            MoneyCents.FromDecimal(entry.BalanceAfter),
            entry.CreatedAt);
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionKindContract>))]
public enum TransactionKindContract
{
    [JsonStringEnumMemberName("debit")]
    Debit,
}
