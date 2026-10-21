using Cicclo.Api.Platform;
using Microsoft.AspNetCore.SignalR;

namespace Cicclo.Api.Laundry.Notifications;

/// <summary>
/// Adaptador SignalR da porta de avisos: envia cada aviso, em ordem, a todas as conexões da demo pelo nome wire, com o
/// payload por valor. Depende só do IHubContext (singleton do framework, sem DbContext, Redis ou backplane) e não
/// captura exceções: a falha de transporte é observável pelo chamador, que nunca a trata como rollback.
/// </summary>
public sealed class LaundryNotificationPublisher(IHubContext<LaundryHub> hub) : ILaundryNotifications
{
    public async Task PublishAsync(IReadOnlyList<LaundryNotification> notifications, CancellationToken cancellationToken)
    {
        foreach (var notification in notifications)
        {
            await hub.Clients.All.SendAsync(notification.EventName, notification.Payload, cancellationToken);
        }
    }
}

public static class LaundryNotificationServices
{
    /// <summary>Flag da extensão realtime; ausente equivale a false.</summary>
    public const string EnabledKey = "Realtime:Enabled";

    /// <summary>
    /// Barreira única do Hub (RT-01/02, D11): Realtime:Enabled AND Development AND Laundry:DemoMode. Fora dela nada de
    /// SignalR é registrado nem mapeado (/hubs/laundry responde 404) e os casos de uso não montam avisos. Não é
    /// autorização: o Hub é broadcast da wallet demo, sem usuário, grupo ou walletId.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue<bool>(EnabledKey)
        && environment.IsDevelopment()
        && configuration.GetValue<bool>($"{LaundryOptions.SectionName}:{nameof(LaundryOptions.DemoMode)}");

    /// <summary>
    /// SignalR em memória de uma instância (sem Redis nem backplane), o emissor e o efeito pós-commit dos casos de uso.
    /// O host da API só chama isto dentro da barreira <see cref="IsEnabled"/>.
    /// </summary>
    public static IServiceCollection AddLaundryNotifications(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<ILaundryNotifications, LaundryNotificationPublisher>();
        services.AddSingleton<LaundryChangeNotifier>();
        return services;
    }
}
