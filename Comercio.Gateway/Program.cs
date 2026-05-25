using Comercio.Shared;
using Comercio.Gateway;

var builder = WebApplication.CreateBuilder(args);

// Dependency injections
builder.Services.AddSingleton<InMemoryTenantStore>();
builder.Services.AddScoped<ITenantContext, TenantContext>();

builder.Services.AddControllers();

var app = builder.Build();

// Middleware pipelines register
app.UseMiddleware<TenantMiddleware>();

// Endpoint Minimal API to perimeter isolation diagnostics
app.MapGet("/api/diagnostics/tenant", (ITenantContext tenantContext) =>
{
    if (tenantContext.TenantId is null)
    {
        return Results.BadRequest(new
        {
            Message = "No tenant detected on HTTP request."
        });
    }

    return Results.Ok(new
    {
        tenantContext.TenantId,
        tenantContext.Subdomain,
        tenantContext.Plan,
        tenantContext.IsActive,
        Message = "Tenant isolation verified successfully in .NET 10 pipeline"
    });
});

app.Run();

public partial class Program { }