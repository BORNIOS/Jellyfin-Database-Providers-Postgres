# What is new in 3.0.0 vs 2.0.1

Technical comparison between the **2.x** line (Jellyfin 10.11.x, .NET 9) and **3.0.0**
(Jellyfin 12.1.x, .NET 10). Every item is taken from the code and the commits on branch
`release/V3`.

> Spanish version (primary): [`novedades-3.0.0.md`](novedades-3.0.0.md)

---

## 1. Summary

| Area | 2.0.1 | 3.0.0 |
|---|---|---|
| Target Jellyfin | 10.11.x | **12.1.x** |
| Framework | net9.0 | **net10.0** |
| `targetAbi` | `10.11.0.0` | `12.1.0.0` |
| Provider contract | 10.11 | **full 12.1** (including `PurgeDatabase`) |
| Jellyfin full backup restore | ❌ breaks startup | ✅ supported |
| SQL console in the panel | ❌ | ✅ 10 templates + history |
| Test suite | hand-made runner | ✅ xUnit, 68 tests, generated summary |
| Advanced options (pool/timeout) | saved but ignored | ✅ persisted, validated and visible |
| Error logging | flat message | ✅ `PostgresException` structure + stack trace |
| Panel tabs | 4 | ✅ 5 (adds *SQL console*) |

---

## 2. Jellyfin 12.1 contract and runtime adaptation

Jellyfin 12.1 changed how the server discovers, starts and migrates database providers.
Version 3.0.0 carries the full adaptation work:

- **`MigrationAssemblyResolver`** (new): locates and loads the SQLite provider assembly that
  Jellyfin 12.1 ships next to the server instead of assuming it sits in the plugin folder.
- **`SqliteCompatibilityBootstrap`** (new): prepares the SQLite database per database (not per
  process), hardened so it cannot abort `Initialise` before Jellyfin has a logger.
- **`MigrationEngine`, `MigrationTableCopier`, `MigrationSchemaPreparer`,
  `MigrationCodeMigrations`, `MigrationService`**: rewritten to honour dependency order, copy
  in batches, truncate before the first row (avoiding later cascade surprises) and report a
  failed table as a failed import instead of failing silently.
- **`MigrationOptions`** and `MaintenanceService` aligned with the new lifecycle.

### `PurgeDatabase` no longer breaks startup

On the 2.x line `PurgeDatabase` threw `NotImplementedException`. Jellyfin 12.1 calls it when
restoring a **full system backup** (`FullSystemBackup`), so that operation left the server
unusable. In 3.0.0 it is implemented with `TRUNCATE … RESTART IDENTITY CASCADE`, validating
and quoting every table name the server sends (`public.Table`, `"public"."Table"` and
`Table`).

---

## 3. Read-only SQL console

New **SQL console** tab in the plugin panel:

- **Read-only by design**: the service validates the statement and rejects mutations
  (`INSERT`/`UPDATE`/`DELETE`/DDL/`TRUNCATE`) as well as multiple statements in one submission.
- **10 templates** for diagnostics: slow queries from `pg_stat_statements`, per-table sizes,
  unused indexes, active connections, bloat, locks, and more.
- Templates filter by `dbid` and exclude administrative statements, so they carry no noise
  from other databases on the same PostgreSQL server.
- **History** of recent queries kept in `sessionStorage`, with a clear button.
- The statement is returned **without comments but with literals and quoted identifiers
  untouched** (a bug caught by the suite itself: returning the masked copy turned
  `"BaseItems"` into `""` and PostgreSQL answered `42601`).

---

## 4. Automated verification (xUnit)

The hand-made runner was retired in favour of a standard suite runnable with `dotnet test`:

