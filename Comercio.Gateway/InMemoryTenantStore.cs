using Comercio.Shared;

namespace Comercio.Gateway;

/// <summary>
/// Mock in-memory database for the tenants catalog.
/// Avoids physical infrastructure dependency during Phase 1 development
/// </summary>
public class InMemoryTenantStore
{
    // Uses the modern collection expressions [] sytax of C#
    private readonly Dictionary<string, TenantDto> _tenantsBySubdomain =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["alpha-store"] = new TenantDto (
            Guid.Parse("a1111111-1111-1111-1111-111111111111"),
            "alpha-store",
            "Premium",
            isActive: true
        ),

        ["beta-store"] = new TenantDto (
            Guid.Parse("b2222222-2222-2222-2222-222222222222"),
            "beta-store",
            "Basic",
            isActive: true
        ),

        ["suspended-store"] = new TenantDto (
            Guid.Parse("f9999999-9999-9999-9999-999999999999"),
            "suspended-store",
            "Basic",
            isActive: false
        )
    };

    /// <summary>
    /// Recovers a tenant given its identifying subdomain
    /// </summary>
    public TenantDto? GetBySubdomain(string subdomain)
    {
        return _tenantsBySubdomain.TryGetValue(subdomain, out var tenant) ?
            tenant: null;
    }
}