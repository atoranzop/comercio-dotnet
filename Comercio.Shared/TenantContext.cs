namespace Comercio.Shared;

/// <summary>
/// Implementation of the tenant context with scoped lifetime
/// (one single instance per HTTP request)
/// </summary>
public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public string? Subdomain { get; private set; }
    public string? Plan { get; private set; }
    public bool IsActive { get; private set; }

    //<inheritdoc />
    public void SetTenant(Guid tenantId, string subdomain, string plan, bool isActive)
    {
        TenantId = tenantId;
        Subdomain = subdomain;
        Plan = plan;
        IsActive = isActive;
    }
}