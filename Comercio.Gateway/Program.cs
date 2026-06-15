using Comercio.Shared;
using Comercio.Gateway;
using Comercio.Gateway.Dtos;
using Catalog.Infrastructure.Persistence;
using Catalog.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy.Transforms;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Dependency injections for common SaaS services
builder.Services.AddSingleton<InMemoryTenantStore>();
builder.Services.AddScoped<ITenantContext, TenantContext>();

// Inject DbContext configuring local SQLite (skip in Test environment - tests will configure it)
if (!builder.Environment.IsEnvironment("Test"))
{
    var dbPath = Path.Combine(AppContext.BaseDirectory, "comercio_catalog.db");
    builder.Services.AddDbContext<CatalogDbContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));
}

builder.Services.AddControllers();

builder.Services.AddRateLimiter(options =>
{
    // Respond with HTTP 429 (Too many requests) when a client exceeds the
    // limit
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Create a dynamic limiter partitionated by TenantId and Commertial Plan
    options.AddPolicy("TenantRateLimiter", context =>
    {
        // Recover TenantContext injected in the pipeline by the Middleware
        var tenantContext = context
            .RequestServices
            .GetRequiredService<ITenantContext>();

        // If not tenant detected, apply restictive quota based on IP
        var tenantKey = tenantContext.TenantId?.ToString() ?? 
            context.Connection.RemoteIpAddress?.ToString() ??
            "anonymous"; 
        var isPremium = tenantContext.Plan?.Equals(
            "Premium", 
            StringComparison.OrdinalIgnoreCase
        ) ?? false;

        // Limit configuration
        //  - Premium Plan: 100 requests in a 10 sec timeframe.
        //  - Basic Plan: 5 requests in a 10 sec timeframe.
        int permitLimit = isPremium ? 100 : 5;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: tenantKey,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0 // Reject incoming requests immediately if full quota
            });
    });
});

// CONFIGURE YARP WITH SECURE PROPAGATION
// The API Gateway acts as the newtwork guardian. It reads the obtained TenantId
// and injects it as an internal secure header so the micro-services/modules trust 
// it blindly.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(transformBuilder =>
    {
        transformBuilder.AddRequestTransform(async transformContext =>
        {
            // Get tenant context perimetrally validated
            var tenantContext = transformContext
                .HttpContext
                .RequestServices
                .GetRequiredService<ITenantContext>();
            
            if (tenantContext.TenantId.HasValue)
            {
                // Inject internal header. Internal services will never validate 
                // DNS or subdomains, they trust fully on this Gateway-provided
                // header.
                transformContext.ProxyRequest.Headers.Remove("X-Internal-TenantId");
                transformContext.ProxyRequest.Headers.TryAddWithoutValidation(
                    "X-Internal-TenantId",
                    tenantContext.TenantId.Value.ToString()
                );
            }

            await Task.CompletedTask;
        });
    });

var app = builder.Build();

// Make sure than the DB is created at starting in development
// Demo migration.
if (!app.Environment.IsEnvironment("Test"))
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        db.Database.EnsureCreated();
    }
}

// Middleware pipelines register
app.UseMiddleware<TenantMiddleware>();
app.UseRateLimiter();

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

// CATALOG ENDPOINTS (HU 2.1 / HU 3.1):
app.MapGet("/api/catalog/products", async (CatalogDbContext db) =>
{
    // EF Core will automatically apply the global filter based on scoped TenantContext
    var products = await db.Products.ToListAsync();
    return Results.Ok(products);
}).RequireRateLimiting("TenantRateLimiter");

app.MapPost("/api/catalog/products", async (
    CreateProductRequest request,
    CatalogDbContext db
) =>
{
    var product = new Product
    {
        ProductId = Guid.NewGuid(),
        SKU = request.SKU,
        Name = request.Name,
        Price = request.Price,
        Stock = request.Stock,
        IsActive = true
    };

    db.Products.Add(product);

    await db.SaveChangesAsync();

    return Results.Created(
        $"/api/catalog/products/{product.ProductId}", 
        product
    );
}).RequireRateLimiting("TenantRateLimiter");

app.MapReverseProxy();

app.Run();

public partial class Program { }