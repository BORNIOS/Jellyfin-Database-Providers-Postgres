# Plugin integration with PG Provider

PG Provider gives Jellyfin plugins private, managed and observable PostgreSQL storage without exposing the connection string or Jellyfin's `public` schema.

![Integrations tab](../Screenshots/Tab-Intergations.png)

The **🧩 Integrations** tab shows which plugins use the provider, their storage and tables, a bounded data preview, and the maintenance policy selected by the administrator.

## Isolation model

Each plugin owns a readable schema name with a GUID suffix, such as `myplugin_0123456789abcdef0123456789abcdef`. PG Provider permanently records the plugin GUID, name and schema together.

The contract never exposes connection strings, `NpgsqlConnection`, Jellyfin's `public` schema, or another plugin's data and policies. Use Jellyfin's normal contracts for Jellyfin data; use PG Provider only for state owned by your plugin.

Loaded plugins are trusted in-process code. This API prevents accidental cross-plugin access; it is not a hostile-code security boundary.

## Discover and use the host

The public capability is `jellyfin.postgres.private-schema-host`, protocol version `1`, and the contract type is `Jellyfin.Database.Providers.Postgres.Api.IPluginSchemaHost`.

Request `IPluginSchemaHost` from Jellyfin's service provider when referencing the provider assembly. If PostgreSQL is optional, a missing host must select your plugin's fallback storage.

```csharp
var host = serviceProvider.GetService<IPluginSchemaHost>();
if (host is null)
{
    // PG Provider is unavailable: use the plugin fallback.
    return;
}
```

### Plugin load contexts

Jellyfin can load each plugin in a separate assembly load context. Consequently, two types both named `IPluginSchemaHost` are not necessarily assignable when they come from different copies of the DLL. In that case, `GetService<IPluginSchemaHost>()` through a consumer's private reference can return `null` even though PG Provider is active and another plugin is already using it.

For robust optional PostgreSQL discovery, use this sequence:

1. Inspect `IPluginManager.Plugins` and locate the live PG Provider plugin instance first. Do not start from a DLL on disk or an arbitrary loaded copy.
2. In that live assembly, locate `PostgresSchemaHostCapability`, verify `ProtocolName` and a compatible protocol version, then obtain the type named by `ContractTypeName`.
3. Ask the non-generic service provider for that **live-assembly type**: `services.GetService(activeContractType)`.
4. If a host is returned, invoke it through a reflection adapter constrained to the public contract. Otherwise, log the precise technical reason and use the plugin fallback.

Diagnostics should distinguish `PG Provider is not installed or active`, `the contract was not found in the live assembly`, and `DI did not resolve the active contract type`. Do not flatten these into “the provider did not register”; that wording hides type-identity failures and makes retries look like a remedy.

Never create `PostgresPluginSchemaHost`, or any provider implementation, through `ActivatorUtilities`, reflection, or constructors. The implementation is deliberately internal, while the Jellyfin-registered host has its own lifetime, configuration, and isolation guarantees. Creating another instance couples the consumer to non-contractual details and can bypass provider state. When reflection is necessary, constrain it to the public marker and `IPluginSchemaHost` from the live assembly.

The long-term strongly typed solution is a stable abstractions assembly shared by provider and consumers. Until that exists, resolving the contract type from the live provider assembly prevents every consumer from carrying its own incompatible public-contract copy.

Use the immutable GUID from your plugin manifest and a stable lowercase schema name ending with that GUID without hyphens.

```csharp
var schema = await host.OpenSchemaAsync(
    new PluginSchemaRequest(pluginId, "myplugin_0123456789abcdef0123456789abcdef"), cancellationToken);

await host.ApplyMigrationsAsync(schema,
[
    new PluginSchemaMigration(
        "001_create_state",
        "CREATE TABLE state (id uuid PRIMARY KEY, value jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now())"),
    new PluginSchemaMigration(
        "002_state_updated_at_index",
        "CREATE INDEX ix_state_updated_at ON state (updated_at DESC)")
], cancellationToken);
```

Migrations are transactional, serialized per schema and checksum-protected. Applied migrations are immutable; create a new migration instead of editing one. `MigratePreRegistrySchemaAsync` is available only for a deliberate, exact pre-registry schema rename.

## Parameterized data access

Commands and queries run as one unqualified statement inside the assigned schema. Always parameterize values.

```csharp
await host.ExecuteAsync(
    schema,
    "INSERT INTO state (id, value) VALUES (@id, @value) ON CONFLICT (id) DO UPDATE SET value = excluded.value, updated_at = now()",
    [new PluginSqlParameter("id", entityId), new PluginSqlParameter("value", payload)],
    cancellationToken);
```

## Intentional limits and performance

The host rejects multi-statement SQL, comments, qualified cross-schema references, `public`, PostgreSQL catalogs, `COPY`, maintenance commands, role/extension/permission management and schema changes outside your migrations.

Core Jellyfin optimizations such as `pg_trgm`, GIN indexes and query interceptors do not automatically optimize private tables. Define evidence-based indexes through migrations, parameterize every command and benchmark real workloads. PG → SQLite export protects Jellyfin data; each plugin remains responsible for its private-data fallback or export strategy.

## Integrations and maintenance

The administrator controls scheduled work in **🧩 Integrations**:

- A complete PostgreSQL backup always includes every registered private schema.
- Scheduled `ANALYZE` starts enabled to refresh planner statistics.
- Scheduled `VACUUM (ANALYZE)` and concurrent `REINDEX` start disabled and run only for explicitly selected schemas.
- Manual actions are scoped to the selected schema: `ANALYZE` per schema, plus `VACUUM (ANALYZE)` and `REINDEX` per table.

These policies are administrative, not part of the consumer contract. A plugin cannot opt itself into maintenance, modify another plugin's policy or inspect another schema.

## Contribute

Discuss public-contract changes in an issue before implementation to preserve compatibility. Integration tests, observability improvements, documentation and real-world private-schema plugin use cases are welcome.

See the [Spanish guide](integracion-desarrolladores.md).
