# Comercio

https://img.shields.io/badge/.NET-10.0-purple
https://img.shields.io/badge/Architecture-Monolith_Modular_%2B_API_Gateway-blue
https://img.shields.io/badge/License-Proprietary-red

Comercio is a .NET 10, multi-tenant commerce backend built around an ASP.NET
Core API Gateway. It demonstrates tenant-aware product catalog and order
processing backed by PostgreSQL, with inventory concurrency protection,
resilient payment integration, and asynchronous order-event processing.

The repository contains the API and its supporting domain, persistence, and
test projects. It does not currently include a frontend.

## Capabilities

- Resolve tenants from the `X-Tenant-Id` request header or a host subdomain.
- Persist tenant identity, plan, and active status in PostgreSQL.
- Isolate product and order queries by tenant.
- Create products and check out orders while reserving inventory atomically.
- Detect stale inventory updates and preserve a specific `409 Conflict`
  response for concurrency conflicts.
- Persist order-created events in an outbox in the same transaction as the
  order, then dispatch them asynchronously with retry handling.
- Release inventory for expired unpaid orders using a background worker.
- Apply per-tenant rate limits based on the tenant plan.
- Return a consistent JSON response envelope and handle unexpected API
  exceptions centrally without exposing internal exception details.

## Solution layout

| Project | Responsibility |
| --- | --- |
| `Comercio.Gateway` | ASP.NET Core API, tenant and exception middleware, checkout orchestration, rate limiting, and background-service registration |
| `Comercio.Shared` | Shared tenant abstractions, tenant context, and API response contract |
| `Catalog.Core` / `Catalog.Infrastructure` | Product model and PostgreSQL catalog/tenant persistence |
| `Orders.Core` / `Orders.Infrastructure` | Order and event models, PostgreSQL persistence, transactional outbox, and background processing |
| `Comercio.UnitTests` | Isolated unit tests |
| `Comercio.IntegrationTests` | API, persistence, and PostgreSQL integration tests |

Catalog, order, and tenant data use one PostgreSQL database, separated into
`catalog`, `orders`, and `platform` schemas. Entity Framework Core migrations
are maintained independently for each context.

## API overview

Tenant-scoped endpoints accept a tenant identifier through `X-Tenant-Id`.
Requests with an unregistered tenant return `404`; suspended tenants return
`403`. A host such as `alpha-store.example.test` can also supply the
`alpha-store` subdomain. Requests without a tenant identifier are not assigned
a tenant by the middleware.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/catalog/products` | List products for the current tenant |
| `POST` | `/api/catalog/products` | Create a product for the current tenant |
| `POST` | `/api/orders/checkout` | Reserve stock and create an order |
| `POST` | `/api/orders/{orderId}/confirm-payment` | Attempt payment for an order |
| `POST` | `/api/external/payments/process` | Development payment-provider simulation |

Successful and failed JSON responses use `ApiResponse<T>`:

```json
{
  "success": true,
  "data": {
    "example": "result"
  },
  "error": null
}
```

Errors have `success: false`, `data: null`, and an `error` object containing a
stable `code` and safe client-facing `message`. Optional `details` and `traceId`
fields provide validation context or diagnostics where appropriate. HTTP
semantics remain intact: for example, resource creation returns `201 Created`
with a `Location` header, and concurrency conflicts return `409 Conflict`.

## Prerequisites

- .NET 10 SDK
- PostgreSQL
- The Entity Framework Core CLI (`dotnet-ef` 10.0.8) for creating or applying
  migrations

## Configure and run locally

1. Create a local PostgreSQL database.
2. Copy `.env.example` to `.env` in the repository root and replace the
   username and password placeholders in `ConnectionStrings__Postgres`.
   `.env` is ignored by Git; do not commit credentials. Environment variables
   take precedence over values loaded from `.env`.
3. Apply the migrations using the commands in
   [Database migrations](#database-migrations). The Gateway does not create or
   update schemas automatically.
4. Start the Gateway:

   ```powershell
   dotnet run --project Comercio.Gateway\Comercio.Gateway.csproj
   ```

   The HTTP launch profile listens on `http://localhost:5095`. In the
   `Development` environment, the Gateway idempotently inserts the example
   tenants `alpha-store`, `beta-store`, and `suspended-store`. Apply the tenant
   migration before starting it.

Example catalog request:

```http
GET http://localhost:5095/api/catalog/products
X-Tenant-Id: alpha-store
```

## Database migrations

Install the EF CLI if needed:

```powershell
dotnet tool install --global dotnet-ef --version 10.0.8
```

Apply all three contexts after configuring `ConnectionStrings__Postgres`:

```powershell
dotnet ef database update --project Catalog.Infrastructure\Catalog.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context TenantDbContext
dotnet ef database update --project Catalog.Infrastructure\Catalog.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context CatalogDbContext
dotnet ef database update --project Orders.Infrastructure\Orders.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context OrdersDbContext
```

To add a migration, run `dotnet ef migrations add <MigrationName>` with the
matching `--project`, `--startup-project`, `--context`, and
`--output-dir Persistence\Migrations` arguments shown below:

```powershell
dotnet ef migrations add AddTenantChange --project Catalog.Infrastructure\Catalog.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context TenantDbContext --output-dir Persistence\Migrations
dotnet ef migrations add AddCatalogChange --project Catalog.Infrastructure\Catalog.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context CatalogDbContext --output-dir Persistence\Migrations
dotnet ef migrations add AddOrderChange --project Orders.Infrastructure\Orders.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context OrdersDbContext --output-dir Persistence\Migrations
```

Migrations can be generated without a live database connection. Database
updates require a configured PostgreSQL connection.

## Tests

Run unit and ordinary integration tests:

```powershell
dotnet test Comercio.slnx
```

The PostgreSQL persistence tests validate the real provider, migrations,
tenant lookup and isolation, transaction rollback, optimistic concurrency, and
outbox dispatch. They require a PostgreSQL database and
`ConnectionStrings__Postgres`. Enable them for the current PowerShell session
with:

```powershell
$env:COMERCIO_POSTGRES_TESTS = "1"
dotnet test Comercio.slnx
Remove-Item Env:COMERCIO_POSTGRES_TESTS
```

These tests insert uniquely identified rows and clean them up after execution.
The CI workflow provisions PostgreSQL, applies migrations, and runs the tests
with the PostgreSQL test flag enabled.

## License

This project is licensed under the GNU General Public License, version 3
(GPL-3.0-only). See [`LICENSE`](./LICENSE) for the complete license text.
