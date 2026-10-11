using MediatR;
using Microsoft.Extensions.Logging;

namespace Orders.Core.Events;

/// <summary>
/// Reacts to the event OrderCreatedEvent asyncronously to start secondary tasks
/// </summary>
/// <param name="logger"></param>
public class OrderCreatedEventHandler (
    ILogger<OrderCreatedEventHandler> logger
) : INotificationHandler<OrderCreatedEvent>
{
    public Task Handle(
        OrderCreatedEvent notification,
        CancellationToken cancellationToken
    )
    {
        // Asyncronous audit simulation
        logger.LogInformation(
            "[AUDIT] Order Event Received Asyncronously. Tenant: {TenantId} | " +
            "Order: {OrderId} | Total: $ {TotalPrice}",
            notification.TenantId, notification.OrderId, notification.TotalPrice
        );

        // Purchase confirmation email to costumer simulation
        logger.LogInformation(
            "[NOTIFICATION] Confirmation email sended asyncronous to client " +
            "{CostumerId} for buying on store of tenant {TenantId}.",
            notification.CostumerId, notification.TenantId
        );

        return Task.CompletedTask;
    }
}