namespace Comercio.Shared;

/// <summary>
/// Data transference object that represents a tenant.
/// Uses the modern C# primary constructors features.
/// </summary>
public class TenantDto(
    Guid tenantId, 
    string subdomain, 
    string plan, 
    bool isActive
)
{
    public Guid TenantId { get; } = tenantId;
    public string Subdomain { get; } = subdomain;
    public string Plan { get; } = plan;
    public bool IsActive { get; } = isActive;
}