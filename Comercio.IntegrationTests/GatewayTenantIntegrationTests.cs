using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Catalog.Infrastructure.Persistence;
using Orders.Infrastructure.Persistence;
using Xunit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Comercio.Shared;

namespace Comercio.IntegrationTests;

/// <summary>
/// Industrial integration tests to check the perimetral behavior of 
/// the API Gateway and its middlewares.
/// </summary>
public class GatewayTenantIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public GatewayTenantIntegrationTests(WebApplicationFactory<Program> factory)
    {
        // Configure custom services for testing
        var customFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<CatalogDbContext>>();
                services.RemoveAll<CatalogDbContext>();

                services.AddDbContext<CatalogDbContext>(options =>
                    options.UseInMemoryDatabase("GatewayTestDb")
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

                services.RemoveAll<DbContextOptions<OrdersDbContext>>();
                services.RemoveAll<OrdersDbContext>();

                services.AddDbContext<OrdersDbContext>(options =>
                    options.UseInMemoryDatabase("GatewayTestOrdersDb")
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
            });
        });

        _client = customFactory.CreateClient();
    }

    [Fact]
    public async Task DiagnosticEdnpoint_MustReturn403_WhenTenantIsSuspended()
    {
        // Arrange
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/diagnostics/tenant"
        );
        request.Headers.Add("X-Tenant-Id", "suspended-store");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Equal("tenant_suspended", body.Error?.Code);
    }

    [Fact]
    public async Task DiagnosticEndpoint_MustReturn404_WhenTenantDoesNotExist()
    {
        // Arrange
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/diagnostics/tenant"
        );
        request.Headers.Add("X-Tenant-Id", "ghost-store");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Equal("tenant_not_found", body.Error?.Code);
    }

    [Fact]
    public async Task CheckoutValidation_ReturnsUnifiedErrorResponse()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/orders/checkout");
        request.Headers.Add("X-Tenant-Id", "alpha-store");
        request.Content = JsonContent.Create(new
        {
            CustomerId = Guid.NewGuid(),
            Items = Array.Empty<object>()
        });

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Equal("validation_failed", body.Error?.Code);
    }

    /// <summary>
    /// Auxiliary model to deserialization and analysis of the JSON Response 
    /// of the Endpoint.
    /// </summary>
    private class TenantDiagnosticResponse
    {
        public Guid TenantId { get; set; }
        public string? Subdomain { get; set; }
        public string? Plan { get; set; }
        public bool IsActive { get; set; }
    }
}