namespace Catalog.Infrastructure.Persistence;

public sealed class TenantRecord
{
    public Guid TenantId { get; set; }
    public string Subdomain { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
