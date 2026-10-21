using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Cicclo.Api.Platform.Http;

/// <summary>Limite de uma política nomeada; cada política lê a própria seção `RateLimiting:&lt;Seção&gt;`.</summary>
public sealed class RateLimitPolicyOptions
{
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; }
}

/// <summary>
/// Políticas nomeadas por grupo de endpoints: janela fixa de 1 minuto por RemoteIpAddress, sem fila. Endpoints da
/// mesma política compartilham o limite. Cabeçalhos X-Forwarded-For não são considerados; sem IP remoto, a partição
/// é "unknown".
/// </summary>
public static class ApiRateLimits
{
    /// <summary>GET wallet e catálogo: `RateLimiting:Reads:PermitLimit`, padrão 60.</summary>
    public const string Reads = "reads";

    /// <summary>Leituras de contratações e do histórico financeiro: `RateLimiting:BookingReads:PermitLimit`, padrão 30.</summary>
    public const string BookingReads = "booking-reads";

    public const string UnknownPartition = "unknown";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static readonly (string Policy, string Section, int DefaultLimit)[] Policies =
    [
        (Reads, "Reads", 60),
        (BookingReads, "BookingReads", 30),
    ];

    public static IServiceCollection AddApiRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        foreach (var (policy, section, defaultLimit) in Policies)
        {
            services.AddOptions<RateLimitPolicyOptions>(policy)
                .Configure(options => options.PermitLimit = defaultLimit)
                .Bind(configuration.GetSection($"RateLimiting:{section}"))
                .ValidateDataAnnotations()
                .ValidateOnStart();
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            foreach (var (policy, _, _) in Policies)
            {
                options.AddPolicy(policy, context =>
                {
                    var permitLimit = context.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitPolicyOptions>>().Get(policy).PermitLimit;
                    return RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? UnknownPartition,
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = Window, QueueLimit = 0 });
                });
            }

            options.OnRejected = async (rejected, _) =>
            {
                var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var remaining) ? remaining : Window;
                rejected.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await ApiProblems.RateLimited(rejected.HttpContext).ExecuteAsync(rejected.HttpContext);
            };
        });
        return services;
    }
}
