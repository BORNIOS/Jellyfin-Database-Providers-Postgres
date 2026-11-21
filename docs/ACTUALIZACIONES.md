# Actualizar Jellyfin sin esperar al proveedor

Jellyfin carga un proveedor compilado para una versión anterior (`appVersion >= targetAbi`) y solo ejecuta
las migraciones EF que trae el proveedor. Hasta ahora, actualizar Jellyfin antes que el proveedor dejaba la base
atrás sin ningún aviso:
- si faltan índices, las consultas se vuelven lentas;
- si faltan columnas, fallan.

Y cada versión nueva de Jellyfin obligaba a repetir a mano el mismo trabajo en el repositorio. Dos mecanismos
cubren ahora los dos lados.

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
- Se aplican en una transacción con bloqueo consultivo (`pg_advisory_xact_lock`).
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

**Cuando llega la versión del proveedor con la migración real:** la migración repite operaciones que ya están
hechas. Para que no falle con "ya existe", el proveedor genera el SQL de migraciones en forma idempotente
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
  Schema drift check (Healed): Aplicados 4 cambio(s) de esquema de Jellyfin 12.4.0.0 que el proveedor 3.1.99.0 aún no incluye: ...
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

Se probó con el proveedor 3.1.0 más este mecanismo (compilado para 12.1, sin `Jellyfin124`) sobre la imagen
oficial de Jellyfin 12.4. La base era una copia de una base de producción 12.1 (108 862 elementos).
1. **Primer arranque:** la deriva se aplicó antes de las migraciones de Jellyfin. Fueron los dos índices `OwnerId`,
   que pasaron a ser parciales. Arranque en 8 s, sin errores.
2. **Reinicio:** `AlreadyHealed`, sin cambios.
3. **Proveedor 3.2:** al cambiar a 3.2 (que trae `Jellyfin124`), la comprobación temprana se aplazó, `Jellyfin124`
   se aplicó sobre los índices ya creados y la segunda comprobación dio `InSync`.

Las mismas situaciones están en `SchemaDriftGuardTests`.

## En el repositorio: seguimiento de versiones

El workflow **Seguimiento de Jellyfin** (`.github/workflows/jellyfin-bump.yml`) se ejecuta cada lunes. También se
puede lanzar a mano, con una versión concreta.

Si NuGet tiene un `Jellyfin.Controller` más nuevo, ejecuta `Tools/Actualizar-Jellyfin.ps1`, que hace lo
siguiente:
1. **Paquetes:**
   - `Jellyfin.*` a la versión nueva;
   - EF Core a la versión que declara `Jellyfin.Database.Implementations`, es decir, la del servidor;
   - Npgsql EF al más reciente de esa serie cuya dependencia de EF Core no supere la del servidor. Por ejemplo,
     Npgsql EF 10.0.4 exige EF 10.0.15 y Jellyfin 12.4 trae 10.0.14.
2. **Versiones:** `targetAbi` y versión del plugin. Si cambia la versión mayor de Jellyfin, sube la mayor del
   plugin; si cambia la menor, la menor; si es un parche, el parche.
3. **Migración EF:** si `dotnet ef migrations has-pending-model-changes` detecta cambios, genera la migración
   `Jellyfin<versión>` (sin BOM y con la supresión de CA1861, como las existentes) y regenera `Resources/schema.sql`.
4. **Compilación y pruebas:** compila en Release y ejecuta la suite contra PostgreSQL 17.
5. **Cambios de Jellyfin:** lista los cambios entre las dos etiquetas que afectan a un proveedor: la interfaz
   `IJellyfinDatabaseProvider`, el modelo, las migraciones de código y las migraciones SQLite.
6. **PR:** abre un PR `auto/jellyfin-<versión>` con el resumen y una lista de revisión manual. Queda en borrador
   si algo falla.

Al ejecutarlo sobre 3.1.0 con `-JellyfinVersion 12.4.0` se obtuvo lo mismo que se hizo a mano para 3.2.0:
- la misma migración `Jellyfin124` y el mismo snapshot;
- el mismo `schema.sql`, salvo la marca de tiempo;
- y avisó del cambio de `IJellyfinDatabaseProvider` (`RefreshStatistics`) y de las migraciones de código nuevas.

**Requisitos:**
- Settings → Actions → General → *Allow GitHub Actions to create and approve pull requests*.
- Opcional: el secreto `JELLYFIN_BUMP_TOKEN`, un PAT con `contents` y `pull-requests`. Con `GITHUB_TOKEN`, GitHub no
  ejecuta otros workflows sobre el PR, aunque la suite ya se ejecuta dentro del propio job.

**En local:**

```powershell
$env:POSTGRES_TEST_CONNECTION = 'Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=...'
./Tools/Actualizar-Jellyfin.ps1                 # última estable de NuGet
./Tools/Actualizar-Jellyfin.ps1 -JellyfinVersion 12.5.0
```

El script no hace commit ni push. Deja los cambios en el árbol de trabajo y el resumen en
`artifacts/actualizacion-jellyfin.md`.

## Lo que sigue requiriendo a una persona

- **Miembros nuevos de `IJellyfinDatabaseProvider`:** implementarlos, como `RefreshStatistics` en 12.4. La prueba
  `DefaultInterfaceMembersAreImplemented` falla mientras alguno use la implementación por defecto.
- **Migraciones de código de Jellyfin con SQL exclusivo de SQLite:** adaptarlas, como hace
  `Jellyfin121MigrationInterceptor`.
- **Cambios de esquema destructivos:** la guardia nunca los aplica.
- **Validación final:** probar el paquete con una copia de una base real antes de publicar el release.
