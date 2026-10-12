using Comercio.Shared;
using Comercio.Gateway;
using Catalog.Infrastructure.Persistence;
using Catalog.Core.Entities;
using Orders.Core.Entities;
using Orders.Core.Events;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Clients;
using Orders.Infrastructure.BackgroundServices;
using Orders.Infrastructure.Outbox;
using Comercio.Gateway.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Polly;
using MediatR;
using Comercio.Gateway.Dtos;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

DotNetEnv.Env.NoClobber().TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// 1. Registrar servicios de plataforma
builder.Services.AddScoped<ITenantContext, TenantContext>();

// 2. Registrar DbContexts
if (builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddSingleton<ITenantStore, InMemoryTenantStore>();
}
else
{
    var postgresConnection = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
            "Missing connection string 'Postgres'. Configure ConnectionStrings__Postgres.");

    builder.Services.AddScoped(_ => new NpgsqlConnection(postgresConnection));

    builder.Services.AddDbContext<TenantDbContext>((services, options) =>
        options.UseNpgsql(services.GetRequiredService<NpgsqlConnection>(), npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "platform")));
    builder.Services.AddScoped<ITenantStore, PostgresTenantStore>();

    builder.Services.AddDbContext<CatalogDbContext>((services, options) =>
        options.UseNpgsql(services.GetRequiredService<NpgsqlConnection>(), npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "catalog")));

    builder.Services.AddDbContext<OrdersDbContext>((services, options) =>
        options.UseNpgsql(services.GetRequiredService<NpgsqlConnection>(), npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders")));
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
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddScoped<OutboxDispatcher>();
    builder.Services.AddHostedService<OutboxDispatcherBackgroundService>();
}

builder.Services.AddControllers();

// Configuración de Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (rejectionContext, cancellationToken) =>
    {
        rejectionContext.HttpContext.Response.ContentType = "application/json";
        await rejectionContext.HttpContext.Response.WriteAsJsonAsync(
            ApiResponse<object>.Failed(
                "rate_limit_exceeded",
                "The request rate limit has been exceeded."),
            cancellationToken);
    };
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

if (app.Environment.IsDevelopment())
{
    await TenantDevelopmentSeeder.SeedAsync(app.Services, app.Lifetime.ApplicationStopping);
}

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    await response.WriteAsJsonAsync(
        ApiResponse<object>.Failed(
            "http_error",
            "The request could not be completed."),
        statusContext.HttpContext.RequestAborted);
});
app.UseMiddleware<TenantMiddleware>();
app.UseRateLimiter();

// Endpoints Diagnóstico y Catálogo
app.MapGet("/api/catalog/products", async (CatalogDbContext db) =>
{
    var products = await db.Products.ToListAsync();
    return Results.Ok(ApiResponse<IReadOnlyList<Product>>.Succeeded(products));
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
    return Results.Created(
        $"/api/catalog/products/{product.ProductId}",
        ApiResponse<Product>.Succeeded(product));
}).RequireRateLimiting("TenantRateLimiter");

// Endpoint de checkout que publica el evento OrderCreatedEvent usando MediatR
app.MapPost("/api/orders/checkout", async (CheckoutRequest request, CatalogDbContext catalogDb, OrdersDbContext ordersDb) =>
{
    if (request.Items == null || request.Items.Count == 0)
    {
        return Results.BadRequest(ApiResponse<object>.Failed(
            "validation_failed",
            "El carrito no contiene productos."));
    }

    await using var catalogTransaction = await catalogDb.Database.BeginTransactionAsync();
    Order order;
    try
    {
        await ordersDb.Database.UseTransactionAsync(
            catalogTransaction.GetDbTransaction());

        var orderItems = new List<OrderItem>();
        decimal totalPrice = 0;

        foreach (var itemRequest in request.Items)
        {
            var product = await catalogDb.Products.SingleOrDefaultAsync(p => p.ProductId == itemRequest.ProductId);

            if (product == null)
            {
                await catalogTransaction.RollbackAsync();
                return Results.NotFound(ApiResponse<object>.Failed(
                    "product_not_found",
                    "Uno de los productos solicitados no existe."));
            }

            if (product.Stock < itemRequest.Quantity)
            {
                await catalogTransaction.RollbackAsync();
                return Results.BadRequest(ApiResponse<object>.Failed(
                    "insufficient_stock",
                    "No hay stock suficiente para uno de los productos solicitados."));
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

        order = new Order
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
        ordersDb.OutboxMessages.Add(OutboxMessage.Create(
            new OrderCreatedEvent(
                order.OrderId,
                order.TenantId,
                order.TotalPrice,
                order.CustomerId)));
        await ordersDb.SaveChangesAsync();

        await catalogTransaction.CommitAsync();
    }
    catch (DbUpdateConcurrencyException)
    {
        await catalogTransaction.RollbackAsync();
        return Results.Json(
            ApiResponse<object>.Failed(
                "concurrency_conflict",
                "The requested changes conflict with a concurrent update."),
            statusCode: StatusCodes.Status409Conflict);
    }

    return Results.Created(
        $"/api/orders/{order.OrderId}",
        ApiResponse<object>.Succeeded(new
        {
            order.OrderId,
            order.TotalPrice,
            order.Status,
            Message = "Orden registrada; su evento quedó en cola para procesamiento."
        }));
}).RequireRateLimiting("TenantRateLimiter");

// Endpoint que ejecuta la confirmación de pago llamando al cliente resiliente con Polly
app.MapPost("/api/orders/{orderId:guid}/confirm-payment", async (Guid orderId, OrdersDbContext ordersDb, MockPaymentClient paymentClient) =>
{
    var order = await ordersDb.Orders.FindAsync(orderId);
    if (order == null)
    {
        return Results.NotFound(ApiResponse<object>.Failed(
            "order_not_found",
            "La orden solicitada no existe."));
    }

    if (order.Status != "Pendiente de Pago")
    {
        return Results.BadRequest(ApiResponse<object>.Failed(
            "invalid_order_state",
            "La orden no se puede pagar en su estado actual."));
    }

    // Llamamos a la pasarela de pagos simulada protegida por Polly (Reintentos + Disyuntor)
    var success = await paymentClient.ProcessPaymentAsync(order.OrderId, order.TotalPrice);

    if (success)
    {
        order.Status = "Pagada";
        await ordersDb.SaveChangesAsync();
        return Results.Ok(ApiResponse<object>.Succeeded(new
        {
            order.OrderId,
            order.Status,
            Message = "Pago confirmado mediante cliente resiliente."
        }));
    }

    return Results.BadRequest(ApiResponse<object>.Failed(
        "payment_declined",
        "El cobro fue declinado por la pasarela externa de pagos."));
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
        return Results.Json(
            ApiResponse<object>.Failed(
                "payment_service_unavailable",
                "El servicio de pagos no está disponible temporalmente."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(ApiResponse<object>.Succeeded(new
    {
        Success = true,
        TransactionId = Guid.NewGuid()
    }));
});

app.Run();

public record PaymentRequest(Guid OrderId, decimal Amount);
public partial class Program { }
