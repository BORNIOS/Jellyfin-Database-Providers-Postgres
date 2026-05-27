# PostgreSQL Solution for Jellyfin

Production-ready PostgreSQL backend solution for Jellyfin through PLUGIN_PROVIDER.

[![Jellyfin 10.11.x](https://img.shields.io/badge/Jellyfin-10.11.x-blue?style=flat-square)](https://jellyfin.org)
[![Release](https://img.shields.io/github/v/release/BORNIOS/Jellyfin-Database-Providers-Postgres?style=flat-square)](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/releases/latest)

Language: [Español](README.md) | [English](README.en.md)

---

## Executive Summary

This repository delivers a concrete infrastructure solution:

- Replace SQLite with PostgreSQL in Jellyfin.
- Keep Jellyfin core untouched.
- Deploy and operate with a repository manifest that Jellyfin can consume directly.

## Why this solution

- Better operational scalability for larger libraries and concurrent workloads.
- Built-in backup and restore from the plugin dashboard using pg_dump/psql.
- Predictable release path using signed release assets and manifest-driven install.

## Compatibility

| Component | Version |
| --- | --- |
| Jellyfin | 10.11.10 |
| .NET runtime in host | 9.0.x |
| EF Core | 9.0.x |
| Npgsql EF provider | 9.0.4 |
| PostgreSQL | 13+ (recommended 16/17) |

## Install from Manifest (recommended)

This is the official and supported install path.

1. Open Jellyfin Admin.
1. Go to Plugins -> Plugin Repositories.
1. Add a new repository with this URL:

```text
https://raw.githubusercontent.com/BORNIOS/Jellyfin-Database-Providers-Postgres/main/manifest.json
```

1. Save, refresh plugin catalog, and install PostgreSQL Database Provider.

## Manual install from release ZIP

1. Download latest ZIP from releases.
1. Extract plugin files into your Jellyfin plugins directory.
1. Restart Jellyfin.
1. Configure the plugin from the Jellyfin dashboard.

## Manual database.xml example

```xml
<DatabaseConfigurationOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <LockingBehavior>NoLock</LockingBehavior>
  <CustomProviderOptions>
    <PluginName>Jellyfin.Database.Providers.Postgres</PluginName>
    <PluginAssembly>Jellyfin.Database.Providers.Postgres.dll</PluginAssembly>
    <ConnectionString>Host=127.0.0.1;Port=5432;Database=jellyfin;Username=jellyfin;Password=CHANGE_ME;Pooling=true;Maximum Pool Size=200</ConnectionString>
    <Options>
      <CustomDatabaseOption>
        <Key>command-timeout</Key>
        <Value>60</Value>
      </CustomDatabaseOption>
      <CustomDatabaseOption>
        <Key>EnableSensitiveDataLogging</Key>
        <Value>False</Value>
      </CustomDatabaseOption>
    </Options>
  </CustomProviderOptions>
</DatabaseConfigurationOptions>
```

## Release model

- Single source of truth for client installation: manifest.json.
- CI builds ZIP, regenerates manifest with checksum and source URL, and publishes both in release assets.

## Operational notes

- Migration from SQLite is not automatic.
- Use Jellyfin.SqliteToPostgres.Migrator before switching.
- The plugin can create backups and restore .sql/.zip files from the Maintenance tab.
- You can also schedule backup runs through Jellyfin Scheduled Tasks.

## Backup and restore in the plugin

1. Open the Configuration tab and save these values first:
1. Default backup directory.
1. pg_dump path (optional, recommended on Windows when not in PATH).
1. psql path (optional, recommended on Windows when not in PATH).
1. Default ZIP compression behavior.
1. Save configuration.
1. Go to Maintenance to run manual backup or restore a `.sql` / `.zip` backup.

Notes:
- Configuration is the single place for binary paths (pg_dump/psql) and default backup directory.
- Maintenance is task execution only, without duplicated path fields.
- Scheduled tasks use the persisted configuration (including binary paths).
- If binary paths are empty, the plugin resolves `pg_dump` and `psql` from system PATH.
