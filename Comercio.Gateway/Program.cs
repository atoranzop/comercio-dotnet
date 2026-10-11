using Comercio.Shared;
using Comercio.Gateway;
using Catalog.Infrastructure.Persistence;
using Catalog.Core.Entities;
using Orders.Core.Entities;
using Orders.Core.Events;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Clients;
using Orders.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Polly;
using MediatR;
using Comercio.Gateway.Dtos;

var builder = WebApplication.CreateBuilder(args);

// 1. Registrar servicios de plataforma
builder.Services.AddSingleton<InMemoryTenantStore>();
builder.Services.AddScoped<ITenantContext, TenantContext>();

// 2. Registrar DbContexts
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddDbContext<CatalogDbContext>(options =>
        options.UseSqlite("Data Source=../comercio_catalog.db"));

    builder.Services.AddDbContext<OrdersDbContext>(options =>
        options.UseSqlite("Data Source=../comercio_orders.db"));
}

// 3. REGISTRAR CLIENTE HTTP DE PASARELA DE PAGOS CON RESILIENCIA DE POLLY (RNF-4)
builder.Services.AddHttpClient<MockPaymentClient>(client =>
{
    // Simulamos que llama a un procesador externo (en este caso, apunta a nuestra propia API Gateway para las pruebas)
    client.BaseAddress = new Uri("http://localhost:5000");
})
// Política 1: Reintentos asíncronos exponenciales ante fallas transitorias de red
.AddTransientHttpErrorPolicy(policy => policy.WaitAndRetryAsync(
    retryCount: 3,
    sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) // Reintentos en: 2s, 4s, 8s
))
// Política 2: Circuit Breaker para detener llamadas si el servicio se cae persistentemente
.AddTransientHttpErrorPolicy(policy => policy.CircuitBreakerAsync(
    handledEventsAllowedBeforeBreaking: 2, // Abre el circuito tras 2 fallos de red seguidos
    durationOfBreak: TimeSpan.FromSeconds(15) // Lo mantiene abierto e impide llamadas por 15 segundos
));

// 4. Registrar MediatR para la orquestación de eventos
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(OrderCreatedEvent).Assembly));

// 5. Registrar el procesador asíncrono en segundo plano (SaaS Background Stock Watcher)
builder.Services.AddHostedService<StockReleaseBackgroundService>();

builder.Services.AddControllers();

// Configuración de Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("TenantRateLimiter", context =>
    {
        var tenantContext = context.RequestServices.GetRequiredService<ITenantContext>();
        var tenantKey = tenantContext.TenantId?.ToString() ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonimo";
        var isPremium = tenantContext.Plan?.Equals("Premium", StringComparison.OrdinalIgnoreCase) ?? false;
        int permitLimit = isPremium ? 100 : 5;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: tenantKey,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0
            });
    });
});

var app = builder.Build();

// Garantizar creación de base de datos relacionales locales
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.EnsureCreated();
    scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.EnsureCreated();
}

app.UseMiddleware<TenantMiddleware>();
app.UseRateLimiter();

// Endpoints Diagnóstico y Catálogo
app.MapGet("/api/catalog/products", async (CatalogDbContext db) =>
{
    var products = await db.Products.ToListAsync();
    return Results.Ok(products);
}).RequireRateLimiting("TenantRateLimiter");

app.MapPost("/api/catalog/products", async (CreateProductRequest request, CatalogDbContext db) =>
{
    var product = new Product
    {
        ProductId = Guid.NewGuid(),
        SKU = request.SKU,
        Name = request.Name,
        Price = request.Price,
        Stock = request.Stock,
        ConcurrencyToken = Guid.NewGuid()
    };
    db.Products.Add(product);
    await db.SaveChangesAsync();
    return Results.Created($"/api/catalog/products/{product.ProductId}", product);
}).RequireRateLimiting("TenantRateLimiter");

