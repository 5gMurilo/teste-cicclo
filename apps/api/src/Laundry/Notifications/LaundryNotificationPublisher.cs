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
    /// <summary>
    /// SignalR em memória de uma instância e o emissor. O host da API só chamará isto quando a extensão realtime for
    /// habilitada (E09/T3); por ora apenas o host isolado dos testes de protocolo o usa.
    /// </summary>
    public static IServiceCollection AddLaundryNotifications(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<ILaundryNotifications, LaundryNotificationPublisher>();
        return services;
    }
}
