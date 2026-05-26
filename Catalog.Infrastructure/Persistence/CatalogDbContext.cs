using Microsoft.EntityFrameworkCore;
using Comercio.Shared;
using Catalog.Core.Entities;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// Entity Framework Core context for catalog management.
/// It incorporates transparent rules for multi-tenant isolation at
/// a database level
/// </summary>
public class CatalogDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public CatalogDbContext(
        DbContextOptions<CatalogDbContext> options,
        ITenantContext tenantContext
    ) : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Mapping config for Product entity
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(p => p.ProductId);
            entity.Property(p => p.SKU).IsRequired().HasMaxLength(50);
            entity.Property(p => p.Name).IsRequired().HasMaxLength(150);
            entity.Property(p => p.Price).HasPrecision(18, 2);

            // Global Query Filter (RNF 1 / HU 3.1):
            // Every SELECT query injected by EF Core will have implicitly
            // a filter WHERE TenantId == _tenantContext.TenantId 
            entity.HasQueryFilter(p => p.TenantId == _tenantContext.TenantId);

            // Composed index
            // Optimizes the SQL queries that use the tenant filter
            entity.HasIndex(p => p.TenantId);
        });
    }

    /// <summary>
    /// Overrides the persistence to inject automatically the TenantId
    /// of the active request.
    /// </summary>
    /// <param name="cancellationToken">
    /// A CancellationToken to observe while waiting for the task to complete.
    /// Saves all changes made in this context to the database.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous save operation. 
    /// The task result contains the number of state entries written to the database.
    /// </returns>
    /// <exception cref="InvalidOperationException"></exception>
    public override Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default
    )
    {
        var entries = ChangeTracker.Entries<IMultiTenant>();

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                // Strict validation of perimetral security
                if (_tenantContext.TenantId == null)
                {
                    throw new InvalidOperationException(
                        "A multi-tenant entity cannot be registered without an active " +
                        "tenant context on the pipeline"
                    );
                }

                // Secure injection of the current tenant ID
                entry.Entity.TenantId = _tenantContext.TenantId.Value;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
