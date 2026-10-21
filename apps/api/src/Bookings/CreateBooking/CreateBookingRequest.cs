using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Cicclo.Api.Bookings.CreateBooking;

/// <summary>
/// Intenção de compra recebida no POST. Preço, saldo e wallet nunca vêm do cliente; propriedades desconhecidas e tipos
/// incorretos são recusados com VALIDATION_ERROR. As regras estruturais são aplicadas por BookingIntent.Create.
/// </summary>
public sealed record CreateBookingRequest(
    [property: Required, MinLength(1), MaxLength(10), Description("Códigos das execuções (1 a 10); repetições são execuções distintas e a ordem não altera preço nem intenção. Ausente, vazio, acima do limite ou com código null é recusado.")]
    IReadOnlyList<string?>? ServiceCodes,
    [property: Required, Range(1, long.MaxValue), Description("Total em centavos que o cliente confirmou; só é comparado com o total calculado no servidor (divergência: 409 PRICE_CHANGED). Nunca é usado como valor cobrado.")]
    long? ExpectedTotalCents,
    [property: Description("Código do lote a ativar primeiro; deve estar em serviceCodes. Opcional: omitido ou null é ausência de preferência (lote com mais de um item fica todo pending_activation). Compra de um único item sempre o ativa.")]
    string? PreferredFirstServiceCode = null);
