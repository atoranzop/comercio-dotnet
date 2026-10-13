using Comercio.Shared;
using Microsoft.AspNetCore.Http;

namespace Comercio.Gateway;

/// <summary>
/// Middleware to intercept every perimeter HTTP request, extract
/// the tenant ID, validate its commertial state and inject context
/// </summary>
public class TenantMiddleware(RequestDelegate next, ITenantStore store)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
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

        var tenant = await store.FindBySubdomainAsync(
            subdomain,
            context.RequestAborted);

        if (tenant is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                ApiResponse<object>.Failed(
                    "tenant_not_found",
                    "The requested tenant is not registered."),
                context.RequestAborted
            );

            return;
        }

        if (!tenant.IsActive)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                ApiResponse<object>.Failed(
                    "tenant_suspended",
                    "The requested tenant is not active."),
                context.RequestAborted
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