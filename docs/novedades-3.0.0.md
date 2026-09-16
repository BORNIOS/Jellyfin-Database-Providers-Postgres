# Novedades de 3.0.0 frente a 2.0.1

Comparativa técnica entre la línea **2.x** (Jellyfin 10.11.x, .NET 9) y **3.0.0**
(Jellyfin 12.1.x, .NET 10). Todos los puntos están tomados del código y de los commits de
la rama `release/V3`.

> English version: [`whats-new-3.0.0.en.md`](whats-new-3.0.0.en.md)

---

## 1. Resumen

| Área | 2.0.1 | 3.0.0 |
|---|---|---|
| Jellyfin objetivo | 10.11.x | **12.1.x** |
| Framework | net9.0 | **net10.0** |
| `targetAbi` | `10.11.0.0` | `12.1.0.0` |
| Contrato de proveedor | 10.11 | **12.1 completo** (incluido `PurgeDatabase`) |
| Restauración de respaldo completo de Jellyfin | ❌ rompe el arranque | ✅ soportada |
| Consola SQL en el panel | ❌ | ✅ 10 plantillas + historial |
| Suite de pruebas | runner artesanal | ✅ xUnit, 68 pruebas, resumen autogenerado |
| Opciones avanzadas (pool/timeout) | guardadas pero ignoradas | ✅ persistidas, validadas y visibles |
| Log de errores | mensaje plano | ✅ estructura de `PostgresException` + traza |
| Pestañas del panel | 4 | ✅ 5 (se añade *Consola SQL*) |

---

## 2. Adaptación al contrato y al runtime de Jellyfin 12.1

Jellyfin 12.1 cambió la forma en que el servidor descubre, arranca y migra proveedores de
base de datos. La versión 3.0.0 incorpora el trabajo de adaptación completo:

- **`MigrationAssemblyResolver`** (nuevo): localiza y carga el ensamblado del proveedor
  SQLite que Jellyfin 12.1 distribuye junto al servidor, en lugar de asumir que está en el
  directorio del plugin.
- **`SqliteCompatibilityBootstrap`** (nuevo): prepara la base SQLite por base de datos (no
  por proceso), endurecido para no abortar `Initialise` antes de que Jellyfin tenga logger.
- **`MigrationEngine`, `MigrationTableCopier`, `MigrationSchemaPreparer`,
  `MigrationCodeMigrations`, `MigrationService`**: reescritos para respetar el orden de
  dependencias, copiar por lotes, truncar antes de la primera fila (evitando cascadas
  posteriores), y reportar una tabla fallida como importación fallida en vez de continuar en
  silencio.
- **`MigrationOptions`** y `MaintenanceService` alineados con el nuevo ciclo de vida.

### `PurgeDatabase` ya no rompe el arranque

En la línea 2.x, `PurgeDatabase` lanzaba `NotImplementedException`. Jellyfin 12.1 lo invoca
al restaurar un **respaldo completo del sistema** (`FullSystemBackup`), por lo que esa
operación dejaba el servidor inservible. En 3.0.0 está implementado con
`TRUNCATE … RESTART IDENTITY CASCADE`, validando y citando correctamente los nombres de
tabla que envía el servidor (incluidas las formas `public.Tabla`, `"public"."Tabla"` y
`Tabla`).

---

## 3. Consola SQL de solo lectura

Nueva pestaña **Consola SQL** en el panel del plugin:

- **Solo lectura por diseño**: el servicio valida la sentencia y rechaza mutaciones
  (`INSERT`/`UPDATE`/`DELETE`/`DDL`/`TRUNCATE`) y varias sentencias en un solo envío.
- **10 plantillas** listas para diagnosticar: consultas lentas de `pg_stat_statements`,
  tamaños por tabla, índices no usados, conexiones activas, bloat, bloqueos, etc.
- Las plantillas filtran por `dbid` y excluyen sentencias administrativas, de modo que no
  arrastran ruido de otras bases del mismo servidor PostgreSQL.
- **Historial** de las últimas consultas conservado en `sessionStorage`, con botón para
  limpiarlo.
- La sentencia se devuelve **sin comentarios pero sin tocar literales ni identificadores
  entrecomillados** (un fallo detectado por las propias pruebas: devolver la versión
  enmascarada convertía `"BaseItems"` en `""` y PostgreSQL respondía `42601`).

---

## 4. Verificación automatizada (xUnit)

Se retiró el runner propio y se sustituyó por una suite estándar, ejecutable con `dotnet test`:

