# Release plan: 2.0.1 and 3.0.0

How to publish the two versions that carry the move to Jellyfin 12:
**2.0.1** (for Jellyfin 10.11.x) and **3.0.0** (for Jellyfin 12.1.x).

> Spanish version (primary): [`plan-de-publicacion.md`](plan-de-publicacion.md)

---

## 1. Why 2.0.1 must ship before 3.0.0

Jellyfin's catalog **serves a single `manifest.json`**: the one on `main`. Every manifest entry
declares its `targetAbi`, and the client downloads the **highest compatible** version for the
user's server.

Direct consequences:

1. A user on **Jellyfin 10.11.x** can only see and install entries with `targetAbi 10.11.*`.
   If 2.0.1 is missing from the manifest, that user stays on 2.0.0.0.
2. That user **needs 2.0.1** to export PostgreSQL to SQLite without hitting the primary-key
   letter-case error (see [`migration-10.11-to-12.en.md`](migration-10.11-to-12.en.md),
   section 2).
3. Without an export there is no bridge to Jellyfin 12: these are **two releases with different
   purposes**, not two attempts at the same thing.

```
v2.0.1  → rescues 10.11 users (reliable export to SQLite)
v3.0.0  → enables Jellyfin 12.1 with the new provider contract
```

---

## 2. What was fixed in the release flow

`release.yml` used to update the `manifest.json` **of the branch it ran on**. Releasing from
`release/2.0.1` therefore left the entry on that branch and it **never reached the catalog**
(which is read from `main`): the release existed, but nobody could see it.

The flow now **replicates the same entry into `main`** right after updating the release branch,
using a temporary worktree and a bot commit. The improvement is applied on:

- branch `release/V3` (publishes 3.0.0 with `targetAbi 12.1.0.0`), and
- branch `release/2.0.1` (publishes 2.0.1 with `targetAbi 10.11.0.0`).

The step is idempotent: if `main` already carries that version, it exits without committing.

---

## 3. Before releasing

```bash
# 1. Both branches build with no warnings
git switch release/2.0.1   && dotnet build Jellyfin.Database.Providers.Postgres.sln -c Release
git switch release/V3      && dotnet build Jellyfin.Database.Providers.Postgres.sln -c Release

# 2. The 3.0.0 suite is green (68 tests)
$env:POSTGRES_TEST_CONNECTION = 'Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=***'
./Tools/Generar-ResumenEjecutivo.ps1
```

Also confirm `main` and `release/V3` are aligned for the plugin (the manifest updates itself;
never edit it by hand).

---

## 4. Publish 2.0.1 (first)

**GitHub → Actions → 📦 Release Plugin → Run workflow**

| Input | Value |
|---|---|
| `release_version` | `2.0.1` |
| `changelog` | see below |
| `prerelease` | `false` |
| Branch | `release/2.0.1` |

Suggested changelog:

```
2.0.1 — Critical PostgreSQL -> SQLite export fix

- Fix: primary-key columns are copied verbatim, so row pairs that differ only by letter
  case no longer collapse into the same key and the full export stops aborting.
- Fix: on a constraint violation the log reports table, row number and key values instead
  of the cryptic SQLite error.
- CI: the catalog entry is also published to main so Jellyfin 10.11.x can see this version.

Requires Jellyfin 10.11.x. Use 3.0.0 for Jellyfin 12.1.
```

Outcome: tag `v2.0.1`, ZIP `Jellyfin.Database.Providers.Postgres-2.0.1.0.zip`, manifest entry
`2.0.1.0` with `targetAbi 10.11.0.0` on `main`.

---

## 5. Publish 3.0.0 (after)

**GitHub → Actions → 📦 Release Plugin → Run workflow**

| Input | Value |
|---|---|
| `release_version` | `3.0.0` |
| `changelog` | see below |
| `prerelease` | `false` |
| Branch | `release/V3` (or `main` once merged) |

Suggested changelog:

```
3.0.0 — Jellyfin 12.1

- New: full adaptation to the Jellyfin 12.1 database provider contract (requires .NET 10).
- Critical fix: PurgeDatabase implemented. On 2.x it threw NotImplementedException and broke
  restoration of the full system backup.
- New: read-only SQL console in the panel, with 10 diagnostic templates and history.
- New: working advanced options (pool size, prepared statements, timeout), validated and
  shown in the current status.
- New: xUnit test suite (68 tests) with an executive summary generated in CI.
- Better: dedicated plugin log with structured PostgresException detail.
- Better: more robust SQLite export and safe startup on already migrated servers.

Requires Jellyfin 12.1.x. Use 2.0.1 for Jellyfin 10.11.x.
```

Outcome: tag `v3.0.0`, ZIP `Jellyfin.Database.Providers.Postgres-3.0.0.0.zip`, manifest entry
`3.0.0.0` with `targetAbi 12.1.0.0` on `main`.

---

## 6. Post-release verification (mandatory)

```bash
# 6.a The served manifest holds both entries and their ABIs
curl -s https://raw.githubusercontent.com/BORNIOS/Jellyfin-Database-Providers-Postgres/main/manifest.json \
  | jq '.[0].versions[] | {version, targetAbi}'

# Expected:
# { "version": "2.0.1.0", "targetAbi": "10.11.0.0" }
# { "version": "3.0.0.0", "targetAbi": "12.1.0.0" }

# 6.b Manifest checksum matches the downloaded ZIP
curl -sL -o pgp-2.0.1.0.zip https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/releases/download/v2.0.1/Jellyfin.Database.Providers.Postgres-2.0.1.0.zip
md5sum pgp-2.0.1.0.zip     # must match the entry's checksum field

# 6.c The ZIP carries the three DLLs
unzip -l pgp-2.0.1.0.zip   # Jellyfin.Database.Providers.Postgres.dll, Npgsql.dll, Npgsql.EntityFrameworkCore.PostgreSQL.dll
```

Minimum functional test, on two clean installs:

1. **Jellyfin 10.11.11** → the catalog offers 2.0.1; install it and run an export to SQLite.
2. **Jellyfin 12.1.x** → the catalog offers 3.0.0; install it, import that SQLite file and
   activate PostgreSQL.

---

## 7. Risks and notes

- **Never reuse a published version**: the flow replaces the entry, but the old tag and ZIP are
  left orphaned. If you must fix something, publish 2.0.2 / 3.0.1.
- **Use `prerelease: true`** to validate a ZIP before exposing it; the manifest is updated
  anyway, so remove the entry if you decide not to publish it.
- **ZIPs are not interchangeable**: 2.0.1 carries `net9.0` DLLs, 3.0.0 carries `net10.0` DLLs.
- **The bot needs write access** (`permissions: contents: write`, already declared) to create
  the release and push the manifest.

## 8. Rollback

1. Delete the release and the tag on GitHub.
2. Revert the bot commit on `main` that added the manifest entry:
   `git revert <sha> -- manifest.json` and `git push origin main`.
3. On the server, uninstall the plugin and go back to the previous version.

---

## 9. Keep reading

- [Migration manual from 10.11.x to 12.x](migration-10.11-to-12.en.md)
- [What is new in 3.0.0 vs 2.0.1](whats-new-3.0.0.en.md)
