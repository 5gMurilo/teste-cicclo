namespace Cicclo.Api.Platform.Http;

/// <summary>
/// Conversão de valores numeric(12,2) para centavos inteiros do contrato HTTP. Não arredonda:
/// valor com mais de duas casas ou fora do armazenamento é um erro, não uma aproximação.
/// </summary>
public static class MoneyCents
{
    /// <summary>Moeda única da demonstração; não há catálogo multi-moeda.</summary>
    public const string Currency = "BRL";

    public const decimal MaxAmount = 9_999_999_999.99m;

    public static long FromDecimal(decimal amount)
    {
        if (amount < 0m || amount > MaxAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Valor monetário fora do intervalo armazenável 0..9999999999.99.");
        }

        var cents = amount * 100m;
        if (cents != decimal.Truncate(cents))
        {
            throw new ArgumentException("Valor monetário com mais de duas casas decimais.", nameof(amount));
        }

        return (long)cents;
    }
}
