namespace Comercio.Shared;

/// <summary>
/// Defines the contract to get and set the current tenant context
/// during the life cycle of an HTTP request
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    string? Subdomain {get; }
    string? Plan { get; }
    bool IsActive { get; }

    /// <summary>
    /// Initializes the tenant properties after its resolution on the middleware
    /// </summary>
    void SetTenant(Guid tenantId, string subdomain, string plan, bool isActive);
}