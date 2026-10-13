using Catalog.Core.Entities;
using Catalog.Infrastructure.Persistence;
using Comercio.Gateway;
using Comercio.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orders.Core.Events;
using Npgsql;
using Orders.Core.Entities;
using Orders.Infrastructure.Outbox;
using Orders.Infrastructure.Persistence;
using Xunit;

namespace Comercio.IntegrationTests;

public sealed class PostgresPersistenceTests
{
    private readonly string _connectionString;

    public PostgresPersistenceTests()
    {
        DotNetEnv.Env.NoClobber().TraversePath().Load();
        _connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings__Postgres is required to run PostgreSQL persistence tests.");
    }

    [PostgresFact]
    public async Task Tenant_store_uses_applied_postgres_migration_and_resolves_tenant_states()
    {
        var activeTenantId = Guid.NewGuid();
        var suspendedTenantId = Guid.NewGuid();
        var subdomainSuffix = Guid.NewGuid().ToString("N");
        var activeSubdomain = $"active-{subdomainSuffix}";
        var suspendedSubdomain = $"suspended-{subdomainSuffix}";

        await using var db = CreateTenantDbContext();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);

        await db.Database.MigrateAsync();
        var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(
            appliedMigrations,
            migration => migration.Contains("InitialTenantStore", StringComparison.Ordinal));

