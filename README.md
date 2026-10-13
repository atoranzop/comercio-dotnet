# Comercio - B2B Multi-Tenant E-Commerce Engine

## Configuración de PostgreSQL

En desarrollo local, edita `.env` en la raíz del repositorio y completa la
cadena de conexión. `.env` está excluido de Git. La aplicación y las
herramientas de migración buscan ese archivo desde el directorio actual y sus
directorios padre. Las variables ya definidas en el entorno tienen prioridad;
en despliegue configura la conexión mediante el mecanismo de secretos o
variables del entorno de destino.

La aplicación usa una única base de datos PostgreSQL para los almacenes de
inquilinos y los contextos de catálogo y pedidos:

- `ConnectionStrings__Postgres`: cadena de conexión Npgsql a esa base de datos.
- `TenantDbContext`: usa el esquema `platform` y persiste los inquilinos.
- `CatalogDbContext`: usa el esquema `catalog`.
- `OrdersDbContext`: usa el esquema `orders`.

En despliegue, configura `ConnectionStrings__Postgres` mediante el mecanismo de
secretos o variables del entorno de destino; no despliegues `.env` ni guardes
credenciales en `appsettings*.json` o en el repositorio.

## Contrato de respuestas API

Las respuestas JSON del Gateway usan `ApiResponse<T>`:

- `success`: indica si la operación terminó correctamente.
- `data`: contiene el resultado cuando la operación tiene éxito; en caso de
  error es `null`.
- `error`: en los errores contiene `code`, un mensaje seguro para el cliente
  y, cuando aplica, `details` o `traceId`; en caso de éxito es `null`.

Los códigos HTTP mantienen su semántica. Las respuestas de creación conservan
`201 Created` y el encabezado `Location`; validaciones, recursos inexistentes,
conflictos, fallos inesperados y rechazos por límite de solicitudes llevan el
mismo envoltorio JSON.

## Migraciones de Entity Framework Core

El Gateway no crea ni actualiza el esquema durante el arranque. Las migraciones
de catálogo y pedidos se administran por separado y se aplican explícitamente.

Las migraciones iniciales ya están incluidas en el repositorio. Cada contexto
mantiene su historial de migraciones dentro de su propio esquema. Cuando cambie
el modelo, añade migraciones futuras desde la raíz del repositorio (sustituye
el nombre de ejemplo por uno descriptivo para cada cambio):

```powershell
dotnet ef migrations add NombreCambioInquilinos --project Catalog.Infrastructure\Catalog.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context TenantDbContext --output-dir Persistence\Migrations
dotnet ef migrations add NombreCambioCatalogo --project Catalog.Infrastructure\Catalog.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context CatalogDbContext --output-dir Persistence\Migrations
dotnet ef migrations add NombreCambioPedidos --project Orders.Infrastructure\Orders.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context OrdersDbContext --output-dir Persistence\Migrations
```

Las migraciones se pueden generar sin conectarse a PostgreSQL. Para aplicarlas,
configura `ConnectionStrings__Postgres` en `.env` o en el entorno y ejecuta:

```powershell
dotnet ef database update --project Catalog.Infrastructure\Catalog.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context TenantDbContext
dotnet ef database update --project Catalog.Infrastructure\Catalog.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context CatalogDbContext
dotnet ef database update --project Orders.Infrastructure\Orders.Infrastructure.csproj --startup-project Comercio.Gateway\Comercio.Gateway.csproj --context OrdersDbContext
```

La aplicación de las migraciones es un paso operativo separado del arranque
del Gateway. En el entorno `Development`, el Gateway inserta de forma
idempotente `alpha-store`, `beta-store` y `suspended-store` en la tabla
`platform."Tenants"`; aplica primero la migración de `TenantDbContext`. En
otros entornos no se crean estos datos de ejemplo.

## Outbox de eventos de pedidos

El checkout guarda el pedido y su evento `OrderCreatedEvent` en
`orders."OutboxMessages"` dentro de la misma transacción que el descuento de
stock. El worker publica los eventos pendientes mediante MediatR y marca cada
mensaje como procesado; ante errores, conserva el mensaje y programa reintentos
con espera exponencial. La entrega es **at-least-once**: un fallo del proceso
después de ejecutar un handler pero antes de registrar el mensaje como procesado
puede provocar una nueva entrega. Los handlers que produzcan efectos externos
deben ser idempotentes.

Tras actualizar el código, aplica la nueva migración de Pedidos con el comando
`dotnet ef database update` indicado arriba antes de arrancar el Gateway.

## Verificación contra PostgreSQL local

Las pruebas dirigidas a PostgreSQL verifican el aislamiento por tenant en
Catálogo y Pedidos, el rechazo de actualizaciones concurrentes obsoletas y que
el stock se revierte si falla la escritura del pedido dentro de la transacción
compartida, así como el rollback cuando falla la escritura del outbox y su
despacho. Insertan filas con identificadores aleatorios y las eliminan al
terminar. La CI aplica las migraciones y ejecuta estas pruebas contra
PostgreSQL; localmente no se ejecutan en la suite ordinaria. Para habilitarlas
en PowerShell:

```powershell
$env:COMERCIO_POSTGRES_TESTS = "1"
dotnet test Comercio.IntegrationTests\Comercio.IntegrationTests.csproj --filter FullyQualifiedName~PostgresPersistenceTests
Remove-Item Env:COMERCIO_POSTGRES_TESTS
```

La prueba carga `ConnectionStrings__Postgres` desde `.env` si no está definida
ya en el entorno.
