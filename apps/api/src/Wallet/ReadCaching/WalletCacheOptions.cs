using System.ComponentModel.DataAnnotations;

namespace Cicclo.Api.Wallet.ReadCaching;

/// <summary>Seção `Caching`: cache Redis opcional da projeção de saldo, desligado por padrão (CACHE-01, D07/D08).</summary>
public sealed class WalletCacheOptions
{
    public const string SectionName = "Caching";

    public bool Enabled { get; init; }

    /// <summary>Configuração do StackExchange.Redis (host:porta e opções); pode conter segredo e nunca é registrada em log.</summary>
    public string? Redis { get; init; }

    /// <summary>TTL contado a partir da gravação no cache (D07).</summary>
    [Range(1, 3_600)]
    public int TimeToLiveSeconds { get; init; } = 30;

    /// <summary>Orçamento de espera de cada operação, conexão preguiçosa incluída.</summary>
    [Range(50, 5_000)]
    public int OperationTimeoutMilliseconds { get; init; } = 500;

    /// <summary>Namespace opcional das chaves, para isolar instâncias de teste (D08). Vazio em operação normal.</summary>
    public string KeyPrefix { get; init; } = "";
}
