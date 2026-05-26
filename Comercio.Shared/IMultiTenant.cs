namespace Comercio.Shared;

/// <summary>
/// Mandatory contract that must implement every entity that require logical
/// isolation by tenant.
/// </summary>
public interface IMultiTenant
{
    /// <summary>
    /// Tenant (owner) identifier of the entity
    /// </summary>
    Guid TenantId { get; set; }
}