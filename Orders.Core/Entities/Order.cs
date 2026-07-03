using Comercio.Shared;

namespace Orders.Core.Entities;

/// <summary>
/// Represents the header of a transactional purchasing order associated to
/// a tenant.
/// </summary>
public class Order : IMultiTenant
{
    public Guid OrderId { get; set; }
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = "Pending";
    public decimal TotalPrice { get; set; }

    /// <summary>
    /// Relation of navigation towards the order details.
    /// </summary>
    public List<OrderItem> Items { get; set; } = [];
}