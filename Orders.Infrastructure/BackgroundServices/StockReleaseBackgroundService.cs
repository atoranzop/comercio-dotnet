using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

using Catalog.Infrastructure.Persistence;
using Orders.Infrastructure.Persistence;
using Comercio.Shared;

namespace Orders.Infrastructure.BackgroundServices;

/// <summary>
/// Background service that searches periodically for expired orders (unpaid)
/// and releases the reserved stock atomically (RF-3.5 / HU-3.5)
/// </summary>
/// <param name="serviceProvider"></param>
/// <param name="logger"></param>
public class StockReleaseBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<StockReleaseBackgroundService> logger) : BackgroundService
{
    // The service runs every 10th seconds in dev to facilitate testing
    private readonly TimeSpan _period = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Processes expired orders repeatedly, waiting ten seconds between passes
    /// until the service is stopped and logging processing failures.
    /// </summary>
    /// <param name="stoppingToken">Signals that the background service should stop.</param>
    /// <returns>A task representing the lifetime of the expiration loop.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[BACKGROUND] Purchase stock expiration timer started.");

        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessExpiredOrderAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BACKGROUND] Unexpected error in stock timer loop");
            }

            await Task.Delay(_period, stoppingToken);
        }
    }

    /// <summary>
    /// Finds orders across all tenants with status "Pending payment" that are at least
    /// fifteen minutes old, restores stock for existing products in each order's catalog
    /// transaction, and then saves the successfully processed orders as "Cancelled".
    /// </summary>
    /// <param name="stoppingToken">The cancellation token passed to database operations.</param>
    /// <returns>A task representing one pass of stock release and order status updates.</returns>
    private async Task ProcessExpiredOrderAsync(CancellationToken stoppingToken)
    {
        // Since it is a BackgroundService Singleton, we must create a scope to safely
        // consume Scoped services as DbContexts and ITenantContext.
        using var scope = serviceProvider.CreateScope();

        var ordersDb = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        // Time limit: Orders created more than 15 mins ago that are still 
        // "Pending payment"
        var expirationTime = DateTime.UtcNow.AddMinutes(-15);

        // Deactivate temporaryly the Global Query Filter to be able to scan all expired
        // orders form all tenants in one background check.
        var expiredOrders = await ordersDb.Orders
            .IgnoreQueryFilters()
            .Where(o => o.Status == "Pending payment" && o.CreatedAt <= expirationTime)
            .Include(o => o.Items)
            .ToListAsync(stoppingToken);

        if (expiredOrders.Count == 0)
        {
            return;
        }

        logger.LogInformation("[BACKGROUND] {Count} expired orders detected. " + 
            "Starting atomic stock release...", expiredOrders.Count);
        
        foreach(var order in expiredOrders)
        {
            // Manually configure the TenantId at auditory injection and isolated change 
            // saving context.
            tenantContext.SetTenant(order.TenantId, string.Empty, string.Empty, true);

            // Start an atomic relational transaction to revert inventory
            using var catalogTransaction = await catalogDb.Database
                .BeginTransactionAsync(stoppingToken);
            
            try
            {
                foreach (var item in order.Items)
                {
                    // Seek the product skipping the filter to be able to find the 
                    // proper tenant product.
                    var product = await catalogDb.Products
                        .IgnoreQueryFilters()
                        .SingleOrDefaultAsync(p => p.ProductId == item.ProductId, 
                        stoppingToken);

                    if (product != null)
                    {
                        logger.LogInformation(
                            "[BACKGROUND] Reversing {Qty} units of SKU '{Sku}' for tenant " +
                            "{TenantId}", item.Quantity, product.SKU, order.TenantId 
                        );

                        // Return temporary stock to public catalog
                        product.Stock += item.Quantity;
                        product.ConcurrencyToken = Guid.NewGuid();
                    }
                }

                // Save the inventory reversion
                await catalogDb.SaveChangesAsync(stoppingToken);
                await catalogTransaction.CommitAsync(stoppingToken);

                // Change the order status to "Cancelled"
                order.Status = "Cancelled";
            }
            catch (Exception ex)
            {
                await catalogTransaction.RollbackAsync(stoppingToken);
                logger.LogError(ex, "[BACKGROUND] Failed to release stock for " + 
                "order {OrderId}", order.OrderId);
                continue;
            }
        }

        // Save the status updates for orders
        await ordersDb.SaveChangesAsync(stoppingToken);
        logger.LogInformation(
            "[BACKGROUND] Process of stock release and expired orders " +
            "invalidation completed."
            );
    }
}