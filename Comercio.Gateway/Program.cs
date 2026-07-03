using Comercio.Shared;
using Comercio.Gateway;
using Comercio.Gateway.Dtos;
using Catalog.Infrastructure.Persistence;
using Catalog.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy.Transforms;
using System.Threading.RateLimiting;
using Orders.Infrastructure.Persistence;
using Orders.Core.Entities;

var builder = WebApplication.CreateBuilder(args);

// Dependency injections for common SaaS services
builder.Services.AddSingleton<InMemoryTenantStore>();
builder.Services.AddScoped<ITenantContext, TenantContext>();

// Inject DbContexts configuring local SQLite independent databases (modular monolite architecture)
// This operation is skipped in Test environment - tests will configure it
if (!builder.Environment.IsEnvironment("Test"))
{
    var catalogDbPath = Path.Combine(AppContext.BaseDirectory, "comercio_catalog.db");
    var ordersDbPath = Path.Combine(AppContext.BaseDirectory, "comercio_orders.db");
    builder.Services.AddDbContext<CatalogDbContext>(options =>
        options.UseSqlite($"Data Source={catalogDbPath}"));
    builder.Services.AddDbContext<OrdersDbContext>(options => 
        options.UseSqlite($"Data Source={ordersDbPath}"));
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
                transformContext.ProxyRequest.Headers.Add(
                    "X-Internal-TenantId",
                    tenantContext.TenantId.ToString()
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
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        catalogDb.Database.EnsureCreated();

        var ordersDb = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        ordersDb.Database.EnsureCreated();
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

app.MapPost("/api/orders/checkout", async (
    CheckoutRequest request, 
    CatalogDbContext catalogDb, 
    OrdersDbContext ordersDb
) =>
{
    if (request.Items == null || !request.Items.Any())
    {
        return Results.BadRequest(new
        {
            Message = "No items provided for checkout."
        });
    }

    // 1. Start explicit transaction over the Catalog database to
    // freeze the stock atomically
    using var catalogTransaction = await catalogDb.Database.BeginTransactionAsync();

    try
    {
        var orderItems = new List<OrderItem>();
        decimal totalPrice = 0;

        foreach (var item in request.Items)
        {
            // Consult the product (with global TenantId filter applied automatically by EF Core)
            var product = await catalogDb.Products.SingleOrDefaultAsync(p => p.ProductId == item.ProductId);

            if (product == null)
            {
                await catalogTransaction.RollbackAsync();
                return Results.NotFound(new
                {
                    Error = $"The product with ID {item.ProductId} does not exist on the database"
                });
            }

            // Validate available stock strictly
            if (product.Stock < item.Quantity)
            {
                await catalogTransaction.RollbackAsync();
                return Results.BadRequest(new
                {
                    Error = $"Insufficient stock for product {product.Name}. " +
                        "Available: {product.Stock}, Requested: {item.Quantity}"
                });
            }

            // Discount available stock
            product.Stock -= item.Quantity;

            // Update concurrency token to break paralel concurrent transactions optimism 
            product.ConcurrencyToken = Guid.NewGuid();

            // Freeze the unit price at the moment of the purchase
            var priceAtPurchase = product.Price;
            totalPrice += priceAtPurchase * item.Quantity;

            orderItems.Add(new OrderItem
            {
                OrderItemId = Guid.NewGuid(),
                ProductId = product.ProductId,
                Quantity = item.Quantity,
                UnitPriceAtPurchase = priceAtPurchase
            });
        }

        // Save changes on the catalog database (it will launch DbUpdateConcurrencyException if a concurrent 
        // transaction has modified the same product on a way that restricts the stock to a negative value)
        await catalogDb.SaveChangesAsync();
        await catalogTransaction.CommitAsync();

        // 2. Register the header and details of the order on the Orders database using logical isolation
        var order = new Order
        {
            OrderId = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            CreatedAt = DateTime.UtcNow,
            Status = "Pending",
            TotalPrice = totalPrice,
            Items = orderItems
        };

        ordersDb.Orders.Add(order);
        await ordersDb.SaveChangesAsync();

        return Results.Created(
            $"/api/orders/{order.OrderId}",
            new
            {
                order.OrderId,
                order.CreatedAt,
                order.TotalPrice,
                order.Status,
                message = "Order registered successfully with logical isolation per tenant."
            }
        );
    }
    catch (DbUpdateConcurrencyException)
    {
        // Rollback the transaction if a concurrency conflict occurs
        await catalogTransaction.RollbackAsync();
        return Results.Conflict(new
        {
            Message = "Concurrency conflict detected. " +
                "The stock of one or more products has changed. " + 
                "Please try again."
        });
    }
    catch (Exception ex)
    {
        await catalogTransaction.RollbackAsync();
        return Results.Problem("An unexpected error occurred during the checkout process: " 
            + ex.Message);
    }
});

app.MapReverseProxy();

app.Run();

public partial class Program { }