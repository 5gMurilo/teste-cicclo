using Cicclo.Api.Wallet.GetBalance;

namespace Cicclo.Api.Wallet.ReadCaching;

/// <summary>
/// Porta estreita do cache da projeção de saldo (CACHE-02/03/05). Guarda só a leitura eventual do GET wallet: nunca é
/// consultada para autorizar débito, refund ou replay, e não é repositório da wallet. Nenhuma operação lança por falha
/// do cache; somente o cancelamento pedido pelo chamador é propagado.
/// </summary>
public interface IWalletReadCache
{
    /// <summary>Projeção ainda válida no cache; null em miss, entrada inválida, timeout ou Redis indisponível.</summary>
    Task<WalletBalance?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Grava a projeção lida do PostgreSQL com o TTL configurado; false quando não foi gravada.</summary>
    Task<bool> WriteAsync(WalletBalance balance, CancellationToken cancellationToken);

    /// <summary>Remove somente a chave da projeção; false quando a remoção não foi confirmada pelo Redis.</summary>
    Task<bool> InvalidateAsync(CancellationToken cancellationToken);
}
