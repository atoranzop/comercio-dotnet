namespace Comercio.Shared;

/// <summary>
/// Provides tenant lookup by subdomain.
/// </summary>
public interface ITenantStore
{
    Task<TenantDto?> FindBySubdomainAsync(
        string subdomain,
        CancellationToken cancellationToken = default);
}