        try
        {
            db.Tenants.AddRange(
                new TenantRecord
                {
                    TenantId = activeTenantId,
                    Subdomain = activeSubdomain,
                    Plan = "Premium",
                    IsActive = true
                },
                new TenantRecord
                {
                    TenantId = suspendedTenantId,
                    Subdomain = suspendedSubdomain,
                    Plan = "Basic",
                    IsActive = false
                });
            await db.SaveChangesAsync();

            var store = new PostgresTenantStore(db);
            var activeTenant = await store.FindBySubdomainAsync(
                activeSubdomain.ToUpperInvariant());
            var suspendedTenant = await store.FindBySubdomainAsync(suspendedSubdomain);
            var missingTenant = await store.FindBySubdomainAsync(
                $"missing-{subdomainSuffix}");

            Assert.NotNull(activeTenant);
            Assert.Equal(activeTenantId, activeTenant.TenantId);
            Assert.Equal("Premium", activeTenant.Plan);
            Assert.True(activeTenant.IsActive);

            Assert.NotNull(suspendedTenant);
            Assert.Equal(suspendedTenantId, suspendedTenant.TenantId);
            Assert.Equal("Basic", suspendedTenant.Plan);
            Assert.False(suspendedTenant.IsActive);
            Assert.Null(missingTenant);

            var middleware = new TenantMiddleware(_ => Task.CompletedTask, store);

            var activeContext = CreateTenantRequest(activeSubdomain);
            var activeTenantContext = new TenantContext();
            await middleware.InvokeAsync(activeContext, activeTenantContext);
            Assert.Equal(StatusCodes.Status200OK, activeContext.Response.StatusCode);
            Assert.Equal(activeTenantId, activeTenantContext.TenantId);

            var suspendedContext = CreateTenantRequest(suspendedSubdomain);
            await middleware.InvokeAsync(suspendedContext, new TenantContext());
            Assert.Equal(StatusCodes.Status403Forbidden, suspendedContext.Response.StatusCode);
            Assert.Equal(
                "tenant_suspended",
                await ReadErrorCodeAsync(suspendedContext));

            var missingContext = CreateTenantRequest($"missing-{subdomainSuffix}");
            await middleware.InvokeAsync(missingContext, new TenantContext());
            Assert.Equal(StatusCodes.Status404NotFound, missingContext.Response.StatusCode);
            Assert.Equal(
                "tenant_not_found",
                await ReadErrorCodeAsync(missingContext));
        }
        finally
        {
            await db.Tenants
                .Where(tenant => tenant.TenantId == activeTenantId
                    || tenant.TenantId == suspendedTenantId)
                .ExecuteDeleteAsync();
        }
    }

    [PostgresFact]
    public async Task Catalog_and_orders_queries_are_isolated_by_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var productAId = Guid.NewGuid();
        var productBId = Guid.NewGuid();
        var orderAId = Guid.NewGuid();
        var orderBId = Guid.NewGuid();

        try
        {
            await using (var db = CreateCatalogContext(tenantA))
            {
                db.Products.Add(CreateProduct(productAId));
                await db.SaveChangesAsync();
            }

            await using (var db = CreateCatalogContext(tenantB))
            {
                db.Products.Add(CreateProduct(productBId));
                await db.SaveChangesAsync();
            }

            await using (var db = CreateOrdersContext(tenantA))
            {
                db.Orders.Add(CreateOrder(orderAId));
                await db.SaveChangesAsync();
            }

            await using (var db = CreateOrdersContext(tenantB))
            {
                db.Orders.Add(CreateOrder(orderBId));
                await db.SaveChangesAsync();
            }

            await using (var db = CreateCatalogContext(tenantA))
            {
                var productIds = await db.Products.Select(product => product.ProductId)
                    .ToListAsync();
                Assert.Equal([productAId], productIds);
            }

            await using (var db = CreateCatalogContext(tenantB))
            {
                var productIds = await db.Products.Select(product => product.ProductId)
                    .ToListAsync();
                Assert.Equal([productBId], productIds);
            }

            await using (var db = CreateOrdersContext(tenantA))
            {
                var orderIds = await db.Orders.Select(order => order.OrderId)
                    .ToListAsync();
                Assert.Equal([orderAId], orderIds);
            }

            await using (var db = CreateOrdersContext(tenantB))
            {
                var orderIds = await db.Orders.Select(order => order.OrderId)
                    .ToListAsync();
                Assert.Equal([orderBId], orderIds);
            }
        }
        finally
        {
            await DeleteProductsAsync(productAId, productBId);
            await DeleteOrdersAsync(orderAId, orderBId);
        }
    }

    [PostgresFact]
    public async Task Catalog_updates_reject_a_stale_concurrency_token()
    {
        var tenantId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var winningToken = Guid.NewGuid();

        try
        {
            await using (var db = CreateCatalogContext(tenantId))
            {
                db.Products.Add(CreateProduct(productId));
                await db.SaveChangesAsync();
            }

            await using var firstContext = CreateCatalogContext(tenantId);
            await using var secondContext = CreateCatalogContext(tenantId);
            var firstProduct = await firstContext.Products.SingleAsync(
                product => product.ProductId == productId);
            var secondProduct = await secondContext.Products.SingleAsync(
                product => product.ProductId == productId);

            firstProduct.Stock--;
            firstProduct.ConcurrencyToken = winningToken;
            secondProduct.Stock--;
            secondProduct.ConcurrencyToken = Guid.NewGuid();

            await firstContext.SaveChangesAsync();

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => secondContext.SaveChangesAsync());

            await using var verificationContext = CreateCatalogContext(tenantId);
            var persistedProduct = await verificationContext.Products.SingleAsync(
                product => product.ProductId == productId);
            Assert.Equal(9, persistedProduct.Stock);
            Assert.Equal(winningToken, persistedProduct.ConcurrencyToken);
        }
        finally
        {
            await DeleteProductsAsync(productId);
        }
    }

    [PostgresFact]
    public async Task Checkout_rolls_back_stock_and_order_when_outbox_save_fails()
    {
        var tenantId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var checkoutOrderId = Guid.NewGuid();
        var outboxMessageId = Guid.NewGuid();
        var initialToken = Guid.NewGuid();

        try
        {
            await using (var catalogDb = CreateCatalogContext(tenantId))
            {
                var product = CreateProduct(productId);
                product.ConcurrencyToken = initialToken;
                catalogDb.Products.Add(product);
                await catalogDb.SaveChangesAsync();
            }

            await using (var ordersDb = CreateOrdersContext(tenantId))
            {
                var existingMessage = OutboxMessage.Create(new OrderCreatedEvent(
                    Guid.NewGuid(),
                    tenantId,
                    1m,
                    Guid.NewGuid()));
                existingMessage.Id = outboxMessageId;
                ordersDb.OutboxMessages.Add(existingMessage);
                await ordersDb.SaveChangesAsync();
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            await using var catalogContext = CreateCatalogContext(tenantId, connection);
            await using var ordersContext = CreateOrdersContext(tenantId, connection);
            await using var transaction =
                await catalogContext.Database.BeginTransactionAsync();
            await ordersContext.Database.UseTransactionAsync(
                transaction.GetDbTransaction());

            var trackedProduct = await catalogContext.Products.SingleAsync(
                product => product.ProductId == productId);
            trackedProduct.Stock--;
            trackedProduct.ConcurrencyToken = Guid.NewGuid();
            await catalogContext.SaveChangesAsync();

            ordersContext.Orders.Add(CreateOrder(checkoutOrderId));
            await ordersContext.SaveChangesAsync();

            var outboxMessage = OutboxMessage.Create(new OrderCreatedEvent(
                checkoutOrderId,
                tenantId,
                1m,
                Guid.NewGuid()));
            outboxMessage.Id = outboxMessageId;
            ordersContext.OutboxMessages.Add(outboxMessage);
            await Assert.ThrowsAsync<DbUpdateException>(
                () => ordersContext.SaveChangesAsync());

            await transaction.RollbackAsync();

            await using var verifyCatalog = CreateCatalogContext(tenantId);
            var persistedProduct = await verifyCatalog.Products.SingleAsync(
                product => product.ProductId == productId);
            Assert.Equal(10, persistedProduct.Stock);
            Assert.Equal(initialToken, persistedProduct.ConcurrencyToken);

            await using var verifyOrders = CreateOrdersContext(tenantId);
            Assert.Equal(0, await verifyOrders.Orders.CountAsync(
                order => order.OrderId == checkoutOrderId));
            Assert.Equal(1, await verifyOrders.OutboxMessages.CountAsync(
                message => message.Id == outboxMessageId));
        }
        finally
        {
            await DeleteProductsAsync(productId);
            await DeleteOrdersAsync(checkoutOrderId);
            await DeleteOutboxMessagesAsync(outboxMessageId);
        }
    }

    [PostgresFact]
    public async Task Outbox_dispatch_marks_message_processed_after_publishing()
    {
        var tenantId = Guid.NewGuid();
        var notification = new OrderCreatedEvent(
            Guid.NewGuid(),
            tenantId,
            15m,
            Guid.NewGuid());
        var message = OutboxMessage.Create(notification);

        try
        {
            await using (var insertContext = CreateOrdersContext(tenantId))
            {
                insertContext.OutboxMessages.Add(message);
                await insertContext.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMediatR(configuration =>
                configuration.RegisterServicesFromAssembly(
                    typeof(OrderCreatedEvent).Assembly));
            using var provider = services.BuildServiceProvider();
            var mediator = provider.GetRequiredService<MediatR.IMediator>();
            var logger = provider.GetRequiredService<ILogger<OutboxDispatcher>>();

            await using var dispatchContext = CreateOrdersContext(tenantId);
            var dispatcher = new OutboxDispatcher(dispatchContext, mediator, logger);
            Assert.True(await dispatcher.DispatchNextAsync(CancellationToken.None));

            await using var verifyContext = CreateOrdersContext(tenantId);
            var processedMessage = await verifyContext.OutboxMessages
                .SingleAsync(storedMessage => storedMessage.Id == message.Id);
            Assert.NotNull(processedMessage.ProcessedAtUtc);
            Assert.Equal(0, processedMessage.Attempts);
            Assert.Null(processedMessage.LastError);
        }
        finally
        {
            await DeleteOutboxMessagesAsync(message.Id);
        }
    }

    private CatalogDbContext CreateCatalogContext(
        Guid tenantId,
        NpgsqlConnection? connection = null)
    {
        var tenantContext = CreateTenantContext(tenantId);
        var optionsBuilder = new DbContextOptionsBuilder<CatalogDbContext>();
        if (connection is null)
        {
            optionsBuilder.UseNpgsql(_connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "catalog"));
        }
        else
        {
            optionsBuilder.UseNpgsql(connection, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "catalog"));
        }

        var options = optionsBuilder.Options;
        return new CatalogDbContext(options, tenantContext);
    }

    private OrdersDbContext CreateOrdersContext(
        Guid tenantId,
        NpgsqlConnection? connection = null)
    {
        var tenantContext = CreateTenantContext(tenantId);
        var optionsBuilder = new DbContextOptionsBuilder<OrdersDbContext>();
        if (connection is null)
        {
            optionsBuilder.UseNpgsql(_connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders"));
        }
        else
        {
            optionsBuilder.UseNpgsql(connection, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders"));
        }

        var options = optionsBuilder.Options;
        return new OrdersDbContext(options, tenantContext);
    }

    private TenantDbContext CreateTenantDbContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "platform"))
            .Options;
        return new TenantDbContext(options);
    }

    private static DefaultHttpContext CreateTenantRequest(string subdomain)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = subdomain;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var response = await System.Text.Json.JsonSerializer.DeserializeAsync<
            ApiResponse<object>>(
                context.Response.Body,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));
        return response?.Error?.Code;
    }

    private static TenantContext CreateTenantContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId, "postgres-test", "Premium", true);
        return tenantContext;
    }

    private static Product CreateProduct(Guid productId) =>
        new()
        {
            ProductId = productId,
            SKU = $"PG-{productId:N}",
            Name = "PostgreSQL tenant isolation test",
            Price = 1m,
            Stock = 10,
            ConcurrencyToken = Guid.NewGuid()
        };

    private static Order CreateOrder(Guid orderId) =>
        new()
        {
            OrderId = orderId,
            CustomerId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            Status = "PostgreSQL test",
            TotalPrice = 1m
        };

    private async Task DeleteProductsAsync(params Guid[] productIds)
    {
        await using var db = CreateCatalogContext(Guid.Empty);
        foreach (var productId in productIds)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM catalog.\"Products\" WHERE \"ProductId\" = {productId}");
        }
    }

    private async Task DeleteOrdersAsync(params Guid[] orderIds)
    {
        await using var db = CreateOrdersContext(Guid.Empty);
        foreach (var orderId in orderIds)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM orders.\"Orders\" WHERE \"OrderId\" = {orderId}");
        }
    }

    private async Task DeleteOutboxMessagesAsync(params Guid[] messageIds)
    {
        await using var db = CreateOrdersContext(Guid.Empty);
        foreach (var messageId in messageIds)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM orders.\"OutboxMessages\" WHERE \"Id\" = {messageId}");
        }
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("COMERCIO_POSTGRES_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set COMERCIO_POSTGRES_TESTS=1 to run tests against local PostgreSQL.";
        }
    }
}
