# Release pipeline (internal)

This document is for repository maintainers only. It describes the GitHub Actions workflow used to build and publish the plugin.

## Workflow path

- `.github/workflows/publish-plugin.yml`

## Behavior

- Triggered on `push` to tags matching `v*` and manual dispatch.
- Uses `Jellyfin.Database.Providers.Postgres/Jellyfin.Database.Providers.Postgres.csproj` `<Version>` as source of truth.
- On tag builds, validates that tag `vX.Y.Z.W` matches csproj `<Version>` exactly.
- On manual runs, uses csproj `<Version>` (optional input `version` can be provided only as a validation check).
- Builds and publishes the project with `dotnet publish`.
- Packages a ZIP containing:
  - `Jellyfin.Database.Providers.Postgres.dll`
  - `Npgsql.dll`
  - `Npgsql.EntityFrameworkCore.PostgreSQL.dll`
- Regenerates `manifest.json` with resolved version, checksum, timestamp, source URL, and metadata read from `build.yaml` (guid/name/description/overview/owner/category/targetAbi).
- Commits the updated `manifest.json` back to `main`.
- Creates a GitHub Release (tag-triggered runs only) and uploads both assets:
  - `Jellyfin.Database.Providers.Postgres.zip`
  - `manifest.json`

## Recommended release flow

1. Update version in `Jellyfin.Database.Providers.Postgres/Jellyfin.Database.Providers.Postgres.csproj`:
  - `<Version>` (single source of truth)
2. Update `build.yaml` only when metadata changes (for example `targetAbi`, name, description, owner, category).
3. Commit and push to `main`.
4. Create and push matching tag:
  - `git tag vX.Y.Z.W`
  - `git push origin vX.Y.Z.W`
5. Workflow publishes ZIP + manifest and creates the GitHub Release.

If tag and csproj version do not match, the workflow fails intentionally to prevent inconsistent releases.
