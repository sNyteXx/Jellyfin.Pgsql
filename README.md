# The Unofficial Postgre SQL adapter for Jellyfin Server

This plugin adds postgres SQL support to [Jellyfin Server](https://github.com/jellyfin/jellyfin).


> [!IMPORTANT]
> Pleae note that there are several additional steps required to make this work and it is to be considered __HIGHLY__ experimental.

# How to use it

You can use your existing Jellyfin compose file and change the image accordingly to: `ghcr.io/jpvenson/jellyfin.pgsql:10.11.10-1`.

You need to add the connection parameters as enviornment variables in your compose file:

```yaml

services:
  jellyfin:
    image: ghcr.io/jpvenson/jellyfin.pgsql:10.11.10-1
    volumes:
        - /path/to/config:/config
        - /path/to/cache:/cache
        - /path/to/media:/media
    environment:
        - POSTGRES_HOST=
        - POSTGRES_PORT=
        - POSTGRES_DB=jellyfin
        - POSTGRES_USER=jellyfin
        - POSTGRES_PASSWORD=jellyfin
      # Optional settings bellow, uncomment if you want to connect using SSL
      # - POSTGRES_SSLMODE=Require
      # - POSTGRES_TRUSTSERVERCERTIFICATE=true
```

# Build

Checkout the Jellyfin submodule.
Use dotnet build to build the plugin.
Place the plugin in the `plugins` folder of the Jellyfin app.
Update the `database.xml` file to switch to the plugin as its database provider:

```xml
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <CustomProviderOptions>
    <PluginAssembly>../../../Jellyfin.Plugin.Pgsql/bin/debug/net9.0/Jellyfin.Plugin.Pgsql.dll</PluginAssembly>
    <PluginName>PostgreSQL</PluginName>
    <ConnectionString>CONNECTION_STRING_TO_LOCAL_PGSQL_SERVER</ConnectionString>
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>

```

Launch your Jellyfin server.

# Add migration
Run `dotnet ef migrations add {MIGRATION_NAME} --project "/workspaces/Jellyfin.Pgsql/Jellyfin.Plugin.Pgsql" -- --migration-provider Jellyfin-PgSql`

# Release flow

To create a new release, first sync all Jellyfin server changes then create a new migration as seen above. After that create a new efbundle:
`dotnet ef migrations bundle -o docker/jellyfin.PgsqlMigrator.dll -r linux-x64 --self-contained --project "/workspaces/Jellyfin.Pgsql/Jellyfin.Plugin.Pgsql" --  --migration-provider Jellyfin-PgSql`
Then build the container.

# Upgrade precautions for existing PostgreSQL installs

Before you deploy an image or plugin build based on Jellyfin 10.11.10, take a full PostgreSQL backup. At minimum, back up the `Users` table and the `__EFMigrationsHistory` table because the upstream 10.11.10 user changes add and populate `NormalizedUsername` and then enforce a new unique index on it.

Recommended sequence:

1. Stop Jellyfin so there are no concurrent writes while preparing the upgrade.
2. Run a full backup, for example `pg_dump --clean --if-exists --file jellyfin-pre-10.11.10.sql "$POSTGRES_DB"`.
3. Run targeted safety backups as well, for example `CREATE TABLE "Users_Backup_Pre_10_11_10" AS TABLE "Users";` and `CREATE TABLE "__EFMigrationsHistory_Backup_Pre_10_11_10" AS TABLE "__EFMigrationsHistory";`.
4. Check for case-insensitive username duplicates before applying the upgrade:
   `SELECT UPPER("Username"), COUNT(*) FROM "Users" GROUP BY UPPER("Username") HAVING COUNT(*) > 1;`
5. Only continue with the upgrade if the duplicate check returns no rows.
6. Start Jellyfin with the upgraded plugin/image and verify that the migration completed successfully.
7. Keep the backup tables and dump until login, user rename, and startup flows were validated.

# Migration Instructions (ADVANCED, UNTESTED)

To migrate your existing Jellyfin instance to a custom database (not using the docker image) follow the steps IN THIS ORDER.

1. Download the Jellyfin PGSQL container and configure it to point to an existing empty database and empty config directory. DO NOT USE YOUR EXISTING DATA OR SQLITE LIBRARY CONFIGURE A FULLY CLEAR INSTANCE.
2. Run Jellyfin once with it configured to your empty database, this will seed the database and its migration history.
3. Stop your Jellyfin instance after it has been started once (no need to fully configure it via the setup wizard). If you did not get the setup wizard then you did something wrong!
4. Install the pgloader tool `apt install pgloader` or see https://pgloader.readthedocs.io/en/latest/install.html.
5. Download the [jellyfindb.load](/docker/jellyfindb.load) file
6. Adapt the `jellyfindb.load` file accordingly to point towards your old jellyfin.db and your postgres instance. See https://pgloader.readthedocs.io/en/latest/ref/sqlite.html
7. Use the load file in `jellyfindb.load` to transfer your sqlite db into the postgres db like `pgloader /jellyfin-pgsql/jellyfindb.load`.
8. Move your old Data back to the Jellyfin directories
9. Start Jellyfin

If you get an error regarding a missing `__EFMigrationsHistory` you did not start Jellyfin with a clear state.
