using System.ComponentModel;
using Cicclo.Api.Platform.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cicclo.Api.Wallet.ListTransactions;

/// <summary>GET /api/v1/wallet/transactions: histórico financeiro paginado da wallet demo, sem efeito colateral.</summary>
public static class ListTransactionsEndpoint
{
    public static IEndpointRouteBuilder MapListTransactionsEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapGet(
                "/wallet/transactions",
                async Task<IResult> (
                    [FromQuery, Description("Página 1-based; padrão 1.")] int? page,
                    [FromQuery, Description("Itens por página, de 1 a 100; padrão 20.")] int? pageSize,
                    PostgresLedgerQuery query,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    if (Paging.Validate(context, page, pageSize, out var resolvedPage, out var resolvedPageSize) is { } invalid)
                    {
                        return invalid;
                    }

                    var (entries, hasNextPage) = await query.ListAsync(resolvedPage, resolvedPageSize, cancellationToken);
                    return TypedResults.Ok(new PageResponse<TransactionResponse>([.. entries.Select(TransactionResponse.From)], resolvedPage, resolvedPageSize, hasNextPage));
                })
            .WithName("ListWalletTransactions")
            .WithTags("Wallet")
            .WithSummary("Histórico financeiro da wallet demo, paginado.")
            .WithDescription(
                "Lê do PostgreSQL os lançamentos da wallet demo em ordem createdAt decrescente e transactionId decrescente. amountCents é sempre "
                + "positivo e balanceAfterCents é o saldo gravado logo depois de cada lançamento (histórico, não recalculado com o saldo atual). "
                + "A wallet é resolvida no servidor; parâmetros como walletId ou userId são ignorados. Não altera saldo nem lançamentos.")
            .Produces<PageResponse<TransactionResponse>>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                "Limite da política de leituras de contratações e histórico excedido (janela fixa por IP).",
                (StatusCodes.Status400BadRequest, "page ou pageSize não inteiro ou fora dos limites (VALIDATION_ERROR)."))
            .WithResponseExample(
                StatusCodes.Status200OK,
                new PageResponse<TransactionResponse>(
                    [
                        new(
                            Guid.Parse("01a2b3c4-0000-7000-8000-000000000001"),
                            Guid.Parse("fb0cb8f8-ced3-4ad7-9ee5-e7df973abc19"),
                            null,
                            TransactionKindContract.Debit,
                            3980,
                            1020,
                            new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc)),
                    ],
                    1,
                    Paging.DefaultPageSize,
                    false));
        return api;
    }
}
