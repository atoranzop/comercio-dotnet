using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Catalog.Infrastructure.Persistence;
using Catalog.Core.Entities;
using Orders.Infrastructure.Persistence;
using Comercio.Gateway;
using Microsoft.AspNetCore.Hosting;

namespace Comercio.IntegrationTests;

public class OrderConcurrencyTests: IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly SqliteConnection _catalogMasterConnection;
    private readonly SqliteConnection _ordersMasterConnection;

    public OrderConcurrencyTests(WebApplicationFactory<Program> factory)
    {
        _catalogMasterConnection = new SqliteConnection("Data Source=file:concurrent_catalog?mode=memory&cache=shared");
        _ordersMasterConnection = new SqliteConnection("Data Source=file:concurrent_orders?mode=memory&cache=shared");
        _catalogMasterConnection.Open();
        _ordersMasterConnection.Open();

        // Configure custom services for testing with SQLite in-memory and shared cache
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(DbContextOptions<CatalogDbContext>));
                services.RemoveAll(typeof(CatalogDbContext));

                services.AddDbContext<CatalogDbContext>(options =>
                    options.UseSqlite("Data Source=file:concurrent_catalog?mode=memory&cache=shared"));

                services.RemoveAll(typeof(DbContextOptions<OrdersDbContext>));
                services.RemoveAll(typeof(OrdersDbContext));

                services.AddDbContext<OrdersDbContext>(options =>
                    options.UseSqlite("Data Source=file:concurrent_orders?mode=memory&cache=shared"));
            });
        });

        using var scope = _factory.Services.CreateScope();
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var ordersDb = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        catalogDb.Database.EnsureCreated();
        ordersDb.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _catalogMasterConnection.Dispose();
        _ordersMasterConnection.Dispose();
    }

    private Guid SeedProductWithLimitedStock()
    {
        using var scope = _factory.Services.CreateScope();
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        // Seed a product with limited stock for beta-store tenant
        // TenantId for "beta-store" is b2222222-2222-2222-2222-222222222222
        var product = new Product
        {
            ProductId = Guid.NewGuid(),
            TenantId = Guid.Parse("b2222222-2222-2222-2222-222222222222"),
            SKU = "TESTSKU",
            Name = "Test Product",
            Price = 10.00m,
            Stock = 1, // Limited stock
            IsActive = true
        };

        catalogDb.Products.Add(product);
        catalogDb.SaveChanges();
        
        return product.ProductId;
    }

    [Fact]
    public async Task Checkout_MustPreventOversales_WhenConcurrentOrdersExceedStock()
    {
        // Arrange: Seed a product with limited stock
        var productId = SeedProductWithLimitedStock();

        var client = _factory.CreateClient();
        string subdomain = "beta-store"; // Use existing subdomain from InMemoryTenantStore

        // Act: Simulate two concurrent checkout requests for the SAME product
        var checkoutRequest1 = new HttpRequestMessage(HttpMethod.Post, "/api/orders/checkout");
        checkoutRequest1.Headers.Add("X-Tenant-Id", subdomain);
        checkoutRequest1.Content = JsonContent.Create(new
        {
            CustomerId = Guid.NewGuid(),
            Items = new[]
            {
                new { ProductId = productId, Quantity = 1 }
            }
        });

        var checkoutRequest2 = new HttpRequestMessage(HttpMethod.Post, "/api/orders/checkout");
        checkoutRequest2.Headers.Add("X-Tenant-Id", subdomain);
        checkoutRequest2.Content = JsonContent.Create(new
        {
            CustomerId = Guid.NewGuid(),
            Items = new[]
            {
                new { ProductId = productId, Quantity = 1 }
            }
        });

        // Send both requests concurrently
        var task1 = client.SendAsync(checkoutRequest1);
        var task2 = client.SendAsync(checkoutRequest2);

        await Task.WhenAll(task1, task2);

        // Assert: One request should succeed (201 Created) and the other should fail (409 Conflict)
        var response1 = await task1;
        var response2 = await task2;

        Assert.True(
            (response1.StatusCode == HttpStatusCode.Created &&
             (response2.StatusCode == HttpStatusCode.Conflict || response2.StatusCode == HttpStatusCode.BadRequest)) ||
            (response2.StatusCode == HttpStatusCode.Created &&
             (response1.StatusCode == HttpStatusCode.Conflict || response1.StatusCode == HttpStatusCode.BadRequest)),
            $"Expected one Created and one failure, but got response1={response1.StatusCode} and response2={response2.StatusCode}."
        );
    }
}