using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Cicclo.Api.Platform.Http;

/// <summary>Construção única dos erros HTTP: `urn:cicclo:problem:*`, errorCode estável, traceId e errors mapa/null.</summary>
public static class ApiProblems
{
    public const string ValidationError = "VALIDATION_ERROR";

    public const string DatabaseUnavailable = "DATABASE_UNAVAILABLE";

    public const string InternalError = "INTERNAL_ERROR";

    public static IResult Validation(HttpContext context, IDictionary<string, string[]> errors) =>
        Create(context, StatusCodes.Status400BadRequest, ValidationError, "Requisição inválida", "Um ou mais campos são inválidos.", errors);

    public static IResult Unavailable(HttpContext context) =>
        Create(
            context,
            StatusCodes.Status503ServiceUnavailable,
            DatabaseUnavailable,
            "Banco de dados indisponível",
            "O PostgreSQL não está acessível ou a base demo não foi preparada.");

    public static IResult Internal(HttpContext context) =>
        Create(
            context,
            StatusCodes.Status500InternalServerError,
            InternalError,
            "Erro interno",
            "Ocorreu um erro inesperado. Informe o traceId para diagnóstico.");

    public static IResult Create(
        HttpContext context,
        int status,
        string errorCode,
        string title,
        string detail,
        IDictionary<string, string[]>? errors = null)
    {
        var problem = new ProblemDetails
        {
            Type = $"urn:cicclo:problem:{errorCode.ToLowerInvariant().Replace('_', '-')}",
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path,
        };
        problem.Extensions["errorCode"] = errorCode;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        problem.Extensions["errors"] = errors;
        return TypedResults.Problem(problem);
    }
}
