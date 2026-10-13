using Comercio.Shared;

namespace Comercio.Gateway;

/// <summary>
/// In-memory tenant store for isolated tests.
/// </summary>
public class InMemoryTenantStore
    : ITenantStore
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

    public Task<TenantDto?> FindBySubdomainAsync(
        string subdomain,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tenantsBySubdomain.TryGetValue(subdomain, out var tenant);
        return Task.FromResult(tenant);
    }
}