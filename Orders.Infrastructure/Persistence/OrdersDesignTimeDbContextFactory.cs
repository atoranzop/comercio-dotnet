using Comercio.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orders.Infrastructure.Persistence;

namespace Orders.Infrastructure;

public sealed class OrdersDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args)
    {
        DotNetEnv.Env.NoClobber().TraversePath().Load();
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__Postgres");
        var optionsBuilder = new DbContextOptionsBuilder<OrdersDbContext>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            optionsBuilder.UseNpgsql(npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders"));
        }
        else
        {
            optionsBuilder.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders"));
        }

        return new OrdersDbContext(optionsBuilder.Options, new TenantContext());
    }
}
