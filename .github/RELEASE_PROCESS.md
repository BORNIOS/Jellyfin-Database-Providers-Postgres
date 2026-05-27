# Release pipeline (internal)

This document is for repository maintainers only. It describes the GitHub Actions workflow used to build and publish the plugin.

## Workflow path

- `.github/workflows/publish-plugin.yml`

## Behavior

- Triggered on `push` to tags matching `v*` and manual dispatch.
- Determines the plugin version from the Git tag, or from the required `workflow_dispatch` input `version`.
- Builds and publishes the project with `dotnet publish`.
- Packages a ZIP containing:
  - `Jellyfin.Database.Providers.Postgres.dll`
  - `Npgsql.dll`
  - `Npgsql.EntityFrameworkCore.PostgreSQL.dll`
- Regenerates `manifest.json` with resolved version, checksum, timestamp, and source URL.
- Commits the updated `manifest.json` back to `main`.
- Creates a GitHub Release (tag-triggered runs only) and uploads both assets:
  - `Jellyfin.Database.Providers.Postgres.zip`
  - `manifest.json`