| Clase | Qué cubre |
|---|---|
| `ProviderContractTests` | El proveedor expone exactamente un proveedor, sin miembros de interfaz sin implementar |
| `DatabaseConfigurationTests` | Detección del motor a partir de `database.xml` |
| `SchemaAuditTests` | Toda tabla/columna usada en SQL crudo existe en el modelo EF |
| `SqlRewriteTests` | Reescritura de SQL de 10.11 → 12.1 idempotente y acotada |
| `QueryConsoleTests` | Acepta solo lectura, rechaza mutaciones y sentencias múltiples |
| `LoggingTests` | El log conserva nivel, mensaje y traza; el detalle de PostgreSQL llega al archivo |
| `WebPageTests` | Integridad del panel: ids, claves i18n simétricas ES/EN |
| `AdvancedOptionTests` | Pool y timeout: la cadena de conexión manda, la configuración rellena huecos |
| `PostgresIntegrationTests` | Integración real: esquema inicial, importación/exportación, purga, respaldos |

Además:

- `Tools/Generar-ResumenEjecutivo.ps1` genera
  [`VERIFICACION-RESUMEN.md`](VERIFICACION-RESUMEN.md) a partir del `.trx` real, con
  clasificación por clase y semáforo por resultado.
- `.github/workflows/verification.yml` ejecuta la suite contra un servicio PostgreSQL 17 y
  publica el resumen en la pestaña *Summary* del job.
- `.github/workflows/build.yaml` compila y ejecuta la suite en cada push a `main`/`release/*`.

Resultado en la rama `release/V3`: **68 pruebas, 0 fallos**.

---

## 5. Logging útil de verdad

- Todos los mensajes del plugin van a `<log>/Postgres-AAAA-MM-DD.log` (un archivo propio, sin
  mezclarse con el log del servidor).
- Los errores de PostgreSQL incluyen ahora los campos estructurados de `PostgresException`:
  `SqlState`, `Detail`, `Hint`, tabla, columna, constraint y `WHERE`, cada uno truncado para
  no inundar el archivo.
- La traza completa se escribe **una vez por firma** (`SqlState|Constraint|Message`);
  las repeticiones se resumen.
- Los comandos SQL implicados se registran (500 caracteres) y, si se activa
  **Registrar parámetros de comando**, también sus valores.

---

## 6. Opciones avanzadas que sí funcionan

En 2.0.1 los campos de pool se guardaban pero el proveedor **sobrescribía** el pool tomado de
la configuración con un valor fijo, y el estado actual no los mostraba. En 3.0.0:

- `SaveConfig` **valida** (`MaxPoolSize >= MinPoolSize`; si no, responde 400) y **persiste**
  `MinPoolSize`, `MaxPoolSize` y `MaxAutoPrepare`.
- El proveedor construye la cadena de conexión con `BuildTunedConnectionString`: **si la
  cadena escrita por el usuario trae la clave, esa manda**; si no, se rellena con la
  configuración guardada. El tiempo de espera se aplica siempre.
- **Current status** muestra `Pool: min–max · Timeout: N s · Prepared statements: N`, de modo
  que el efecto del cambio es visible en la misma pantalla.
- Cambiar el pool requiere reiniciar Jellyfin (el propio panel lo advierte).

---

## 7. Panel más claro

- Cinco pestañas coherentes: **Configuracion**, **Migracion**, **Mantenimiento**, **Health**
  y **Consola SQL**.
- Diccionarios de traducción ES/EN **simétricos** (una prueba automática falla si se añade una
  clave a un idioma y no al otro).
- Tablas, botones de acción, avisos y errores con el mismo lenguaje visual; foco visible para
  navegación por teclado.
- Historial de consultas y limpieza del mismo desde la propia interfaz.

---

## 8. Exportación a SQLite más robusta

- **2.0.1**: las columnas que forman la clave primaria se copian verbatim, preservando
  mayúsculas/minúsculas, y una violación de constraint se reporta con tabla, número de fila y
  valores de la clave.
- **3.0.0** añade sobre eso: copia solo de columnas presentes en ambos motores, limpieza de
  filas huérfanas que referencian usuarios inexistentes y creación de un `library.db` mínimo
  válido para que las rutinas heredadas de Jellyfin 12.1 no aborten el arranque
  (ver [`migracion-10.11-a-12.md`](migracion-10.11-a-12.md)).

---

## 9. Qué NO cambia

- El `guid` del plugin y el nombre del catálogo son idénticos, así que la actualización desde
  la versión 2.x conserva la configuración guardada.
- Las tareas programadas (backup, VACUUM ANALYZE, REINDEX, optimización de índices) mantienen
  sus nombres y su programación.
- La integración opcional con JellyTrend sigue disponible a través de
  `JellyTrendContract/IRecommendationQueryProvider.cs`.

---

## 10. Sigue leyendo

- [Manual de migración de 10.11.x a 12.x](migracion-10.11-a-12.md)
- [Plan de publicación de 2.0.1 y 3.0.0](plan-de-publicacion.md)
- [Guía técnica de la adaptación a Jellyfin 12.1](JELLYFIN-12.1.md)
