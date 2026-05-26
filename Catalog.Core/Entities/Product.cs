using Comercio.Shared;

namespace Catalog.Core.Entities;

/// <summary>
/// Represents a product on the tenant's private catalog.
/// Implaments the IMultiTenant interface to ensure its isolation.
/// </summary>
public class Product : IMultiTenant
{
    public Guid ProductId { get; set; }

    /// <summary>
    /// Discriminatory key injected automatically by the system
    /// </summary>
    public Guid TenantId { get; set; }

    public string SKU { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public bool IsActive { get; set; } = true;
}