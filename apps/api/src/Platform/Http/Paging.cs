using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Cicclo.Api.Platform.Http;

/// <summary>Página de uma listagem: itens na ordem determinística da consulta e indicação de próxima página.</summary>
public sealed record PageResponse<T>(
    [property: Required, Description("Itens desta página, em ordem createdAt decrescente e id decrescente.")] IReadOnlyList<T> Items,
    [property: Required, Range(1, int.MaxValue), Description("Página atual, 1-based.")] int Page,
    [property: Required, Range(1, Paging.MaxPageSize), Description("Tamanho de página aplicado.")] int PageSize,
    [property: Required, Description("true quando existe pelo menos um item depois desta página.")] bool HasNextPage);

/// <summary>Paginação por page/pageSize (A08): padrão 1/20, page &gt;= 1 e pageSize entre 1 e 100.</summary>
public static class Paging
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;

    /// <summary>Aplica os padrões e devolve o erro de campo, ou null quando a página é válida.</summary>
    public static IResult? Validate(HttpContext context, int? page, int? pageSize, out int resolvedPage, out int resolvedPageSize)
    {
        resolvedPage = page ?? 1;
        resolvedPageSize = pageSize ?? DefaultPageSize;
        var errors = new Dictionary<string, string[]>();
        if (resolvedPage < 1)
        {
            errors["page"] = ["Informe um inteiro maior ou igual a 1."];
        }

        if (resolvedPageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Informe um inteiro entre 1 e {MaxPageSize}."];
        }

        return errors.Count == 0 ? null : ApiProblems.Validation(context, errors);
    }

    /// <summary>Quantos itens pular, em 64 bits: a última página válida não estoura.</summary>
    public static long Offset(int page, int pageSize) => ((long)page - 1) * pageSize;
}
