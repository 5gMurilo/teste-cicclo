using Cicclo.Api.Laundry;
using Cicclo.Api.Laundry.ReconcileOffers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Cicclo.Api.Platform.BackgroundServices;

/// <summary>
/// Host da reconciliação persistida: a cada intervalo (Laundry:QueueWorkerIntervalSeconds, padrão 5 s, medido pelo
/// TimeProvider) abre um escopo próprio e executa o caso de uso ReconcileOffers. Nenhum prazo vive em memória: o
/// PostgreSQL é a fonte da fila e das ofertas, então reiniciar o host não perde nada. Falha de uma iteração é logada
/// e aguarda o próximo tick (sem busy-spin); o encerramento cancela a espera e o I/O em andamento.
/// </summary>
public sealed partial class OfferReconciliationWorker(
    IServiceScopeFactory scopes, TimeProvider time, IOptions<LaundryOptions> options, ILogger<OfferReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.QueueWorkerIntervalSeconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunIterationAsync(stoppingToken);
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento: a espera do tick ou o I/O em andamento foi cancelado; a transação aberta é revertida.
            LogStopped();
        }
    }

    private async Task RunIterationAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ReconcileOffers>().ExecuteAsync(stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            LogIterationFailed(exception);
        }
    }

    [LoggerMessage(EventId = 320, Level = LogLevel.Error, Message = "Iteração da reconciliação de ofertas falhou; nova tentativa no próximo intervalo.")]
    private partial void LogIterationFailed(Exception exception);

    [LoggerMessage(EventId = 321, Level = LogLevel.Information, Message = "Reconciliação de ofertas encerrada.")]
    private partial void LogStopped();
}

public static class OfferReconciliationServices
{
    /// <summary>
    /// Caso de uso de reconciliação, seus colaboradores e o worker. Só o modo API normal chama isto: o bootstrap
    /// encerra antes e nunca sobe o worker.
    /// </summary>
    public static IServiceCollection AddOfferReconciliation(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<QueueCoordinator>();
        services.AddSingleton<LaundryMetrics>();
        services.AddScoped<ILaundrySessions, PostgresLaundrySessions>();
        services.AddScoped<ReconcileOffers>();
        services.AddHostedService<OfferReconciliationWorker>();
        return services;
    }
}
