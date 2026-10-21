using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;

namespace Cicclo.Api.Platform.Http;

/// <summary>Corpo de erro publicado (application/problem+json): campos RFC 9457 mais errorCode, traceId e errors.</summary>
public sealed record ApiProblem(
    [property: Required, Description("URN estável do tipo de erro: urn:cicclo:problem:<slug>.")] string Type,
    [property: Required, Description("Resumo legível do erro.")] string Title,
    [property: Required, Description("Status HTTP repetido no corpo.")] int Status,
    [property: Required, Description("Explicação sem SQL, stack trace ou dados de conexão.")] string Detail,
    [property: Required, Description("Caminho da requisição que falhou.")] string Instance,
    [property: Required, Description("Código estável do erro, por exemplo DATABASE_UNAVAILABLE.")] string ErrorCode,
    [property: Required, Description("Identificador de correlação com logs e traces.")] string TraceId,
    [property: Required, Description("Erros por campo; null quando o erro não é de campo.")] IDictionary<string, string[]>? Errors);

/// <summary>Construção única dos erros HTTP: `urn:cicclo:problem:*`, errorCode estável, traceId e errors mapa/null.</summary>
public static class ApiProblems
{
    public const string ContentType = "application/problem+json";

    public const string ValidationError = "VALIDATION_ERROR";

    public const string DatabaseUnavailable = "DATABASE_UNAVAILABLE";

    public const string InternalError = "INTERNAL_ERROR";

    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";

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

    public static IResult RateLimited(HttpContext context) =>
        Create(
            context,
            StatusCodes.Status429TooManyRequests,
            RateLimitExceeded,
            "Limite de requisições excedido",
            "Aguarde o número de segundos indicado em Retry-After antes de tentar novamente.");

    public static IResult Create(
        HttpContext context,
        int status,
        string errorCode,
        string title,
        string detail,
        IDictionary<string, string[]>? errors = null) =>
        TypedResults.Json(
            new ApiProblem(
                $"urn:cicclo:problem:{errorCode.ToLowerInvariant().Replace('_', '-')}",
                title,
                status,
                detail,
                context.Request.Path,
                errorCode,
                Activity.Current?.Id ?? context.TraceIdentifier,
                errors),
            contentType: ContentType,
            statusCode: status);
}
