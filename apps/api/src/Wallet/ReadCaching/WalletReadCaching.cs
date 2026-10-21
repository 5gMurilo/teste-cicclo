namespace Cicclo.Api.Wallet.ReadCaching;

/// <summary>
/// Registro da extensão opcional (CACHE-01, D07/D08). A leitura cache-aside e o efeito pós-commit sempre existem; o
/// adaptador Redis só é registrado com `Caching:Enabled=true`. Desligado (padrão), nenhuma opção é validada, nenhum
/// multiplexer é criado e GET/escritas se comportam exatamente como sem cache.
/// </summary>
public static class WalletReadCaching
{
    public static IServiceCollection AddWalletReadCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<CachedWalletBalanceQuery>();
        services.AddSingleton<WalletCacheInvalidation>();
        var section = configuration.GetSection(WalletCacheOptions.SectionName);
        if (!section.GetValue<bool>(nameof(WalletCacheOptions.Enabled)))
        {
            return services;
        }

        services.AddOptions<WalletCacheOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(options => !string.IsNullOrWhiteSpace(options.Redis), "Caching:Redis é obrigatório com Caching:Enabled=true.")
            .ValidateOnStart();
        services.AddSingleton<IWalletReadCache, RedisWalletReadCache>();
        return services;
    }
}
