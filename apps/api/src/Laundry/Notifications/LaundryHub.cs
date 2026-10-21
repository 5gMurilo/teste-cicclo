using Microsoft.AspNetCore.SignalR;

namespace Cicclo.Api.Laundry.Notifications;

/// <summary>
/// Hub somente de saída da wallet demo (RT-09): nenhum método invocável pelo cliente, portanto nenhuma compra,
/// confirmação, estorno ou seleção de wallet. Os avisos partem de LaundryNotificationPublisher via IHubContext.
/// O host da API só o mapeia dentro da barreira <see cref="LaundryNotificationServices.IsEnabled"/>.
/// </summary>
public sealed class LaundryHub : Hub
{
    public const string Path = "/hubs/laundry";
}
