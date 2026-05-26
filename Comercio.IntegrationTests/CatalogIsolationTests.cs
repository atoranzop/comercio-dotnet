using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

using Catalog.Infrastructure.Persistence;
using Catalog.Core.Entities;
using Comercio.Gateway;
using Xunit.Sdk;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Comercio.IntegrationTests;

public class CatalogIsolationTests : 
    IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CatalogIsolationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            var customFactory = factory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<CatalogDbContext>>();
                    services.RemoveAll<CatalogDbContext>();

                    services.AddDbContext<CatalogDbContext>(options =>
                    options.UseInMemoryDatabase("GatewayTestDb"));
                });
            });
        });

        SeedDatabase();
    }

    private void SeedDatabase()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        // Clean the database befor populating it
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();

        var guidAlpha = Guid.Parse("a1111111-1111-1111-1111-111111111111");
        db.Products.AddRange(
            new Product
            {
                ProductId = Guid.NewGuid(),
                TenantId = guidAlpha,
                SKU = "ALPHA-01",
                Name = "Premium Alpha T-Shirt",
                Price = 25.00m,
                Stock = 10
            },
            new Product
            {
                ProductId = Guid.NewGuid(),
                TenantId = guidAlpha,
                SKU = "ALPHA-02",
                Name = "Alpha Hat",
                Price = 15.00m,
                Stock = 5
            }
        );

        // Populate a product belonging to Beta Store
        var guidBeta = Guid.Parse("b2222222-2222-2222-2222-222222222222");
        db.Products.Add(
            new Product
            {
                ProductId = Guid.NewGuid(),
                TenantId = guidBeta,
                SKU = "BETA-01",
                Name = "Beta Run Shoes",
                Price = 80.00m,
                Stock = 3
            }
        );

        db.SaveChanges();
    }

    [Fact]
    public async Task CatalogQuery_MustReturnCurrentTenantProductsOnly()
    {
        // Arrange: Create integration client simulating Alpha Store navigation
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/catalog/products"
        );
        request.Headers.Add(
            "X-Tenant-Id",
            "alpha-store"
        );

        // Act: Consume the catalog API
        var response = await client.SendAsync(request);

        // Assert: Validate logic reading isolation
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var products = await response.Content.ReadFromJsonAsync<List<ProductResponse>>();
        Assert.NotNull(products);

        Assert.Equal(2, products.Count);
        Assert.All(products, p => Assert.Contains("Alpha", p.Name));
    }

    [Fact]
    public async Task ProductCreation_MustInjectTenantIdAutomatically_AndNotAllowCrossreading()
    {
        // Arrange: Create client for Beta Store
        var client = _factory.CreateClient();
        
        // 1. Register product on Beta Store
        var createRequest = new HttpRequestMessage(
            HttpMethod.Post, 
            "/api/catalog/products"
        );

        createRequest.Headers.Add("X-Tenant-Id", "beta-store");
        createRequest.Content = JsonContent.Create(
            new 
            { 
                SKU = "BETA-02", 
                Name = "Camiseta Beta Pro", 
                Price = 45.00m, 
                Stock = 20 
            }
        );

        // Act: Save product
        var createResponse = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // 2. Try to consult Alpha Store Catalog
        var queryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/products");
        queryRequest.Headers.Add("X-Tenant-Id", "alpha-store");

        var queryResponse = await client.SendAsync(queryRequest);
        var productsAlfa = await queryResponse.Content.ReadFromJsonAsync<List<ProductResponse>>();

        // Assert: Validate that the new product injected on Beta store 
        // cannot be visible on Alpha Store 
        Assert.NotNull(productsAlfa);
        Assert.Equal(2, productsAlfa.Count); 
        Assert.True(productsAlfa.All(p => !p.Name.Contains("Beta")),
            "No product from Alpha should contain 'Beta' on its name.");
    }

    private class ProductResponse
    {
        public Guid ProductId { get; set; }
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SKU { get; set; } = string.Empty;
    }
}