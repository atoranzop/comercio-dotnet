using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Catalog.Infrastructure.Persistence;

public sealed class TenantDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        DotNetEnv.Env.NoClobber().TraversePath().Load();
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__Postgres");
        var optionsBuilder = new DbContextOptionsBuilder<TenantDbContext>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            optionsBuilder.UseNpgsql(npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "platform"));
        }
        else
        {
            optionsBuilder.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "platform"));
        }

        return new TenantDbContext(optionsBuilder.Options);
    }
}
