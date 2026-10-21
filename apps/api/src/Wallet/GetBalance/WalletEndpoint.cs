using Cicclo.Api.Platform.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Cicclo.Api.Wallet.GetBalance;

/// <summary>GET /api/v1/wallet: traduz o saldo persistido para o contrato em centavos; não aceita seletor de conta.</summary>
public static class WalletEndpoint
{
    public static IEndpointRouteBuilder MapWalletEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapGet("/wallet", async Task<Ok<WalletResponse>> (PostgresWalletBalanceQuery query, CancellationToken cancellationToken) =>
            {
                var balance = await query.ExecuteAsync(cancellationToken);
                return TypedResults.Ok(new WalletResponse(MoneyCents.FromDecimal(balance.Balance), balance.Currency, balance.UpdatedAt));
            })
            .WithName("GetWallet")
            .WithTags("Wallet")
            .WithSummary("Saldo da wallet demo.")
            .WithDescription("Lê o saldo autoritativo do PostgreSQL a cada chamada. A wallet é resolvida no servidor; parâmetros como walletId ou userId não são aceitos nem influenciam a resposta.")
            .ProducesReadProblems()
            .WithResponseExample(StatusCodes.Status200OK, new WalletResponse(5000, "BRL", new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc)));
        return api;
    }
}
