# Jellyfin 12.1 / .NET 10

## Compatibilidad

Esta rama produce el proveedor **3.0.0.0**, exclusivo para Jellyfin **12.1.x**.
Requiere .NET SDK 10 para compilar, PostgreSQL **16 o posterior** y herramientas
`pg_dump` / `psql` compatibles con el servidor para los respaldos de actualización.
La limpieza JSON de Jellyfin 12.1 se adapta al predicado `IS JSON` de PostgreSQL 16+.

| Dependencia | Versión |
| --- | --- |
| Jellyfin.Controller / Common / Database.Implementations | 12.1.0 |
| EF Core / Microsoft.Data.Sqlite | 10.0.11 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.2 |
| Framework / ABI | net10.0 / 12.1.0.0 |

No instalar este paquete sobre Jellyfin 10.11. El catálogo conserva los releases
anteriores y sus checksums; el workflow agrega el nuevo release con ABI 12.1.0.0.

## Actualizar

1. Conservar un respaldo completo de los datos y configuración de Jellyfin y un
   `pg_dump` de PostgreSQL antes de actualizar el servidor/proveedor.
2. Sustituir las tres DLL del plugin juntas: `Jellyfin.Database.Providers.Postgres.dll`,
   `Npgsql.dll` y `Npgsql.EntityFrameworkCore.PostgreSQL.dll`. No copiar DLL de
   Jellyfin, EF Core o ASP.NET desde la salida de compilación al directorio de plugins.
3. Para una base PostgreSQL existente, Jellyfin aplica la migración incremental
   `Jellyfin121` al arrancar. Los respaldos solicitados por las migraciones de código
   se guardan en `{DataPath}/postgres-migration-backups`. Configurar `PgBinPath` o
   incluir las herramientas PostgreSQL en PATH antes del arranque.
4. Para importar SQLite, actualizar y arrancar primero esa base con Jellyfin 12.1.
   El importador rechaza tablas/columnas antiguas antes de escribir en PostgreSQL.
   No sustituye las migraciones de datos que debe ejecutar el servidor.
5. Revisar inicio de sesión, bibliotecas, listas de reproducción, progreso de
   reproducción y logs después del primer arranque real.

La migración conserva el identificador InitialCreate existente, convierte OwnerId y
PrimaryVersionId a UUID, incorpora LinkedChildren y los índices actuales, elimina
permisos/preferencias huérfanos y consolida ItemValues duplicados preservando sus
asociaciones. No confunde ExtraIds con OriginalLanguage. Las importaciones y
exportaciones copian únicamente migraciones de código realmente aplicadas; las
pendientes siguen pendientes.

La opción de truncado sigue reemplazando los datos de destino. Todas las tablas se
truncan antes de copiar la primera fila para evitar pérdidas por cascadas posteriores.
Una importación con errores de tabla se reporta como fallida.

Para volver de versión, restaurar el respaldo completo correspondiente. La migración
inversa de esquema no puede recuperar columnas eliminadas ni listas duplicadas que
la versión anterior no representaba.

## Compilar y comprobar

```powershell
dotnet build Jellyfin.Database.Providers.Postgres.sln -c Release
dotnet run --project tests/CompatibilityChecks -c Release
```

Las comprobaciones sin variables de entorno no abren conexiones PostgreSQL.
Para integración, proporcionar una instancia **exclusiva de pruebas** con permiso
para crear bases; el runner crea bases nuevas con prefijo `jellyfin_provider_test_`:

```powershell
$env:POSTGRES_TEST_CONNECTION = 'Host=localhost;Port=55439;Database=postgres;Username=postgres'
$env:POSTGRES_TEST_BACKUPS = '1' # Requiere pg_dump y psql en PATH
dotnet run --project tests/CompatibilityChecks -c Release
```

Las pruebas cubren esquema inicial, actualización con datos, UUID, duplicados y
asociaciones, permisos huérfanos, limpieza JSON síncrona/asíncrona, JSON malformado,
importación/reimportación en public y schema personalizado, exportación SQLite,
historial aplicado, rechazo de SQLite antiguo y callbacks de respaldo/restauración.
Los fixtures PostgreSQL y SQLite se conservan para diagnóstico. No usar una
instancia de producción para estas pruebas.

`Resources/schema.sql` es una referencia generada; el importador utiliza las
migraciones EF, no divide ni ejecuta manualmente ese archivo.

Para regenerar SQL/migraciones con dotnet-ef 10.0.11, incluir las dependencias del
servidor solo durante el diseño:

```powershell
dotnet build Jellyfin.Database.Providers.Postgres -c Release -p:IncludeJellyfinRuntime=true
dotnet ef migrations script --project Jellyfin.Database.Providers.Postgres --configuration Release --no-build -o Jellyfin.Database.Providers.Postgres/Resources/schema.sql
```

Volver a compilar/publicar sin `IncludeJellyfinRuntime` para distribuir el plugin.
