using Catalog.Infrastructure.Persistence;
using Comercio.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Catalog.Infrastructure;

public sealed class CatalogDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        DotNetEnv.Env.NoClobber().TraversePath().Load();
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__Postgres");
        var optionsBuilder = new DbContextOptionsBuilder<CatalogDbContext>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            optionsBuilder.UseNpgsql(npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "catalog"));
        }
        else
        {
            optionsBuilder.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "catalog"));
        }

        return new CatalogDbContext(optionsBuilder.Options, new TenantContext());
    }
}
