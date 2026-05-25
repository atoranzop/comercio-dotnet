using Comercio.Shared;
using Microsoft.AspNetCore.Http;

namespace Comercio.Gateway;

/// <summary>
/// Middleware to intercept every perimeter HTTP request, extract
/// the tenant ID, validate its commertial state and inject context
/// </summary>
public class TenantMiddleware(RequestDelegate next, InMemoryTenantStore store)
{
    public async Task InvokeAsync (HttpContext context, ITenantContext tenantContext)
    {
        string? subdomain = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(subdomain))
        {
            subdomain = ResolveSubdomainFromHost(context.Request.Host.Host);
        }

        if (string.IsNullOrWhiteSpace(subdomain))
        {
            await next(context);
            return;
        }

        var tenant = store.GetBySubdomain(subdomain);

        if (tenant is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(
                new
                {
                    Error = $"Tenant '{subdomain}' not registered on the platform."
                }
            );

            return;
        }

        if (!tenant.IsActive)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(
                new
                {
                    Error = "The provided tenant is currently suspended"
                }
            );

            return;
        }

        tenantContext.SetTenant(
            tenant.TenantId, 
            tenant.Subdomain, 
            tenant.Plan, 
            tenant.IsActive
        );

        context.Request.Headers["X-Internal-TenantId"] = tenant.TenantId.ToString();

        await next(context);
    }

    private static string? ResolveSubdomainFromHost(string host)
    {
        if(
            string.IsNullOrWhiteSpace(host) ||
            host == "localhost" ||
            host == "127.0.0.1"
        )
        {
            return null;
        }

        var parts = host.Split('.');

        return parts.Length >= 2 ? parts[0] : null;
    }
}