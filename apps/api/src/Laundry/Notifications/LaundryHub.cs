using Microsoft.AspNetCore.SignalR;

namespace Cicclo.Api.Laundry.Notifications;

/// <summary>
/// Hub somente de saída da wallet demo (RT-09): nenhum método invocável pelo cliente, portanto nenhuma compra,
/// confirmação, estorno ou seleção de wallet. Os avisos partem de LaundryNotificationPublisher via IHubContext.
/// O mapping no host da API e sua barreira por flag/ambiente pertencem à integração pós-commit (E09/T3).
/// </summary>
public sealed class LaundryHub : Hub
{
    public const string Path = "/hubs/laundry";
}
