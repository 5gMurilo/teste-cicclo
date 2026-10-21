using Cicclo.Api.Platform.Http;
using Cicclo.Api.Wallet.ReadCaching;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Cicclo.Api.Wallet.GetBalance;

/// <summary>GET /api/v1/wallet: traduz o saldo (PostgreSQL ou projeção eventual do cache opcional) para o contrato em centavos; não aceita seletor de conta.</summary>
public static class WalletEndpoint
{
    public static IEndpointRouteBuilder MapWalletEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapGet("/wallet", async Task<Ok<WalletResponse>> (CachedWalletBalanceQuery query, CancellationToken cancellationToken) =>
            {
                var balance = await query.ExecuteAsync(cancellationToken);
                return TypedResults.Ok(new WalletResponse(MoneyCents.FromDecimal(balance.Balance), balance.Currency, balance.UpdatedAt));
            })
            .WithName("GetWallet")
            .WithTags("Wallet")
            .WithSummary("Saldo da wallet demo.")
            .WithDescription(
                "Lê o saldo autoritativo do PostgreSQL a cada chamada. Com o cache opcional ligado (Caching:Enabled=true, desligado por padrão), "
                + "a leitura é eventual: um hit no Redis devolve a projeção gravada por uma leitura anterior, que pode ficar atrasada até o TTL "
                + "(30 s por padrão) contado da gravação, inclusive depois de uma compra, cancelamento ou falha já confirmados e mesmo com o "
                + "PostgreSQL indisponível; não há read-your-writes. O saldo de comandos (compra, replay, refund) e o remainingBalanceCents "
                + "das respostas vêm sempre do PostgreSQL. A wallet é resolvida no servidor; parâmetros como walletId ou userId não são aceitos "
                + "nem influenciam a resposta.")
            .ProducesReadProblems()
            .WithResponseExample(StatusCodes.Status200OK, new WalletResponse(5000, "BRL", new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc)));
        return api;
    }
}
