using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Catalog.Infrastructure.Persistence;
using Catalog.Core.Entities;
using Orders.Infrastructure.Persistence;
using Orders.Core.Entities;
using Comercio.Gateway;
using Comercio.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Comercio.IntegrationTests;

/// <summary>
/// Prueba el temporizador en segundo plano de expiración y devolución de reservas de stock (RF-3.5 / HU-3.5).
/// </summary>
public class StockExpirationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Guid _tenantAlfaId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
    private readonly Guid _productId = Guid.NewGuid();

    public StockExpirationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureTestServices(services =>
            {
                RemoveDbContextRegistration<CatalogDbContext>(services);
                services.AddDbContext<CatalogDbContext>(options =>
                    options.UseInMemoryDatabase("CatalogTestDb")
                        .ConfigureWarnings(w => w.Ignore(
                            Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

                RemoveDbContextRegistration<OrdersDbContext>(services);
                services.AddDbContext<OrdersDbContext>(options =>
                    options.UseInMemoryDatabase("OrdersTestDb")
                        .ConfigureWarnings(w => w.Ignore(
                            Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
            });
        });

        SeedDatabase();
    }

    private void SeedDatabase()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>()
            .SetTenant(_tenantAlfaId, "alpha-store", "Premium", true);

        // Insertar un producto con stock de 5 unidades en Tienda Alfa
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        catalogDb.Database.EnsureDeleted();
        catalogDb.Database.EnsureCreated();

        catalogDb.Products.Add(new Product
        {
            ProductId = _productId,
            TenantId = _tenantAlfaId,
            SKU = "EXP-99",
            Name = "Producto Perecedero",
            Price = 10.00m,
            Stock = 5,
            ConcurrencyToken = Guid.NewGuid()
        });

        catalogDb.SaveChanges();
    }

    [Fact]
    public async Task BackgroundService_DebeCancelarPedidoYDevolverStock_CuandoLaOrdenExcedeLos15MinutosUnpaid()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>()
            .SetTenant(_tenantAlfaId, "alpha-store", "Premium", true);
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var ordersDb = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        // 1. Simular un pedido creado hace 16 minutos (ya expirado)
        var orderId = Guid.NewGuid();
        var order = new Order
        {
            OrderId = orderId,
            TenantId = _tenantAlfaId,
            CustomerId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow.AddMinutes(-16), // Excedido los 15 minutos reglamentarios
            Status = "Pending payment",
            TotalPrice = 20.00m,
            Items = [
                new OrderItem { OrderItemId = Guid.NewGuid(), OrderId = orderId, ProductId = _productId, Quantity = 2, UnitPriceAtPurchase = 10.00m }
            ]
        };

        // Reducimos manualmente el stock para simular el checkout previo de 2 unidades
        var product = await catalogDb.Products.FindAsync(_productId);
        Assert.NotNull(product);
        product.Stock = 3; // Disminuido de 5 a 3 por la reserva temporal

        catalogDb.SaveChanges();

        ordersDb.Orders.Add(order);
        ordersDb.SaveChanges();

        // 2. Esperar 12 segundos para dar tiempo al BackgroundService a ejecutarse en su ciclo
        await Task.Delay(TimeSpan.FromSeconds(12));

        // 3. Consultar estados finales
        await catalogDb.Entry(product).ReloadAsync();
        await ordersDb.Entry(order).ReloadAsync();
        var finalProduct = await catalogDb.Products.FindAsync(_productId);
        var finalOrder = await ordersDb.Orders.FindAsync(orderId);

        // Assert: El Hosted Service debe haber revertido el stock de 3 a 5 de vuelta
        Assert.NotNull(finalProduct);
        Assert.Equal(5, finalProduct.Stock);

        // Assert: El estado del pedido debe transicionar atómicamente a "Cancelled"
        Assert.NotNull(finalOrder);
        Assert.Equal("Cancelled", finalOrder.Status);
    }

    private static void RemoveDbContextRegistration<TContext>(IServiceCollection services)
        where TContext : DbContext
    {
        services.RemoveAll<DbContextOptions<TContext>>();
        services.RemoveAll<TContext>();
    }
}