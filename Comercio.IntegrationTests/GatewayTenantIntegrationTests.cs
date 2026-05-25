using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Comercio.IntegrationTests;

/// <summary>
/// Idustrial integration tests to check the perimetral behavior of 
/// the API Gateway and its middlewares.
/// </summary>
public class GatewayTenantIntegrationTests (
    WebApplicationFactory<Program> factory
)
{
    private readonly HttpClient _client = factory.CreateClient();
    [Fact]
    public async Task DiagnosticEndpoint_MustReturnTenant_WhenValidHeader()
    {
        // Arrange
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/diagnostics/tenant"
        );

        request.Headers.Add("X-Tenant-Id", "alpha-store");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TenantDiagnosticResponse>();
        Assert.NotNull(body);
        Assert.Equal("alpha-store", body.Subdomain);
        Assert.Equal("Premium", body.Plan);
        Assert.True(body.IsActive);
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
    }

    /// <summary>
    /// Auxilliary model to deserialization and analysis of the JSON Response 
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
