using Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Comercio.Gateway;

public static class TenantDevelopmentSeeder
{
    private static readonly TenantRecord[] DevelopmentTenants =
    [
        new()
        {
            TenantId = Guid.Parse("a1111111-1111-1111-1111-111111111111"),
            Subdomain = "alpha-store",
            Plan = "Premium",
            IsActive = true
        },
        new()
        {
            TenantId = Guid.Parse("b2222222-2222-2222-2222-222222222222"),
            Subdomain = "beta-store",
            Plan = "Basic",
            IsActive = true
        },
        new()
        {
            TenantId = Guid.Parse("f9999999-9999-9999-9999-999999999999"),
            Subdomain = "suspended-store",
            Plan = "Basic",
            IsActive = false
        }
    ];

    public static async Task SeedAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        foreach (var tenant in DevelopmentTenants)
        {
            var exists = await dbContext.Tenants.AnyAsync(
                existing => existing.Subdomain == tenant.Subdomain,
                cancellationToken);

            if (!exists)
            {
                dbContext.Tenants.Add(new TenantRecord
                {
                    TenantId = tenant.TenantId,
                    Subdomain = tenant.Subdomain,
                    Plan = tenant.Plan,
                    IsActive = tenant.IsActive
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
