using Microsoft.EntityFrameworkCore;
using Comercio.Shared;
using Orders.Core.Entities;

namespace Orders.Infrastructure.Persistence;

public class OrdersDbContext: DbContext
{
    private readonly ITenantContext _tenantContext;

    public OrdersDbContext(
        DbContextOptions<OrdersDbContext> options, 
        ITenantContext tenantContext
    ) : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Order configuration
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(o => o.OrderId);
            entity.Property(o => o.Status).IsRequired().HasMaxLength(50);
            entity.Property(o => o.TotalPrice).HasPrecision(18, 2);

            // Global logical isolation: Orders queries will inject automatically 
            // the current TenantId
            entity.HasQueryFilter(o => o.TenantId == _tenantContext.TenantId);
            entity.HasIndex(o => o.TenantId);
        });

        // Order Items configuration
        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(oi => oi.OrderItemId);
            entity.Property(oi => oi.UnitPriceAtPurchase).HasPrecision(18, 2);

            entity.HasOne<Order>()
                .WithMany(o => o.Items)
                .HasForeignKey(oi => oi.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var entries = ChangeTracker.Entries<IMultiTenant>();

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                if (_tenantContext.TenantId == null)
                {
                    throw new InvalidOperationException(
                        "It cannot be registered an order without a TenantId perimetrally active"
                    );
                }

                entry.Entity.TenantId = _tenantContext.TenantId.Value;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}