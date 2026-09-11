# Unofficial PostgreSQL adapter for Jellyfin Server

This plugin adds PostgreSQL database support to [Jellyfin Server](https://github.com/jellyfin/jellyfin).

> [!IMPORTANT]
> This is an unofficial and experimental database provider. Always keep a verified PostgreSQL backup before upgrading Jellyfin or this plugin.

## Current compatibility

- Jellyfin: 12.0
- .NET: 10
- Entity Framework Core: 10
- PostgreSQL client tooling in the image: 18
- PostgreSQL provider: Npgsql 10

## How to use it

Use the custom image in your existing Jellyfin compose file:

```yaml
services:
  jellyfin:
    image: ghcr.io/snytexx/jellyfin.pgsql:latest
    volumes:
      - /path/to/config:/config
      - /path/to/cache:/cache
      - /path/to/media:/media
    environment:
      - POSTGRES_HOST=
      - POSTGRES_PORT=5432
      - POSTGRES_DB=jellyfin
      - POSTGRES_USER=jellyfin
      - POSTGRES_PASSWORD=jellyfin
      # Optional SSL settings:
      # - POSTGRES_SSLMODE=Require
      # - POSTGRES_TRUSTSERVERCERTIFICATE=true
```

The image installs the PostgreSQL plugin into Jellyfin's plugin directory and configures `database.xml` from the environment variables at startup.

## Build

Checkout the Jellyfin submodule and build the plugin with .NET 10:

```bash
git submodule update --init --recursive
dotnet restore Jellyfin.Plugin.Pgsql.sln
dotnet build Jellyfin.Plugin.Pgsql.sln -c Release
```

For a manual plugin setup, configure Jellyfin to use the provider:

```xml
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <CustomProviderOptions>
    <PluginAssembly>../../../Jellyfin.Plugin.Pgsql/bin/Debug/net10.0/Jellyfin.Plugin.Pgsql.dll</PluginAssembly>
    <PluginName>PostgreSQL</PluginName>
    <ConnectionString>CONNECTION_STRING_TO_LOCAL_PGSQL_SERVER</ConnectionString>
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>
```

## Add a migration

```bash
dotnet ef migrations add {MIGRATION_NAME} \
  --project "/workspaces/Jellyfin.Pgsql/Jellyfin.Plugin.Pgsql" \
  -- --migration-provider Jellyfin-PgSql
```

## Release flow

Sync the Jellyfin server submodule to the intended release, add the matching PostgreSQL migrations, validate the build and PostgreSQL startup path, then build/publish the container.

The repository CI validates:

- .NET 10 restore and Release build
- Docker image build on the official Jellyfin 12.0 image
- Fresh Jellyfin 12 startup on PostgreSQL 18
- PostgreSQL migration completion through the Jellyfin 12 migration set
- Upgrade from the repository's Jellyfin 10.11.10 PostgreSQL image to Jellyfin 12.0

## Upgrade precautions for existing PostgreSQL installs

Jellyfin 12 changes the database schema substantially. Treat the PostgreSQL database and the Jellyfin config directory as one rollback unit.

Recommended sequence:

1. Stop Jellyfin so there are no concurrent writes.
2. Disable automatic image updates until the upgrade has been validated.
3. Take a full PostgreSQL dump:

   ```bash
   PGPASSWORD="$POSTGRES_PASSWORD" pg_dump \
     --host="$POSTGRES_HOST" \
     --port="$POSTGRES_PORT" \
     --username="$POSTGRES_USER" \
     --dbname="$POSTGRES_DB" \
     --clean --if-exists \
     --file "jellyfin-pre-12.0-$(date -u +%Y%m%d%H%M%S).sql"
   ```

4. Back up the Jellyfin `/config` directory as well.
5. If upgrading from a version before the existing 10.11.10 PostgreSQL migration, check case-insensitive username collisions first:

   ```sql
   SELECT UPPER("Username"), COUNT(*)
   FROM "Users"
   GROUP BY UPPER("Username")
   HAVING COUNT(*) > 1;
   ```

6. Pull the Jellyfin 12 PostgreSQL image and start Jellyfin.
7. Verify successful startup, login, users, libraries, playback and plugin loading.
8. Keep the pre-upgrade dump and config backup until the installation has been validated.

Useful post-upgrade checks:

```sql
SELECT COUNT(*) FROM "Users";
SELECT COUNT(*) FROM "Users" WHERE "NormalizedUsername" IS NULL;
SELECT indexname, indexdef
FROM pg_indexes
WHERE tablename = 'Users'
  AND indexname = 'IX_Users_NormalizedUsername';
SELECT "MigrationId", "ProductVersion"
FROM "__EFMigrationsHistory"
ORDER BY "MigrationId" DESC
LIMIT 10;
```

### Rollback

Do not try to downgrade Jellyfin against a database that has already been migrated to the Jellyfin 12 schema.

1. Stop Jellyfin.
2. Restore the full pre-12 PostgreSQL dump.
3. Restore the matching pre-12 Jellyfin `/config` backup if required.
4. Select the previous known-good image digest/tag.
5. Start Jellyfin and validate the old installation.

The image's database provider also creates a PostgreSQL backup before Jellyfin applies internal database migrations and attempts an automatic restore if a migration fails. This is an additional safeguard, not a replacement for the external pre-upgrade backup.

## Unraid deployment

This repository is currently used with:

- Template: `/boot/config/plugins/dockerMan/templates-user/my-jellyfin-pgsql.xml`
- Container: `jellyfin-pgsql`
- Image: `ghcr.io/snytexx/jellyfin.pgsql:latest`
- Appdata: `/mnt/user/appdata/jellyfin-pgsql`
- PostgreSQL connection: `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`

## Migration from SQLite (advanced / experimental)

To migrate an existing SQLite-backed Jellyfin instance to PostgreSQL:

1. Start the Jellyfin PostgreSQL image against an empty PostgreSQL database and an empty config directory.
2. Let Jellyfin initialize the schema and migration history, then stop it.
3. Install `pgloader`.
4. Adapt [`docker/jellyfindb.load`](/docker/jellyfindb.load) to the old `jellyfin.db` and the PostgreSQL instance.
5. Run `pgloader /jellyfin-pgsql/jellyfindb.load`.
6. Restore the remaining Jellyfin data/config files as required.
7. Start Jellyfin and validate the migrated instance.

If `__EFMigrationsHistory` is missing, the PostgreSQL target was not initialized with a clean Jellyfin PostgreSQL instance first.
