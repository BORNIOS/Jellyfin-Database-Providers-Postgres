# Jellyfin 12.2, 12.3 y 12.4

## Compatibilidad

El proveedor **3.2.0.0** sirve para **todas las versiones de Jellyfin 12, de 12.1 a 12.4**. Se compila contra
Jellyfin **12.1.0** (ABI `12.1.0.0`), la versión más antigua que admite, y funciona en las posteriores:
- los cambios del modelo de 12.2 los aplica la [comprobación de deriva del esquema](ACTUALIZACIONES.md) al arrancar;
- el miembro nuevo de la interfaz, `RefreshStatistics`, está implementado de forma que el servidor 12.2+ lo usa.

Así nadie está obligado a actualizar Jellyfin para usar el proveedor, y quien vuelve a una versión anterior de
Jellyfin no se queda con un servidor que no arranca.

| Dependencia | Versión |
| --- | --- |
| Jellyfin.Controller / Common / Database.Implementations | 12.1.0 |
| EF Core / Microsoft.Data.Sqlite | 10.0.11 (la versión que trae el servidor 12.1) |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.2 (funciona con la EF Core de 12.1 a 12.4: 10.0.11 a 10.0.14) |
| Framework / ABI | net10.0 / 12.1.0.0 |

> **Por qué no un `targetAbi` más alto.** `database.xml` carga la DLL del proveedor directamente, sin la
> comprobación de ABI que Jellyfin hace con los plugins. Un paquete compilado contra 12.4 en un servidor 12.1 no
> se desactiva: Jellyfin no arranca (`Could not load file or assembly 'Jellyfin.Database.Implementations,
> Version=12.4.0.0'`). La prueba `CatalogAbiMatchesTheReferencedSdk` mantiene `targetAbi` igual a la versión
> contra la que se compila.

## Qué cambia desde 12.1 para el proveedor

Los dos cambios llegan en **12.2**; 12.3 y 12.4 no cambian nada más que afecte al proveedor.

| Cambio en Jellyfin | Efecto en PostgreSQL |
| --- | --- |
| `BaseItemConfiguration`: los índices `IX_BaseItems_OwnerId` e `IX_BaseItems_ExtraType_OwnerId` pasan a ser parciales (`MakeOwnerIdIndexesPartial`, solo SQLite en el núcleo) | La comprobación de deriva del esquema los recrea con `WHERE ... IS NOT NULL` (con `CONCURRENTLY`) en el primer arranque con 12.2+. No hay migración EF: en 12.1 el modelo no los espera parciales. |
| `IJellyfinDatabaseProvider.RefreshStatistics` (miembro nuevo con implementación vacía por defecto), invocado tras cada escaneo de biblioteca | Implementado con `ANALYZE` sobre las tablas del modelo de Jellyfin. Como el paquete se compila contra 12.1, donde el miembro no existe, es un método `public virtual` y la clase no es `sealed`: así el runtime de 12.2+ lo asocia al miembro de la interfaz. En 12.1 nadie lo llama. |
| Migraciones de código de 12.4 `HarmonizeConflictingUserData`, `FixNullEncoderPreset` y `MigrateRatingLevels` (renombrada, se vuelve a ejecutar) | Funcionan sin cambios: usan EF/LINQ o XML. Las dos marcadas con `JellyfinMigrationBackup` disparan `MigrationBackupFast`, es decir `pg_dump`: configurar `PgBinPath` o tener las herramientas en PATH. |
| `EnsureExistingDatabaseAsync`: el servidor se niega a arrancar si la base no existe o no tiene historial | No afecta a una base PostgreSQL ya activa. Activar PostgreSQL sobre una base vacía sigue sin ser válido: importar primero. |

La importación desde SQLite (`MigrationSchemaPreparer`) aplica las migraciones del proveedor con
`Database.Migrate()`, que EF Core rechaza si el modelo en ejecución tiene cambios que ninguna migración cubre: es
la situación normal del proveedor en 12.2+. Ese aviso (`PendingModelChangesWarning`) se ignora solo ahí; la
comprobación de deriva completa el esquema en el arranque siguiente a la activación.

## Actualizar

1. `pg_dump` de la base y copia de la configuración de Jellyfin.
2. Sustituir las tres DLL del plugin juntas por las de 3.2.0.0. Se puede hacer antes o después de actualizar
   Jellyfin: el mismo paquete funciona en las dos versiones.
3. Al primer arranque con 12.2 o posterior, la comprobación de deriva del esquema deja los dos índices parciales
   (`Schema drift check (Healed)` en el log; en los arranques siguientes, `AlreadyHealed`). Con 12.4, Jellyfin
   además hace el respaldo `pg_dump` en `{DataPath}/postgres-migration-backups` y aplica sus tres migraciones de
   código. En una base de ~110 000 elementos el arranque completo tarda unos segundos.

Para volver de 12.4 a una versión anterior hay que restaurar el respaldo: las migraciones de código de 12.4
modifican datos.

## Comprobado

- Suite completa contra PostgreSQL 17 y 18 con el runtime de cada versión: 12.1, 12.2, 12.3 y 12.4
  (`-p:JellyfinTestVersion`). En 12.2+ las pruebas de deriva curan los índices `OwnerId` y
  `DefaultInterfaceMembersAreImplemented` confirma que `RefreshStatistics` queda asociado a la implementación del
  proveedor.
- Jellyfin 12.4.0 (imagen oficial `12.4.20261026-211555`) sobre una copia de una base de producción 12.1
  (108 862 elementos, 4 usuarios), comparada con 12.1 + 3.1.0 sobre otra copia de la misma base: arranque sin
  errores, índices válidos, `UserData` idéntica, mismos recuentos por tipo y mismos favoritos y progreso por
  usuario. `MigrateRatingLevels` deja sin valor los elementos sin clasificación (comportamiento del núcleo, igual
  en SQLite), lo que puede cambiar ligeramente qué ven los usuarios con control parental hasta el siguiente
  escaneo. Tras un escaneo completo, `RefreshDatabaseStatisticsPostScanTask` ejecuta el `ANALYZE` del proveedor.
