using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Catalog.Infrastructure.Persistence;
using Orders.Infrastructure.Persistence;
using Xunit;
using Comercio.Gateway;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Comercio.IntegrationTests;

public class GatewayRateLimitingTests: IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GatewayRateLimitingTests(WebApplicationFactory<Program> factory)
    {
        // Configure custom services for testing with isolated in-memory database
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(DbContextOptions<CatalogDbContext>));
                services.RemoveAll(typeof(CatalogDbContext));

                services.AddDbContext<CatalogDbContext>(options =>
                    options.UseInMemoryDatabase($"RateLimitingTestDb-{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

                // Also register OrdersDbContext for the checkout endpoint
                services.RemoveAll(typeof(DbContextOptions<Orders.Infrastructure.Persistence.OrdersDbContext>));
                services.RemoveAll(typeof(Orders.Infrastructure.Persistence.OrdersDbContext));

                services.AddDbContext<Orders.Infrastructure.Persistence.OrdersDbContext>(options =>
                    options.UseInMemoryDatabase($"OrdersRateLimitingTestDb-{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
            });
        });
    }

    [Fact]
    public async Task RateLimiter_MustRetur429TooManyRequests_WhenBasicTenantExceedsLimit()
    {
        // Arrange: Create a mocked targeting Beta Store 
        // (Basic Plan: Limited to 5 req per 10 sec)
        var client = _factory.CreateClient();
        string subdomainBasic = "beta-store";

        // Act: Sent 5 consecutives requests (all must return 200 OK)
        for (int i = 0; i < 5; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/products");
            request.Headers.Add("X-Tenant-Id", subdomainBasic);
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Sent 6th req on the same timeframe
        var limitExceededRequest = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/products");
        limitExceededRequest.Headers.Add("X-Tenant-Id", subdomainBasic);
        var limitExceededResponse = await client.SendAsync(limitExceededRequest);

        // Assert: System must return atomically status 429
        Assert.Equal(HttpStatusCode.TooManyRequests, limitExceededResponse.StatusCode); 
    }

    [Fact]
    public async Task RateLimiter_NotBlockingPremiumTenant_WhenBasicQuotaExceeded()
    {
        // Arrange: Create client targeting alpha store 
        // (rate limit 100 req per 10 sec)
        var client = _factory.CreateClient();
        string subdomainPremium = "alpha-store";

        // Act: Perform 10 req (exceeds basic rate limit)
        for (int i = 0; i < 10; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/products");
            request.Headers.Add("X-Tenant-Id", subdomainPremium);
            var response = await client.SendAsync(request);
            
            // Assert: All requests must return status 200
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

}