namespace Orders.Core.Entities;

/// <summary>
/// Individual detail of the products purchased in an order
/// </summary>
public class OrderItem
{
    public Guid OrderItemId { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }

    /// <summary>
    /// Storages the unit value standing at the moment of the confirmation
    /// The price freezing protects the historic financial reports against future 
    /// variations on the general catalog.
    /// </summary>
    public decimal UnitPriceAtPurchase { get; set; }
}