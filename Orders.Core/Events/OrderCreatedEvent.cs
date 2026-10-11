using MediatR;

namespace Orders.Core.Events;

/// <summary>
/// Dominion event that represents the successfull creation of a request
/// on the platform
/// </summary>
/// <param name="orderId"></param>
/// <param name="tenantId"></param>
/// <param name="totalPrice"></param>
/// <param name="costumerId"></param>
public class OrderCreatedEvent (
    Guid orderId,
    Guid tenantId,
    decimal totalPrice,
    Guid costumerId
) : INotification
{
    public Guid OrderId { get; } = orderId;
    public Guid TenantId { get; } = tenantId;
    public decimal TotalPrice { get; } = totalPrice;
    public Guid CostumerId { get; } = costumerId;
}