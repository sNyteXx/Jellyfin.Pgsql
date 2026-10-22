# Unofficial PostgreSQL adapter for Jellyfin Server

This plugin adds PostgreSQL database support to [Jellyfin Server](https://github.com/jellyfin/jellyfin). This fork also contains the custom `/Items/Filtered` endpoint and compatibility fixes used by the existing PostgreSQL installation.

> [!IMPORTANT]
> This is an unofficial database provider. Keep a verified PostgreSQL backup, the matching Jellyfin `/config` directory, and the previous image digest before every server or provider upgrade.

## Current compatibility

| Component | Version |
| --- | --- |
| Jellyfin Server and official Web client | 12.2 |
| .NET / Entity Framework Core | 10 / 10.0.11 |
| PostgreSQL provider | Npgsql 10 |
| PostgreSQL server versions exercised by CI | 17 and 18 |
| PostgreSQL client tools included in the image | 18 |

The server is built from the pinned Jellyfin submodule with the tracked patches in `patches/server`. The official `jellyfin/jellyfin:12.2` image supplies the matching Web client and runtime assets. PostgreSQL client version 18 does not require upgrading an existing PostgreSQL 17 server.

### Preserved custom behavior

- `GET /Items/Filtered`: tag, inherited-tag and genre exclusion, item type and parent filters, sorting and pagination.
- PostgreSQL UUID `min`/`max` support and the existing handling of empty or detached owner references during UUID migration.
- The PostgreSQL readiness wait before starting Jellyfin and the username-normalization preflight.
- PostgreSQL-compatible rating-level and embedded-linked-child migrations, including the matching 12.2 indexes and model.
- Existing users, libraries, watch history, tags, media segments and plugin settings through the same database and `/config` mounts.

## Use the image

Keep the existing volumes, networking, device mappings and environment settings. Pin a tested image digest for deployment; the `12.2-sha-<commit>` tag identifies its source commit. Do not switch the database or config path while upgrading.

```yaml
services:
  jellyfin:
    image: ghcr.io/snytexx/jellyfin.pgsql@sha256:<verified-image-digest>
    volumes:
      - /path/to/config:/config
      - /path/to/cache:/cache
      - /path/to/media:/media
    environment:
      POSTGRES_HOST: postgres
      POSTGRES_PORT: 5432
      POSTGRES_DB: jellyfin
      POSTGRES_USER: jellyfin
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      # Optional SSL settings:
      # POSTGRES_SSLMODE: Require
      # POSTGRES_TRUSTSERVERCERTIFICATE: 'true'
```

At startup, the entrypoint installs the packaged PostgreSQL provider and updates `database.xml` from the environment. Other plugin folders and their configuration remain in `/config`.

## Build

From the repository root:

```bash
git submodule update --init --recursive
dotnet restore Jellyfin.Plugin.Pgsql.sln
dotnet build Jellyfin.Plugin.Pgsql.sln -c Release --no-restore
docker build --build-arg SOURCE_REVISION="$(git rev-parse HEAD)" \
  -f docker/Dockerfile -t jellyfin-pgsql:12.2-test .
```

The Docker build fails if any tracked server patch cannot be applied. `docker/package.sh [output.tar.gz]` packages the complete build context, including the pinned server source, all migrations, the custom API and server patches. Extract it and use `docker build -f docker/Dockerfile .`.

A standalone provider DLL does not include the server patches. Use the full custom image for this deployment.

## CI and publication

CI builds the .NET 10 provider and Docker image, then checks both PostgreSQL 17 and 18:

- Fresh Jellyfin 12.2 startup, migration history, partial indexes and UUID aggregates.
- JSON migration regressions covering invalid JSON, duplicate keys, nested values, Unicode/number limits and idempotence on both PostgreSQL versions.
- Upgrade from the known production Jellyfin 12.0 image, pinned as `ghcr.io/snytexx/jellyfin.pgsql@sha256:23551780f7799d386b197809192467af3885eb4cfad920a86cbfba9f4f7883ac`, using the same database and config volume.
- Preserved user identities, watch state, media segments and plugin configuration, with a seeded movie and tag.
- Authenticated calls to the custom `/Items/Filtered` endpoint, including tag exclusion.

Docker publication checks out submodules recursively. Pushing code changes to `upgrade/**` automatically publishes a candidate image with only its `12.2-sha-<commit>` tag and a source-revision label. The same tag restriction applies to manually dispatched candidate builds. Only builds from `master` move `12.2` and `latest`. Deployment should use the verified image digest even when an alias is available.

## Upgrade an existing 12.0 PostgreSQL installation

The database and `/config` form one rollback unit. First rehearse the upgrade using restored copies of both, an isolated PostgreSQL database and a separate test container. Keep the test container away from the production database and writable media paths.

For the actual upgrade:

1. Record the current image digest and container settings. Disable automatic image updates for the upgrade period.
2. Stop Jellyfin so that the database and config backups describe the same state.
3. Create a PostgreSQL custom-format backup using a client at least as new as the database server:

   ```bash
   backup_file="jellyfin-pre-12.2-$(date -u +%Y%m%d%H%M%S).dump"
   PGPASSWORD="$POSTGRES_PASSWORD" pg_dump \
     --host="$POSTGRES_HOST" --port="$POSTGRES_PORT" \
     --username="$POSTGRES_USER" --dbname="$POSTGRES_DB" \
     --format=custom --file="$backup_file"
   pg_restore --list "$backup_file" >/dev/null
   ```

4. Back up the complete existing `/config` directory, including plugin binaries/settings, metadata, server identity and data-protection keys. Preserve file ownership and permissions. Save the container template or Compose configuration as well.
5. Verify the backup by restoring it into a separate empty test database. A successful `pg_restore --list` alone only checks that the archive can be read.
6. Select the verified 12.2 custom-image digest in the existing container definition. Retain the same PostgreSQL connection, config/cache/media mounts, network and hardware-transcoding devices.
7. Start Jellyfin and wait for migrations and startup to finish. Confirm that the server reports 12.2.0 and loads the PostgreSQL provider.
8. Verify login, the expected users and libraries, representative favorites/watch positions, tags, intro segments, direct play/transcoding, and the custom filter endpoint. Check plugin status; a plugin previously disabled as incompatible may need enabling after the server upgrade.

Jellyfin's own migrations can normalize conflicting watch-history rows. Validate representative playback state and favorites in addition to row counts. Keep the pre-upgrade dump and matching config backup until the installation has passed these checks.

Useful database checks:

```sql
SELECT COUNT(*) FROM "Users";
SELECT COUNT(*) FROM "Users" WHERE "NormalizedUsername" IS NULL;
SELECT COUNT(*) FROM "UserData";
SELECT COUNT(*) FROM "MediaSegments";
SELECT indexname, indexdef
FROM pg_indexes
WHERE tablename = 'BaseItems'
  AND indexname IN ('IX_BaseItems_OwnerId', 'IX_BaseItems_ExtraType_OwnerId');
SELECT "MigrationId", "ProductVersion"
FROM "__EFMigrationsHistory"
ORDER BY "MigrationId" DESC
LIMIT 10;
```

For installations older than the existing 10.11.10 migration, first check case-insensitive username collisions with `SELECT UPPER("Username"), COUNT(*) FROM "Users" GROUP BY UPPER("Username") HAVING COUNT(*) > 1;`. Resolve collisions before upgrading; the entrypoint does not rename users automatically.

### Rollback

Stop Jellyfin, restore the complete pre-upgrade PostgreSQL dump into a clean database, restore its matching `/config` backup, and select the previous known-good image digest before restarting. Never start the old server against an already migrated 12.2 database. Retain a separate copy of the failed upgrade state if it is needed for diagnosis.

The provider also makes a PostgreSQL backup before internal database migrations and attempts a restore if a migration fails. Keep an external, verified backup for recovery of the complete database/config pair.

## Unraid deployment

The existing installation uses the `jellyfin-pgsql` container and `/boot/config/plugins/dockerMan/templates-user/my-jellyfin-pgsql.xml`. Update the image reference in that template while retaining the existing appdata bind mount and all ports, environment variables, GPU devices, extra parameters and media mounts. Save the previous template with the backup so rollback also restores the original container settings.

## Development migrations

```bash
dotnet ef migrations add MIGRATION_NAME \
  --project Jellyfin.Plugin.Pgsql \
  -- --migration-provider Jellyfin-PgSql
```

Keep the PostgreSQL model snapshot in sync with the pinned server model. Server migrations that contain SQLite-specific SQL need an explicit PostgreSQL path and regression coverage before updating the image.

## Migration from SQLite (advanced / experimental)

Initialize a separate empty PostgreSQL database with this image, let Jellyfin create its schema and migration history, then stop it. Adapt [`docker/jellyfindb.load`](docker/jellyfindb.load) to the old SQLite database and the test PostgreSQL target before running `pgloader`. Restore the remaining Jellyfin config/data files as appropriate and validate the result on the test instance. Keep the original SQLite database and config backup available throughout this separate migration.
