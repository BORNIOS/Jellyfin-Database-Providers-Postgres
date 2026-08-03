# PostgreSQL for Jellyfin

![PostgreSQL plugin logo](Jellyfin.Database.Providers.Postgres/Resources/logo.png)

Use PostgreSQL as Jellyfin's database backend without modifying Jellyfin core.

[![Jellyfin 10.11.x](https://img.shields.io/badge/Jellyfin-10.11.x-blue?style=flat-square)](https://jellyfin.org)
[![Release](https://img.shields.io/github/v/release/BORNIOS/Jellyfin-Database-Providers-Postgres?style=flat-square)](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/BORNIOS/Jellyfin-Database-Providers-Postgres/total?style=flat-square&color=00A4DC&label=downloads)](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/releases)
[![Discord](https://img.shields.io/badge/Discord-Jellyfin_Community-5865F2?style=flat-square&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=flat-square&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)

Language: [Español](README.md) | [English](README.en.md)

## What it solves

- Better behavior with large libraries and concurrent use.
- **Instant search** sub-15ms with GIN trigram index (pg_trgm).
- **Automatic optimization** of indexes and autovacuum tuning in one action.
- Backup and restore from plugin UI (pg_dump/psql).
- Controlled switch between PostgreSQL and SQLite (both directions).
- **PostgreSQL → SQLite export** directly to a `.db` file without external tools.

## Compatibility

| Component | Version |
| --- | --- |
| Jellyfin | 10.11.10 |
| PostgreSQL | 13+ (recommended 16/17) |

## Install

### Option A: plugin repository (recommended)

1. In Jellyfin: Admin -> Plugins -> Plugin Repositories.
2. Add this URL:

```text
https://raw.githubusercontent.com/BORNIOS/Jellyfin-Database-Providers-Postgres/main/manifest.json
```

3. Save, refresh catalog, install PostgreSQL Database Provider.
4. Restart Jellyfin.

### Option B: manual ZIP install

1. Download latest release ZIP.
2. Extract to Jellyfin plugin directory.
3. Restart Jellyfin.

## Plugin UI overview

The plugin page has 3 tabs:

- ⚙️ **Configuration**: PostgreSQL connection, pool, timeout, binary paths and backup defaults.
- 🔄 **Migration**: two cards:
  - **SQLite → PostgreSQL**: copies jellyfin.db to PostgreSQL with live progress.
  - **PostgreSQL → SQLite**: exports all data directly to a native `.db` file.
- 🛠️ **Maintenance**: VACUUM ANALYZE, REINDEX, **Apply optimizations** (GIN indexes), backup/restore, and DB stats.

## Instant Search (InstantSearch)

Starting with v1.0.0.1, the plugin exposes a dedicated search endpoint that bypasses EF Core and queries directly using a GIN trigram index (pg_trgm):

- Typical latency < 15 ms on medium and large libraries.
- Searches by item name, path, and type.
- Falls back to `ILIKE` if GIN indexes have not been created yet.
- Activated when you apply the performance optimization from the Configuration tab.

### Enable GIN indexes

1. In the plugin: **Maintenance** tab -> **Apply optimizations**.
2. This creates 9 GIN indexes CONCURRENTLY and tunes autovacuum on critical tables.
3. The status of each index (created / pending) is shown in the same card.
4. The **Optimize GIN Indexes** scheduled task (weekly, Sunday 05:00) keeps indexes fresh.
5. While GIN indexes do not exist, search falls back to `ILIKE` automatically.

## Export PostgreSQL → SQLite

Card available in the Migration tab, below "Migrate data from SQLite to PostgreSQL".

**Use cases:**
- Revert Jellyfin to SQLite without losing data.
- Portable backup of the database in native format.
- Local inspection or testing of database contents.

**How it works:**
1. The plugin auto-detects the native `jellyfin.db` path (`{DataPath}/jellyfin.db`).
2. If the `.db` file does not exist → it is created with the schema derived from PostgreSQL.
3. If the `.db` file already exists → table structure is preserved; only data is replaced (`DELETE` + `INSERT`).
4. Data is written in transactions of 10,000 rows with `PRAGMA journal_mode=WAL`.
5. No external tools required (`sqlite3`, `pg_dump`, etc.).

**Steps:**
1. Go to Migration -> **Export PostgreSQL → SQLite**.
2. Verify or change the target `.db` path.
3. Start export and follow live progress.

## How to migrate from SQLite to PostgreSQL

Installing the plugin does not migrate data by itself.

1. Create an empty PostgreSQL database (for example `jellyfin`).
2. In the Configuration tab, fill in the connection and run **Test connection**.
3. Go to Migration tab -> **Migrate data from SQLite to PostgreSQL**.
4. Confirm the `jellyfin.db` path (auto-detected if it exists in the data directory).
5. Adjust batch size (default 1000) and optionally enable **Truncate tables before insert**.
6. Start migration and wait for 100% progress.
7. Review copied rows and activate PostgreSQL.

### When to use --truncate

- Recommended when repeating a migration over an already populated PostgreSQL database.
- Recommended on retries to avoid duplicates or stale rows.
- Destructive on existing destination data (TRUNCATE + RESTART IDENTITY + CASCADE).

## How to activate PostgreSQL

From plugin UI:

1. Set connection string and timeout.
2. Save settings.
3. Click Activate PostgreSQL.
4. Confirm restart.

Expected result:

- `database.xml` is configured as `PLUGIN_PROVIDER`.
- Jellyfin starts with `Jellyfin.Database.Providers.Postgres.dll`.

## How to rollback to SQLite

From plugin UI:

1. Click Deactivate PostgreSQL / Revert to SQLite.
2. Plugin removes `database.xml`.
3. Restart Jellyfin.

When rollback makes sense:

- PostgreSQL connectivity problem.
- Maintenance window or incomplete migration.
- You need fast service recovery while fixing PG.

## Backup and restore

In plugin Configuration:

1. Set default backup directory.
2. Optional on Windows: set `pg_dump` and `psql` paths if not in PATH.
3. Set default compression.

In plugin Maintenance:

1. Run manual backup (`.sql` or `.zip`).
2. Run restore from backup.
3. Optionally schedule recurring backups in Jellyfin Scheduled Tasks.

## Scheduled maintenance tasks

Also available in Jellyfin Scheduled Tasks:

| Task | Default | Description |
|---|---|---|
| PostgreSQL Backup | Daily 02:00 | Backup with pg_dump |
| PostgreSQL VACUUM ANALYZE | Sunday 03:00 | Free space and update statistics |
| PostgreSQL REINDEX DATABASE | Sunday 04:00 | Rebuild all indexes |
| Optimize GIN Indexes | Sunday 05:00 | Keep trigram GIN indexes fresh (v1.0.0.1) |

Operational recommendations:

- Schedule REINDEX off-peak (can take several minutes on large databases).
- Avoid overlapping backup, vacuum, and reindex at the same time.
- If the database has heavy write activity, run Optimize GIN Indexes before the weekly peak.

## Post-migration checklist

- Login works with existing users.
- Playback progress/activity continues to update.
- Key row counts validated (UserData, Users, TypedBaseItems).
- No PostgreSQL connection errors in Jellyfin logs.
- Manual backup tested at least once.

## Short FAQ

Q: I installed the plugin but data was not migrated.

A: Expected. Use the Migration tab (SQLite → PostgreSQL) to copy data, then activate PostgreSQL.

Q: Can I edit `database.xml` manually.

A: Yes, but the plugin UI is recommended to reduce configuration mistakes.

Q: Migration fails or gives odd results on retries.

A: On large databases or repeated migrations, enable --truncate to reset destination tables before inserting.

Q: What happens if I export to SQLite and the `.db` file already exists?

A: The plugin preserves the table structure and replaces only the data (DELETE + INSERT). The file is not dropped or recreated.

Q: Does InstantSearch require any client changes?

A: No. It is a server-side API endpoint. The plugin UI uses it internally when PostgreSQL is active and GIN indexes exist.

## Community

Questions or feedback? Open an issue or join the Jellyfin community:

[![Discord](https://img.shields.io/badge/Discord-Join_the_community-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=for-the-badge&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)
