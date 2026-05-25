using Comercio.Shared;
using Xunit;

namespace Comercio.UnitTests;

/// <summary>
/// Contains the white box tests to check the injected state of the TenantContext.
/// </summary>
public class TenantContextTests
{
    [Fact]
    public void SetTenant_MustAsignPropertiesCorrectly()
    {
        // Arrange
        var context = new TenantContext();
        var expectedId = Guid.NewGuid();
        var expectedSubdomain = "test-store";
        var expectedPlan = "Premium";
        var expectedIsActive = true;

        // Act
        context.SetTenant(
            expectedId,
            expectedSubdomain,
            expectedPlan,
            expectedIsActive
        );

        // Assert
        Assert.Equal(expectedId, context.TenantId);
        Assert.Equal(expectedSubdomain, context.Subdomain);
        Assert.Equal(expectedPlan, context.Plan);
        Assert.Equal(expectedIsActive, context.IsActive);
    }
}
