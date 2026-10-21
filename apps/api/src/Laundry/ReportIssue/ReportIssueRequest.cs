using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Cicclo.Api.Laundry.ReportIssue;

/// <summary>Corpo opcional do relato; propriedades desconhecidas e tipos incorretos são recusados com VALIDATION_ERROR.</summary>
public sealed record ReportIssueRequest(
    [property: MaxLength(IssueDescription.MaxLength), Description("Texto livre do relato, opcional, com até 1000 caracteres. Omitido, null ou só com espaços equivale a relato sem descrição; espaços nas pontas são removidos.")]
    string? Description = null);

/// <summary>Relato aberto do item: identificador e estado (sempre reported nesta rota).</summary>
public sealed record ReportIssueResponse(
    [property: Required, Description("Identificador (UUID) do relato aberto do item; o mesmo em toda repetição enquanto ele estiver aberto.")] Guid IncidentId,
    [property: Required, Description("Estado do relato: reported (registrado, sem efeito financeiro).")] IncidentStatusContract Status);

[JsonConverter(typeof(JsonStringEnumConverter<IncidentStatusContract>))]
public enum IncidentStatusContract
{
    [JsonStringEnumMemberName("reported")]
    Reported,
}

/// <summary>Regra pura da descrição: até 1000 caracteres; null, vazia ou só espaços vira null; o resto é aparado.</summary>
public static class IssueDescription
{
    public const int MaxLength = 1000;

    /// <summary>false quando a descrição excede o limite; senão devolve a forma persistida (null ou texto aparado).</summary>
    public static bool TryNormalize(string? raw, out string? description)
    {
        description = null;
        if (raw is { Length: > MaxLength })
        {
            return false;
        }

        description = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        return true;
    }
}
