using Microsoft.EntityFrameworkCore;

namespace Catalog.Infrastructure.Persistence;

public sealed class TenantDbContext(DbContextOptions<TenantDbContext> options)
    : DbContext(options)
{
    public DbSet<TenantRecord> Tenants => Set<TenantRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("platform");

        modelBuilder.Entity<TenantRecord>(entity =>
        {
            entity.ToTable("Tenants", table =>
                table.HasCheckConstraint(
                    "CK_Tenants_Subdomain_Lowercase",
                    "\"Subdomain\" = lower(\"Subdomain\")"));
            entity.HasKey(tenant => tenant.TenantId);
            entity.Property(tenant => tenant.Subdomain)
                .IsRequired()
                .HasMaxLength(63);
            entity.Property(tenant => tenant.Plan)
                .IsRequired()
                .HasMaxLength(50);
            entity.Property(tenant => tenant.IsActive)
                .IsRequired();
            entity.HasIndex(tenant => tenant.Subdomain)
                .IsUnique();
        });
    }
}
