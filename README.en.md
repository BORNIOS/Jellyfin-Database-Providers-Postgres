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
- Backup and restore from plugin UI (pg_dump/psql).
- Controlled switch between PostgreSQL and SQLite.

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

## How to migrate from SQLite to PostgreSQL

Installing the plugin does not migrate data by itself.

1. Create an empty PostgreSQL database (for example `jellyfin`).
2. Run SQLite -> PostgreSQL migration using Jellyfin.SqliteToPostgres.Migrator.
3. Validate key row counts (for example `UserData`, `Users`, `TypedBaseItems`).
4. In plugin settings, set your PostgreSQL connection string.
5. Activate PostgreSQL from the plugin UI (writes `database.xml` and restarts Jellyfin).

Example connection string:

```text
Host=127.0.0.1;Port=5432;Database=jellyfin;Username=jellyfin;Password=CHANGE_ME;Pooling=true;Maximum Pool Size=200
```

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

## Quick post-switch checks

- Login works with existing users.
- Playback progress/activity continues to update.
- No PostgreSQL connection errors in Jellyfin logs.

## Short FAQ

Q: I installed the plugin but data was not migrated.

A: Expected. First run SQLite -> PostgreSQL migrator, then activate PostgreSQL in plugin UI.

Q: Can I edit `database.xml` manually.

A: Yes, but plugin UI is recommended to reduce configuration mistakes.

Q: Migration fails or gives odd results on retries.

A: On large databases or repeated migrations, enable --truncate to reset destination tables before insert.

## Community

Questions or feedback? Open an issue or join the Jellyfin community:

[![Discord](https://img.shields.io/badge/Discord-Join_the_community-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=for-the-badge&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)
