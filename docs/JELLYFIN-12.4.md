# Jellyfin 12.4

## Compatibilidad

El proveedor **3.2.0.0** se compila contra Jellyfin **12.4.0** (ABI `12.4.0.0`). Para
Jellyfin 12.1.x sigue usando **3.1.0.0**; el catálogo ofrece a cada servidor el paquete de su ABI.

| Dependencia | Versión |
| --- | --- |
| Jellyfin.Controller / Common / Database.Implementations | 12.4.0 |
| EF Core / Microsoft.Data.Sqlite | 10.0.14 (la versión que trae el servidor 12.4) |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.2 |
| Framework / ABI | net10.0 / 12.4.0.0 |

## Qué cambia entre 12.1 y 12.4 para el proveedor

| Cambio en Jellyfin | Efecto en PostgreSQL |
| --- | --- |
| `BaseItemConfiguration`: los índices `IX_BaseItems_OwnerId` e `IX_BaseItems_ExtraType_OwnerId` pasan a ser parciales (`MakeOwnerIdIndexesPartial`, solo SQLite en el núcleo) | Nueva migración EF **`Jellyfin124`**: elimina y recrea ambos índices con `WHERE ... IS NOT NULL`. Sin ella el modelo tiene cambios pendientes y los índices cubren todas las filas aunque casi ninguna tenga `OwnerId`. |
| `IJellyfinDatabaseProvider.RefreshStatistics` (miembro nuevo con implementación vacía por defecto), invocado tras cada escaneo de biblioteca | Implementado con `ANALYZE` sobre las tablas del modelo de Jellyfin. No recupera espacio (eso sigue en la optimización programada), por lo que es barato. |
| Migraciones de código `HarmonizeConflictingUserData`, `FixNullEncoderPreset` y `MigrateRatingLevels` (renombrada, se vuelve a ejecutar) | Funcionan sin cambios: usan EF/LINQ o XML. Las dos marcadas con `JellyfinMigrationBackup` disparan `MigrationBackupFast`, es decir `pg_dump`: configurar `PgBinPath` o tener las herramientas en PATH. Las reescrituras de `MigrateRatingLevels` del interceptor de 12.1 siguen aplicándose. |
| `EnsureExistingDatabaseAsync`: el servidor se niega a arrancar si la base no existe o no tiene historial | No afecta a una base PostgreSQL ya activa. Activar PostgreSQL sobre una base vacía sigue sin ser válido: importar primero. |

## Actualizar desde 12.1

1. `pg_dump` de la base y copia de la configuración de Jellyfin.
2. Actualizar el servidor a 12.4 y sustituir las tres DLL del plugin juntas por las de 3.2.0.0.
3. Al primer arranque Jellyfin hace el respaldo `pg_dump` en
   `{DataPath}/postgres-migration-backups`, aplica `Jellyfin124` y las tres migraciones de código.
   En una base de ~110 000 elementos el arranque completo tarda unos segundos.

Para volver a 12.1 hay que restaurar el respaldo: las migraciones de código de 12.4 modifican datos.

## Comprobado

- Suite completa contra PostgreSQL 17 y 18 (incluye pruebas nuevas para los índices parciales,
  `RefreshStatistics`, el ABI del catálogo y los miembros por defecto de la interfaz).
- Jellyfin 12.4.0 (imagen oficial `12.4.20261026-211555`) sobre una copia de una base de producción
  12.1 (108 862 elementos, 4 usuarios), comparada con 12.1 + 3.1.0 sobre otra copia de la misma base:
  arranque sin errores, migraciones aplicadas, índices válidos, `UserData` idéntica, mismos recuentos
  por tipo y mismos favoritos y progreso por usuario. `MigrateRatingLevels` deja sin valor los
  elementos sin clasificación (comportamiento del núcleo, igual en SQLite), lo que puede cambiar
  ligeramente qué ven los usuarios con control parental hasta el siguiente escaneo. Tras un escaneo
  completo, `RefreshDatabaseStatisticsPostScanTask` ejecuta el `ANALYZE` del proveedor.
