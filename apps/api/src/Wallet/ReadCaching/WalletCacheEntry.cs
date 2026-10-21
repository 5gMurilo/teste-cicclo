using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Wallet.GetBalance;

namespace Cicclo.Api.Wallet.ReadCaching;

/// <summary>
/// Forma versionada (chave v1) da projeção no cache: exatamente balanceCents, currency e updatedAt. Qualquer outra
/// forma, valor fora do intervalo do contrato, moeda diferente de BRL ou data sem UTC é rejeitada e vira miss.
/// </summary>
public static class WalletCacheEntry
{
    private const string BalanceCents = "balanceCents";
    private const string Currency = "currency";
    private const string UpdatedAt = "updatedAt";
    private const string UtcFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    /// <summary>Mesmo teto do contrato HTTP (WalletResponse.BalanceCents).</summary>
    public const long MaxBalanceCents = 999_999_999_999;

    /// <summary>JSON da projeção; null quando a projeção não pode representar um saldo válido do contrato.</summary>
    public static string? Serialize(WalletBalance balance)
    {
        if (balance.Currency != MoneyCents.Currency || balance.UpdatedAt.Kind != DateTimeKind.Utc
            || balance.Balance < 0m || balance.Balance > MoneyCents.MaxAmount || decimal.Round(balance.Balance, 2) != balance.Balance)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(BalanceCents, MoneyCents.FromDecimal(balance.Balance));
            writer.WriteString(Currency, balance.Currency);
            writer.WriteString(UpdatedAt, balance.UpdatedAt.ToString(UtcFormat, CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static bool TryParse(string? json, [NotNullWhen(true)] out WalletBalance? balance)
    {
        balance = null;
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual([BalanceCents, Currency, UpdatedAt]) is false
                || root.GetProperty(BalanceCents) is not { ValueKind: JsonValueKind.Number } cents
                || !cents.TryGetInt64(out var value) || value < 0 || value > MaxBalanceCents
                || root.GetProperty(Currency) is not { ValueKind: JsonValueKind.String } currency || currency.GetString() != MoneyCents.Currency
                || root.GetProperty(UpdatedAt) is not { ValueKind: JsonValueKind.String } updatedAt
                || !DateTime.TryParseExact(updatedAt.GetString(), UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
            {
                return false;
            }

            balance = new WalletBalance(value / 100m, MoneyCents.Currency, DateTime.SpecifyKind(at, DateTimeKind.Utc));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
