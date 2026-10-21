using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Cicclo.Api.Platform.Http;

public sealed class ReadRateLimitOptions
{
    public const string SectionName = "RateLimiting:Reads";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; init; } = 60;
}

/// <summary>
/// Política nomeada de leituras: janela fixa de 1 minuto por RemoteIpAddress, sem fila, compartilhada pelos GETs do grupo.
/// Cabeçalhos X-Forwarded-For não são considerados; sem IP remoto, a partição é "unknown".
/// </summary>
public static class ApiReadRateLimit
{
    public const string Policy = "reads";

    public const string UnknownPartition = "unknown";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddApiReadRateLimit(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ReadRateLimitOptions>()
            .Bind(configuration.GetSection(ReadRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Policy, context =>
            {
                var permitLimit = context.RequestServices.GetRequiredService<IOptions<ReadRateLimitOptions>>().Value.PermitLimit;
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? UnknownPartition,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = Window, QueueLimit = 0 });
            });
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
