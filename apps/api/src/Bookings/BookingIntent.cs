using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cicclo.Api.Bookings;

public sealed record BookingIntentResult(BookingIntent? Intent, BookingRejection? Rejection);

/// <summary>
/// Intenção de compra normalizada e imutável. Códigos ficam em ordem ordinal preservando a multiplicidade;
/// preferência omitida ou null é ausência. O fingerprint é o SHA-256 de uma representação canônica
/// (códigos, preferência e total esperado), nunca dos bytes do JSON recebido.
/// </summary>
public sealed class BookingIntent
{
    private const string CanonicalVersion = "cicclo.booking-intent.v1";

    private BookingIntent(IReadOnlyList<string> serviceCodes, string? preferredFirstServiceCode, long expectedTotalCents)
    {
        ServiceCodes = serviceCodes;
        PreferredFirstServiceCode = preferredFirstServiceCode;
        ExpectedTotalCents = expectedTotalCents;
        Fingerprint = ComputeFingerprint(serviceCodes, preferredFirstServiceCode, expectedTotalCents);
    }

    public IReadOnlyList<string> ServiceCodes { get; }

    public string? PreferredFirstServiceCode { get; }

    /// <summary>Consentimento de preço do cliente; nunca é usado como valor cobrado.</summary>
    public long ExpectedTotalCents { get; }

    public string Fingerprint { get; }

    /// <summary>
    /// Valida a estrutura antes da seleção: total não positivo ou código nulo é VALIDATION_ERROR; lote ausente,
    /// vazio, acima de <paramref name="maxServicesPerBooking"/> ou preferência fora do lote é INVALID_SERVICE_SELECTION.
    /// Códigos não são corrigidos (sem trim nem mudança de caixa): desconhecidos são recusados pelo catálogo.
    /// </summary>
    public static BookingIntentResult Create(
        IReadOnlyList<string?>? serviceCodes,
        string? preferredFirstServiceCode,
        long expectedTotalCents,
        int maxServicesPerBooking)
    {
        if (expectedTotalCents <= 0 || (serviceCodes is not null && serviceCodes.Any(code => code is null)))
        {
            return new(null, BookingRejection.ValidationError);
        }

        if (serviceCodes is null || serviceCodes.Count == 0 || serviceCodes.Count > maxServicesPerBooking)
        {
            return new(null, BookingRejection.InvalidServiceSelection);
        }

        var codes = serviceCodes.Select(code => code!).Order(StringComparer.Ordinal).ToArray();
        if (preferredFirstServiceCode is not null && !codes.Contains(preferredFirstServiceCode, StringComparer.Ordinal))
        {
            return new(null, BookingRejection.InvalidServiceSelection);
        }

        return new(new BookingIntent(codes, preferredFirstServiceCode, expectedTotalCents), null);
    }

    private static string ComputeFingerprint(IReadOnlyList<string> codes, string? preferred, long expectedTotalCents)
    {
        // Cada texto é prefixado pelo seu tamanho: nenhum código pode simular separadores de outro campo.
        var canonical = new StringBuilder(CanonicalVersion).Append('\n')
            .Append("codes:").Append(codes.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var code in codes)
        {
            AppendText(canonical, code).Append('\n');
        }

        canonical.Append("preferred:");
        if (preferred is null)
        {
            canonical.Append('-');
        }
        else
        {
            AppendText(canonical, preferred);
        }

        canonical.Append('\n').Append("expected:").Append(expectedTotalCents.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static StringBuilder AppendText(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
}
