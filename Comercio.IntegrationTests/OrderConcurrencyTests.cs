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
    private readonly SqliteConnection _masterConnection;

    public OrderConcurrencyTests(WebApplicationFactory<Program> factory)
    {
        _masterConnection = new SqliteConnection("Data Source=file:concurrent_checkout?mode=memory&cache=shared");
        _masterConnection.Open();

        // Both contexts share one connection so the checkout transaction spans them.
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<SqliteConnection>();
                services.AddScoped(_ => new SqliteConnection(
                    "Data Source=file:concurrent_checkout?mode=memory&cache=shared"));

                services.RemoveAll(typeof(DbContextOptions<CatalogDbContext>));
                services.RemoveAll(typeof(CatalogDbContext));

                services.AddDbContext<CatalogDbContext>((provider, options) =>
                    options.UseSqlite(provider.GetRequiredService<SqliteConnection>()));

                services.RemoveAll(typeof(DbContextOptions<OrdersDbContext>));
                services.RemoveAll(typeof(OrdersDbContext));

                services.AddDbContext<OrdersDbContext>((provider, options) =>
                    options.UseSqlite(provider.GetRequiredService<SqliteConnection>()));
            });
        });

        using var scope = _factory.Services.CreateScope();
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var ordersDb = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        catalogDb.Database.EnsureCreated();
        ordersDb.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "Orders" (
                "OrderId" TEXT NOT NULL CONSTRAINT "PK_Orders" PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "CustomerId" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "TotalPrice" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "OrderItems" (
                "OrderItemId" TEXT NOT NULL CONSTRAINT "PK_OrderItems" PRIMARY KEY,
                "OrderId" TEXT NOT NULL,
                "ProductId" TEXT NOT NULL,
                "Quantity" INTEGER NOT NULL,
                "UnitPriceAtPurchase" TEXT NOT NULL,
                CONSTRAINT "FK_OrderItems_Orders_OrderId" FOREIGN KEY ("OrderId")
                    REFERENCES "Orders" ("OrderId") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "OutboxMessages" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_OutboxMessages" PRIMARY KEY,
                "EventType" TEXT NOT NULL,
                "Payload" TEXT NOT NULL,
                "OccurredAtUtc" TEXT NOT NULL,
                "ProcessedAtUtc" TEXT NULL,
                "Attempts" INTEGER NOT NULL,
                "NextAttemptAtUtc" TEXT NOT NULL,
                "LastError" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_OrderItems_OrderId" ON "OrderItems" ("OrderId");
            CREATE INDEX IF NOT EXISTS "IX_Orders_TenantId" ON "Orders" ("TenantId");
            """);
    }

    public void Dispose()
    {
        _masterConnection.Dispose();
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