| Class | Coverage |
|---|---|
| `ProviderContractTests` | The provider exposes exactly one provider, no unimplemented interface members |
| `DatabaseConfigurationTests` | Engine detection from `database.xml` |
| `SchemaAuditTests` | Every table/column used in raw SQL exists in the EF model |
| `SqlRewriteTests` | 10.11 → 12.1 SQL rewriting is idempotent and scoped |
| `QueryConsoleTests` | Accepts read-only statements, rejects mutations and multi-statement input |
| `LoggingTests` | The log keeps level, message and stack trace; PostgreSQL detail reaches the file |
| `WebPageTests` | Panel integrity: element ids, symmetric ES/EN i18n keys |
| `AdvancedOptionTests` | Pool and timeout: the connection string wins, configuration fills gaps |
| `PostgresIntegrationTests` | Real integration: initial schema, import/export, purge, backups |

Also:

- `Tools/Generar-ResumenEjecutivo.ps1` generates
  [`VERIFICACION-RESUMEN.md`](VERIFICACION-RESUMEN.md) from the real `.trx`, grouped per class
  with a traffic light per result.
- `.github/workflows/verification.yml` runs the suite against a PostgreSQL 17 service and
  publishes the summary in the job *Summary* tab.
- `.github/workflows/build.yaml` builds and runs the suite on every push to `main`/`release/*`.

Result on branch `release/V3`: **68 tests, 0 failures**.

---

## 5. Logging that actually helps

- All plugin messages go to `<log>/Postgres-YYYY-MM-DD.log` (its own file, never mixed with the
  server log).
- PostgreSQL failures now include the structured `PostgresException` fields: `SqlState`,
  `Detail`, `Hint`, table, column, constraint and `WHERE`, each truncated to avoid flooding.
- The full stack trace is written **once per signature** (`SqlState|Constraint|Message`);
  repeats are summarised.
- The SQL involved is recorded (500 characters) and, when **Log command parameters** is on,
  its values too.

---

## 6. Advanced options that work

In 2.0.1 the pool fields were saved but the provider **overwrote** the pool with a fixed value
and the current status never showed them. In 3.0.0:

- `SaveConfig` **validates** (`MaxPoolSize >= MinPoolSize`, otherwise HTTP 400) and
  **persists** `MinPoolSize`, `MaxPoolSize` and `MaxAutoPrepare`.
- The provider builds the connection string with `BuildTunedConnectionString`: **if the user's
  connection string carries the key, it wins**; otherwise the saved configuration fills the
  gap. Command timeout is always applied.
- **Current status** shows `Pool: min–max · Timeout: N s · Prepared statements: N`, so the
  effect of a change is visible on the same screen.
- Changing the pool requires a Jellyfin restart (the panel warns about it).

---

## 7. Clearer panel

- Five coherent tabs: **Configuration**, **Migration**, **Maintenance**, **Health** and
  **SQL console**.
- **Symmetric** ES/EN translation dictionaries (an automated test fails when a key is added to
  one language and not the other).
- Tables, action buttons, notices and errors share one visual language; visible focus for
  keyboard navigation.
- Query history with in-UI clearing.

---

## 8. More robust export to SQLite

- **2.0.1**: primary-key columns are copied verbatim, preserving letter case, and a constraint
  violation is reported with table, row number and key values.
- **3.0.0** adds on top: copying only columns present in both engines, removing orphaned rows
  that reference non-existent users, and creating a minimal valid `library.db` so Jellyfin
  12.1 legacy routines do not abort startup (see
  [`migration-10.11-to-12.en.md`](migration-10.11-to-12.en.md)).

---

## 9. What does not change

- The plugin `guid` and catalog name are identical, so upgrading from 2.x keeps the saved
  configuration.
- Scheduled tasks (backup, VACUUM ANALYZE, REINDEX, index optimization) keep their names and
  schedules.
- Optional JellyTrend integration is still available through
  `JellyTrendContract/IRecommendationQueryProvider.cs`.

---

## 10. Keep reading

- [Migration manual from 10.11.x to 12.x](migration-10.11-to-12.en.md)
- [Release plan for 2.0.1 and 3.0.0](release-plan.en.md)
- [Technical guide for the Jellyfin 12.1 adaptation](JELLYFIN-12.1.md)
