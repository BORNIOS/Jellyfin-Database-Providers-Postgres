# Actualizar Jellyfin sin esperar al proveedor

El proveedor se compila contra la versión de Jellyfin más antigua que admite (`targetAbi` 12.1.0.0) y un único
paquete sirve para todas las posteriores. Así nadie está obligado a actualizar Jellyfin para usar el proveedor.

Jellyfin carga un proveedor compilado para una versión anterior (`appVersion >= targetAbi`), pero solo ejecuta
las migraciones EF que trae el proveedor. Sin más, una versión nueva de Jellyfin que cambia su modelo dejaría la
base atrás sin ningún aviso:
- si faltan índices, las consultas se vuelven lentas;
- si faltan columnas, fallan.

Lo cubren dos mecanismos: en el servidor, la comprobación de deriva del esquema; en el repositorio, un script que
comprueba cada versión nueva de Jellyfin con el paquete actual.

> **No subir `targetAbi` sin necesidad.** `database.xml` carga la DLL del proveedor directamente, sin la
> comprobación de ABI de los plugins: un paquete compilado contra una versión más nueva que la del servidor impide
> que Jellyfin arranque.

## En el servidor: deriva del esquema

`SchemaDriftGuard` compara el snapshot de migraciones del proveedor con el modelo de Jellyfin en ejecución. Es la
misma comparación que hace `HasPendingModelChanges()`, pero devuelve las operaciones que faltan.

| Operación que falta | Qué hace el proveedor |
| --- | --- |
| Crear o eliminar un índice no único, crear una tabla, añadir una columna nulable sin valor por defecto o con un valor por defecto constante | La aplica |
| Índice único (falla si hay duplicados, que Jellyfin limpia con SQL que el diff del modelo no incluye) | No la aplica |
| Columna calculada o con valor por defecto SQL (`now()`, etc.): PostgreSQL reescribe toda la tabla con bloqueo exclusivo | No la aplica |
| Eliminar o renombrar columnas o tablas, cambiar tipos, claves foráneas, cambios de datos | No la aplica: error en el log y en Health, hay que actualizar el proveedor |

Detalles de la aplicación:
- Las operaciones aditivas solo se aplican si **todas** las que faltan lo son.
- Los índices se crean y eliminan con **`CONCURRENTLY`**, así que `BaseItems` sigue aceptando escrituras mientras
  tanto, también en bibliotecas grandes. Como `CONCURRENTLY` no admite transacciones, cada sentencia se confirma
  por separado; tablas y columnas se aplican cada una en su transacción. Una construcción interrumpida deja un
  índice inválido (`indisvalid = false`): cuenta como pendiente y se reconstruye en la comprobación siguiente.
- Un bloqueo consultivo de sesión (`pg_try_advisory_lock`, sondeado cada segundo) deja aplicar cambios a un único
  servidor a la vez. No se espera con `pg_advisory_lock`: una sesión esperando dentro de una sentencia retiene una
  instantánea, y `CREATE INDEX CONCURRENTLY` espera a que terminen todas las anteriores. Con el bloqueo tomado se
  vuelve a leer el catálogo, por si otro servidor ya aplicó parte de los cambios.
- **Qué está hecho se lee siempre del catálogo** (`pg_index`, `pg_attribute`), nunca de un registro propio: un
  índice cuenta como aplicado solo si existe con el mismo nombre, tabla, unicidad, columnas y filtro. En los
  arranques siguientes el informe dice `AlreadyHealed` y no se repite nada.
- Además de la diferencia snapshot/modelo, **cada índice del modelo se compara con su definición en la base**. Así
  se detecta un índice que quedó con otra definición, por ejemplo porque una migración idempotente lo encontró ya
  creado. Se reconstruye (`DROP` + `CREATE`). Una tabla o columna del modelo que falta sin que ninguna migración la
  explique se informa como error.
- Cada operación aplicada queda anotada en `__PostgresSchemaDrift`, con su SQL y las versiones de Jellyfin y del
  proveedor. Es un registro de auditoría. También evita reconstruir en bucle: si una operación ya se aplicó en esta
  misma versión de Jellyfin y del proveedor y la base sigue sin coincidir, se informa (`Detected`) en lugar de
  repetirse en cada arranque.

**Cuándo se ejecuta:**
1. **En `Initialise`, antes de las migraciones de Jellyfin.** Una migración de código nueva de Jellyfin puede
   necesitar ya la columna. Si el proveedor tiene migraciones propias pendientes, la comprobación se aplaza
   (`Deferred`).
2. **Al arrancar los servicios** (`SchemaDriftStartupService`), cuando Jellyfin ya aplicó todas las migraciones.
   Solo se ejecuta si la primera se aplazó o falló.

**Si una versión futura del proveedor trae la migración real** (por ejemplo, al subir la versión base): la
migración repite operaciones que ya están hechas. Para que no falle con "ya existe", el proveedor genera el SQL de migraciones en forma idempotente
(`IdempotentMigrationsSqlGenerator`):
- `CREATE INDEX` y `CREATE TABLE` con `IF NOT EXISTS`;
- `ADD COLUMN IF NOT EXISTS`;
- `DROP INDEX IF EXISTS`.

En una base sin esos objetos el resultado es idéntico al de siempre.

### Configuración

| Valor de `SchemaDriftMode` (config del plugin) | Efecto |
| --- | --- |
| `Heal` (por defecto) | Aplica la deriva aditiva e informa del resto |
| `ReportOnly` | Solo informa (Health `Warn`) |
| `Off` | No comprueba nada |

