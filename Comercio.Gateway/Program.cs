using Comercio.Shared;
using Comercio.Gateway;
using Comercio.Gateway.Dtos;
using Catalog.Infrastructure.Persistence;
using Catalog.Core.Entities;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Dependency injections
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
app.MapGet("api/catalog/products", async (CatalogDbContext db) =>
{
    // EF Core will automatically apply the global filter based on scoped TenantContext
    var products = await db.Products.ToListAsync();
    return Results.Ok(products);
});

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
});

app.Run();

public partial class Program { }