// Endpoint de checkout que publica el evento OrderCreatedEvent usando MediatR
app.MapPost("/api/orders/checkout", async (CheckoutRequest request, CatalogDbContext catalogDb, OrdersDbContext ordersDb, IMediator mediator) =>
{
    if (request.Items == null || request.Items.Count == 0)
    {
        return Results.BadRequest(new { Error = "El carrito no contiene productos." });
    }

    using var catalogTransaction = await catalogDb.Database.BeginTransactionAsync();
    try
    {
        var orderItems = new List<OrderItem>();
        decimal totalPrice = 0;

        foreach (var itemRequest in request.Items)
        {
            var product = await catalogDb.Products.SingleOrDefaultAsync(p => p.ProductId == itemRequest.ProductId);

            if (product == null)
            {
                await catalogTransaction.RollbackAsync();
                return Results.NotFound(new { Error = $"Producto '{itemRequest.ProductId}' no existe." });
            }

            if (product.Stock < itemRequest.Quantity)
            {
                await catalogTransaction.RollbackAsync();
                return Results.BadRequest(new { Error = $"Stock insuficiente para '{product.Name}'." });
            }

            product.Stock -= itemRequest.Quantity;
            product.ConcurrencyToken = Guid.NewGuid();

            var priceAtPurchase = product.Price;
            totalPrice += priceAtPurchase * itemRequest.Quantity;

            orderItems.Add(new OrderItem
            {
                OrderItemId = Guid.NewGuid(),
                ProductId = product.ProductId,
                Quantity = itemRequest.Quantity,
                UnitPriceAtPurchase = priceAtPurchase
            });
        }

        await catalogDb.SaveChangesAsync();
        await catalogTransaction.CommitAsync();

        var order = new Order
        {
            OrderId = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            CreatedAt = DateTime.UtcNow,
            Status = "Pendiente de Pago",
            TotalPrice = totalPrice,
            Items = orderItems
        };

        ordersDb.Orders.Add(order);
        await ordersDb.SaveChangesAsync();

        // PUBLICACIÓN DEL EVENTO DE DOMINIO (RNF-6 / HU-3.3):
        // Disparamos el evento asíncronamente en segundo plano
        await mediator.Publish(new OrderCreatedEvent(order.OrderId, order.TenantId, order.TotalPrice, order.CustomerId));

        return Results.Created($"/api/orders/{order.OrderId}", new 
        {
            order.OrderId,
            order.TotalPrice,
            order.Status,
            Message = "Orden registrada y evento asíncrono publicado."
        });
    }
    catch (DbUpdateConcurrencyException)
    {
        await catalogTransaction.RollbackAsync();
        return Results.StatusCode(StatusCodes.Status409Conflict);
    }
    catch (Exception ex)
    {
        await catalogTransaction.RollbackAsync();
        return Results.Problem($"Error en checkout: {ex.Message}");
    }
}).RequireRateLimiting("TenantRateLimiter");

// Endpoint que ejecuta la confirmación de pago llamando al cliente resiliente con Polly
app.MapPost("/api/orders/{orderId:guid}/confirm-payment", async (Guid orderId, OrdersDbContext ordersDb, MockPaymentClient paymentClient) =>
{
    var order = await ordersDb.Orders.FindAsync(orderId);
    if (order == null)
    {
        return Results.NotFound(new { Error = "La orden no existe." });
    }

    if (order.Status != "Pendiente de Pago")
    {
        return Results.BadRequest(new { Error = $"La orden no se puede pagar porque se encuentra en estado '{order.Status}'." });
    }

    // Llamamos a la pasarela de pagos simulada protegida por Polly (Reintentos + Disyuntor)
    var success = await paymentClient.ProcessPaymentAsync(order.OrderId, order.TotalPrice);

    if (success)
    {
        order.Status = "Pagada";
        await ordersDb.SaveChangesAsync();
        return Results.Ok(new { order.OrderId, order.Status, Message = "Pago confirmado mediante cliente resiliente." });
    }

    return Results.BadRequest(new { Error = "El cobro fue declinado por la pasarela externa de pagos." });
}).RequireRateLimiting("TenantRateLimiter");

// ENDPOINT SIMULADO DE LA PASARELA EXTERNA DE PAGOS (Para pruebas de fallas)
// Permite probar que Polly reintente y abra el circuito si inyectamos errores.
var paymentFailureCounter = 0;
app.MapPost("/api/external/payments/process", (PaymentRequest request) =>
{
    paymentFailureCounter++;

    // Inyectamos fallas transitorias de red para activar los reintentos de Polly:
    // Fallará las primeras 2 llamadas con error de red (HTTP 503) y aprobará en la 3era.
    if (paymentFailureCounter % 3 != 0)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new { Success = true, TransactionId = Guid.NewGuid() });
});

app.Run();

public record PaymentRequest(Guid OrderId, decimal Amount);
public partial class Program { }
