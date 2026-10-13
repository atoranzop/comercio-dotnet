using Comercio.Shared;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Infrastructure.Persistence;

public sealed class PostgresTenantStore(TenantDbContext dbContext) : ITenantStore
{
    public async Task<TenantDto?> FindBySubdomainAsync(
        string subdomain,
        CancellationToken cancellationToken = default)
    {
        var normalizedSubdomain = subdomain.ToLowerInvariant();
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(
                tenant => tenant.Subdomain == normalizedSubdomain,
                cancellationToken);

        return tenant is null
            ? null
            : new TenantDto(
                tenant.TenantId,
                tenant.Subdomain,
                tenant.Plan,
                tenant.IsActive);
    }
}
