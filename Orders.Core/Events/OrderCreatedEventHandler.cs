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
    /// <summary>
    /// Logs an order audit entry and a simulated purchase confirmation email.
    /// </summary>
    /// <param name="notification">The created order's identifiers and total price.</param>
    /// <param name="cancellationToken">The notification cancellation token; logging does not observe it.</param>
    /// <returns>A completed task after both messages have been logged.</returns>
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