La variable de entorno `JELLYFIN_PG_SCHEMA_DRIFT` (`heal`, `report` u `off`) tiene prioridad sobre la
configuración, lo que es útil en Docker. Un valor no reconocido nunca activa cambios automáticos: se usa el modo
configurado, o `ReportOnly` si no hay ninguno, y se avisa en el log.

### Dónde se ve

- **Log de Jellyfin:**
  ```
  Schema drift check (Healed): Aplicados 4 cambio(s) de esquema de Jellyfin 12.4.0.0 que el proveedor 3.2.0.0 aún no incluye: ...
  ```
- **Health:** hallazgo `SchemaDrift`. `Error` si hace falta actualizar el proveedor, `Warn` en modo `ReportOnly`,
  `Ok` si se aplicó.
- **API:**
  - `GET Plugins/PostgresProvider/SchemaDrift` devuelve el último informe;
  - `POST Plugins/PostgresProvider/SchemaDrift/Check` vuelve a comprobar (`?apply=true` para aplicar).

Jellyfin carga este ensamblado dos veces, como proveedor (desde `database.xml`) y como plugin, en contextos de
carga distintos. Por eso:
- el informe se comparte por `AppDomain` en JSON;
- la comprobación temprana lee `SchemaDriftMode` del XML de configuración del plugin.

### Comprobado

- **Suite** contra PostgreSQL 17 y 18, con el runtime de Jellyfin 12.1, 12.2, 12.3 y 12.4 (ver más abajo). Las
  situaciones de `SchemaDriftGuardTests`: curar la deriva una vez y no repetirla, `ReportOnly`, cambios no seguros
  bloqueados, un índice con otra definición o inválido reconstruido, y una migración posterior con los mismos
  cambios aplicada sobre los objetos ya creados.
- **Jellyfin 12.4** (imagen oficial) con el proveedor compilado para 12.1 sobre una copia de una base de producción
  12.1 (108 862 elementos):
  1. **Primer arranque:** la deriva se aplicó antes de las migraciones de Jellyfin. Fueron los dos índices
     `OwnerId`, que pasaron a ser parciales. Arranque en 8 s, sin errores.
  2. **Reinicio:** `AlreadyHealed`, sin cambios.

## En el repositorio: comprobar cada versión nueva de Jellyfin

`Tools/Actualizar-Jellyfin.ps1` responde a una pregunta: **¿funciona el paquete actual en esta versión de
Jellyfin?** Por defecto no modifica ningún archivo:
1. **Versiones:** toma la última estable de `Jellyfin.Controller` en NuGet (o `-JellyfinVersion`) y la versión de
   EF Core que trae ese servidor, y comprueba que el Npgsql EF del paquete funciona con ella. Por ejemplo, Npgsql
   EF 10.0.4 exige EF 10.0.15 y Jellyfin 12.4 trae 10.0.14.
2. **Compilación y pruebas con el runtime de esa versión** (`-p:JellyfinTestVersion`). Fallan, entre otras:
   - `SnapshotMatchesTheJellyfinModel`, si la deriva del modelo incluye cambios que la comprobación de deriva no
     aplica sola;
   - `DefaultInterfaceMembersAreImplemented`, si `IJellyfinDatabaseProvider` tiene miembros nuevos sin implementar.
3. **Cambios de Jellyfin:** lista los cambios entre las dos etiquetas que afectan a un proveedor: la interfaz
   `IJellyfinDatabaseProvider`, el modelo, las migraciones de código y las migraciones SQLite.

El resultado queda en `artifacts/actualizacion-jellyfin.md` y el script termina con código distinto de 0 si el
paquete no es compatible.

```powershell
$env:POSTGRES_TEST_CONNECTION = 'Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=...'
./Tools/Actualizar-Jellyfin.ps1                          # última estable de NuGet
./Tools/Actualizar-Jellyfin.ps1 -JellyfinVersion 12.5.0
```

Lo mismo sin el script:

```sh
dotnet test Tests/Jellyfin.Database.Providers.Postgres.Tests -p:JellyfinTestVersion=12.5.0 -p:EfCoreTestVersion=<EF Core de 12.5>
```

**`-SubirBase`** sube además la versión base del paquete a esa versión: paquetes `Jellyfin.*`, EF Core y Npgsql
EF, `targetAbi`, versión del plugin, migración `Jellyfin<versión>` con `dotnet ef` (sin BOM y con la supresión de
CA1861, como las existentes) y `Resources/schema.sql`. Deja los cambios en el árbol de trabajo, sin commit, y
avisa de que los servidores anteriores dejan de arrancar con ese paquete. Es para cuando se decida dejar de
admitir las versiones anteriores, o cuando un cambio de Jellyfin no se pueda cubrir de otra forma.

## Lo que sigue requiriendo a una persona

- **Miembros nuevos de `IJellyfinDatabaseProvider`:** implementarlos como métodos `public virtual` (no
  `override`: el paquete se compila contra una versión donde no existen), como `RefreshStatistics` en 12.2. La
  prueba `DefaultInterfaceMembersAreImplemented`, ejecutada con el runtime nuevo, falla mientras alguno use la
  implementación por defecto.
- **Migraciones de código de Jellyfin con SQL exclusivo de SQLite:** adaptarlas, como hace
  `Jellyfin121MigrationInterceptor`.
- **Cambios de esquema que la comprobación no aplica** (destructivos, índices únicos, columnas que reescriben la
  tabla): requieren subir la versión base con una migración.
- **Validación final:** probar el paquete con una copia de una base real antes de anunciarlo como compatible.
