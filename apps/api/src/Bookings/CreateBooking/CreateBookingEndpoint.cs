using System.Globalization;
using Cicclo.Api.Platform;
using Cicclo.Api.Platform.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Cicclo.Api.Bookings.CreateBooking;

/// <summary>
/// POST /api/v1/bookings: valida a estrutura (header e corpo), executa a compra numa transação (débito, itens, ledger e
/// ativação inicial) e responde com a projeção autoritativa: 201 + Location na criação, 200 no replay da mesma chave.
/// </summary>
public static class CreateBookingEndpoint
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapCreateBookingEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapPost(
                "/bookings",
                async Task<IResult> (
                    [FromHeader(Name = IdempotencyKeyHeader)] Guid? idempotencyKey,
                    [FromBody] CreateBookingRequest? request,
                    CreateBooking useCase,
                    PostgresBookingProjection projection,
                    BookingMetrics metrics,
                    IOptions<LaundryOptions> options,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    if (Structure(context, idempotencyKey, request, options.Value.MaxServicesPerBooking, out var intent) is { } invalid)
                    {
                        metrics.Rejected();
                        return invalid;
                    }

                    var result = await useCase.ExecuteAsync(idempotencyKey!.Value, intent!, cancellationToken);
                    switch (result.Outcome)
                    {
                        case CreateBookingOutcome.Created or CreateBookingOutcome.Replayed:
                            var booking = await projection.FindAsync(result.Booking!.BookingId, cancellationToken)
                                ?? throw new InvalidOperationException("Booking confirmado ausente da projeção.");
                            metrics.Accepted();
                            return result.Outcome == CreateBookingOutcome.Created
                                ? TypedResults.Created($"/api/v1/bookings/{booking.BookingId}", BookingResponse.From(booking))
                                : TypedResults.Ok(BookingResponse.From(booking));
                        case CreateBookingOutcome.Rejected:
                            metrics.Rejected();
                            return Rejected(context, result);
                        default:
                            return ApiProblems.Create(
                                context,
                                StatusCodes.Status503ServiceUnavailable,
                                ApiProblems.DatabaseUnavailable,
                                "Resultado da compra não confirmado",
                                $"Não foi possível confirmar se a compra foi persistida. Repita com a mesma {IdempotencyKeyHeader} e o mesmo corpo para recuperá-la sem nova cobrança.");
                    }
                })
            .WithName("CreateBooking")
            .WithTags("Bookings")
            .WithSummary("Contratar uma ou mais execuções com débito imediato e idempotente.")
            .WithDescription(
                "Calcula o total no servidor com os preços atuais do catálogo, compara com expectedTotalCents e, numa única transação, debita a wallet demo, "
                + "grava o booking, os itens com preço histórico, o débito no ledger e a ativação inicial: compra de um item ativa esse item; lote com "
                + "preferredFirstServiceCode ativa a primeira ocorrência desse serviço; lote sem preferência fica todo pending_activation. Ativar significa "
                + "entrar na fila FIFO do tipo de máquina e, havendo máquina livre sem fila anterior, receber oferta de 60 s (to_be_confirmed). "
                + "Idempotency-Key (UUID) identifica a intenção: a primeira confirmação responde 201 com Location; repetir a mesma chave com a mesma "
                + "intenção normalizada (ordem dos códigos e preferência omitida/null não importam) responde 200 com o mesmo booking, cobrança histórica, "
                + "estado atual dos itens e saldo atual, sem novo débito; outra intenção com a mesma chave responde 409 IDEMPOTENCY_CONFLICT. "
                + "Recusas não consomem a chave. Chamadas concorrentes com a mesma chave produzem um único booking. Para recuperar uma resposta perdida, "
                + "repita o POST com a mesma chave e o mesmo corpo.")
            .Accepts<CreateBookingRequest>("application/json")
            .Produces<BookingResponse>(StatusCodes.Status201Created)
            .Produces<BookingResponse>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                "Limite da política de compras excedido (5/min por IP por padrão, janela fixa).",
                (StatusCodes.Status400BadRequest, "VALIDATION_ERROR (Idempotency-Key ausente ou não UUID, corpo null/malformado, campo desconhecido, tipo incorreto, expectedTotalCents ausente ou não positivo) ou INVALID_SERVICE_SELECTION (serviceCodes ausente, vazio, acima de 10 ou preferência fora do lote)."),
                (StatusCodes.Status404NotFound, "SERVICE_NOT_FOUND: algum código do lote não existe no catálogo."),
                (StatusCodes.Status409Conflict, "WALLET_INSUFFICIENT_BALANCE, PRICE_CHANGED, SERVICE_DISABLED, NO_OPERATIONAL_MACHINE ou IDEMPOTENCY_CONFLICT; nada é debitado e a chave não é consumida (exceto no conflito, em que ela já pertence a outra intenção)."),
                (StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE: corpo acima de 16 KiB."),
                (StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE: corpo sem Content-Type application/json."))
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                var key = operation.Parameters!.Single(parameter => parameter.Name == IdempotencyKeyHeader);
                var header = (OpenApiParameter)key;
                header.Required = true;
                header.Description = "UUID gerado pelo cliente antes da primeira tentativa e reutilizado em toda repetição da mesma intenção.";
                var created = (OpenApiResponse)operation.Responses!["201"];
                created.Description = "Compra confirmada nesta chamada.";
                created.Headers ??= new Dictionary<string, IOpenApiHeader>();
                created.Headers["Location"] = new OpenApiHeader
                {
                    Description = "URL relativa do booking criado: /api/v1/bookings/{bookingId}.",
                    Required = true,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                };
                operation.Responses["200"].Description = "Replay: a mesma chave já confirmou a mesma intenção; nada foi cobrado de novo.";
                operation.Responses["503"].Description = "PostgreSQL indisponível, base demo não preparada ou resultado do commit não confirmado (repita com a mesma chave).";
                return Task.CompletedTask;
            })
            .WithResponseExample(StatusCodes.Status201Created, GetBooking.GetBookingEndpoint.Example)
            .WithResponseExample(StatusCodes.Status200OK, GetBooking.GetBookingEndpoint.Example);
        return api;
    }

    /// <summary>Estrutura primeiro (A16): chave, corpo e seleção, antes de qualquer acesso ao banco.</summary>
    private static IResult? Structure(HttpContext context, Guid? key, CreateBookingRequest? request, int maxServices, out BookingIntent? intent)
    {
        intent = null;
        if (key is null)
        {
            return ApiProblems.Validation(context, Field(IdempotencyKeyHeader, "Informe um UUID no cabeçalho Idempotency-Key."));
        }

        if (request is null)
        {
            return ApiProblems.Validation(context, Field("body", "Envie o objeto JSON da compra."));
        }

        var created = BookingIntent.Create(request.ServiceCodes, request.PreferredFirstServiceCode, request.ExpectedTotalCents ?? 0, maxServices);
        intent = created.Intent;
        return created.Rejection switch
        {
            null => null,
            BookingRejection.ValidationError when request.ExpectedTotalCents is not > 0 =>
                ApiProblems.Validation(context, Field("expectedTotalCents", "Informe um inteiro positivo em centavos.")),
            BookingRejection.ValidationError =>
                ApiProblems.Validation(context, Field("serviceCodes", "Códigos não podem ser null.")),
            _ when request.ServiceCodes is { Count: > 0 } codes && codes.Count <= maxServices =>
                Selection(context, Field("preferredFirstServiceCode", "A preferência deve ser um dos códigos do lote.")),
            _ => Selection(context, Field("serviceCodes", string.Create(CultureInfo.InvariantCulture, $"Informe de 1 a {maxServices} códigos."))),
        };
    }

    private static IResult Selection(HttpContext context, IDictionary<string, string[]> errors) =>
        ApiProblems.Create(
            context, StatusCodes.Status400BadRequest, ApiProblems.InvalidServiceSelection, "Seleção de serviços inválida", "Revise os serviços do lote.", errors);

    private static IResult Rejected(HttpContext context, CreateBookingResult result) => result.Rejection switch
    {
        BookingRejection.ServiceNotFound => ApiProblems.Create(
            context, StatusCodes.Status404NotFound, ApiProblems.ServiceNotFound, "Serviço não encontrado", "Algum código do lote não existe no catálogo."),
        BookingRejection.ServiceDisabled => ApiProblems.Create(
            context, StatusCodes.Status409Conflict, ApiProblems.ServiceDisabled, "Serviço desabilitado", "Algum serviço do lote não está habilitado para contratação."),
        BookingRejection.NoOperationalMachine => ApiProblems.Create(
            context, StatusCodes.Status409Conflict, ApiProblems.NoOperationalMachine, "Sem máquina operacional", "Algum serviço do lote não tem máquina operacional do seu tipo."),
        BookingRejection.PriceChanged => ApiProblems.Create(
            context,
            StatusCodes.Status409Conflict,
            ApiProblems.PriceChanged,
            "Preço alterado",
            string.Create(CultureInfo.InvariantCulture, $"O total atual é {result.CalculatedTotalCents} centavos; confirme o novo valor com uma nova Idempotency-Key.")),
        BookingRejection.InsufficientBalance => ApiProblems.Create(
            context, StatusCodes.Status409Conflict, ApiProblems.WalletInsufficientBalance, "Saldo insuficiente", "O saldo da wallet demo não cobre o total da compra."),
        BookingRejection.IdempotencyConflict => ApiProblems.Create(
            context, StatusCodes.Status409Conflict, ApiProblems.IdempotencyConflict, "Chave já usada", "Esta Idempotency-Key já confirmou outra intenção de compra."),
        _ => throw new InvalidOperationException($"Recusa sem contrato HTTP: {result.Rejection}."),
    };

    private static Dictionary<string, string[]> Field(string name, string message) => new() { [name] = [message] };
}
