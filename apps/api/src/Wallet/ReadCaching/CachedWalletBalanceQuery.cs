using Cicclo.Api.Wallet.GetBalance;

namespace Cicclo.Api.Wallet.ReadCaching;

/// <summary>
/// Leitura do GET wallet com cache-aside eventual (CACHE-01/02/03). Sem cache registrado (Caching:Enabled=false), é
/// exatamente a consulta PostgreSQL. Com cache: um hit válido responde a projeção guardada, que pode estar atrasada até
/// o TTL contado da gravação; miss, entrada inválida ou falha do Redis (já degradadas pela porta) caem no PostgreSQL,
/// que continua autoritativo, e o valor lido é regravado. Só o GET usa esta classe: compra, replay, cancelamento e
/// refund decidem saldo pelo PostgreSQL, sob o lock da wallet.
/// </summary>
public sealed class CachedWalletBalanceQuery(PostgresWalletBalanceQuery database, IWalletReadCache? cache = null)
{
    public async Task<WalletBalance> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (cache is null)
        {
            return await database.ExecuteAsync(cancellationToken);
        }

        if (await cache.ReadAsync(cancellationToken) is { } cached)
        {
            return cached;
        }

        var balance = await database.ExecuteAsync(cancellationToken);
        await cache.WriteAsync(balance, cancellationToken);
        return balance;
    }
}
