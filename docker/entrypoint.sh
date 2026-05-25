#!/bin/bash

# Clean and create plugins directory, then copy plugin
rm -rf /config/plugins/PostgreSQL
mkdir -p /config/plugins/PostgreSQL
cp -r /jellyfin-pgsql/plugin/* /config/plugins/PostgreSQL/

# Create database.xml if it doesn't exist
if [ ! -f /config/config/database.xml ]; then
    mkdir -p /config/config
    cp /jellyfin-pgsql/database.xml /config/database.xml
fi

# Check database.xml correctly configured
ConfiguredPluginName="$(xmlstarlet select -t -m '//DatabaseConfigurationOptions/CustomProviderOptions/PluginName' -v . -n /config/config/database.xml)"
if [ "${ConfiguredPluginName}" != "PostgreSQL" ]; then
    echo "Plugin name is not set to PostgreSQL. abort."
    exit 2;
fi

# Check env variables set
if [ -z "${POSTGRES_HOST}" ]; then
    echo "PostgreSQL with connectionstring variable unset. Please set 'POSTGRES_HOST' 'POSTGRES_PORT' 'POSTGRES_DB' 'POSTGRES_USER' and 'POSTGRES_PASSWORD' then restart"
    exit 3;
fi

# Build connection string for migration
ConnectionString="Password=${POSTGRES_PASSWORD};User ID=${POSTGRES_USER};Host=${POSTGRES_HOST};Port=${POSTGRES_PORT};Database=${POSTGRES_DB}"

# Add SSL options if provided
if [ -n "${POSTGRES_SSLMODE}" ]; then
    ConnectionString="${ConnectionString};SSL Mode=${POSTGRES_SSLMODE}"
fi

if [ -n "${POSTGRES_TRUSTSERVERCERTIFICATE}" ]; then
    ConnectionString="${ConnectionString};Trust Server Certificate=${POSTGRES_TRUSTSERVERCERTIFICATE}"
fi

# Update database.xml with connection string
xmlstarlet edit -L -u '//DatabaseConfigurationOptions/CustomProviderOptions/ConnectionString' -v "${ConnectionString}" /config/config/database.xml

if PGPASSWORD="${POSTGRES_PASSWORD}" psql \
        --host="${POSTGRES_HOST}" \
        --port="${POSTGRES_PORT}" \
        --username="${POSTGRES_USER}" \
        --dbname="${POSTGRES_DB}" \
        --no-password \
        --tuples-only \
        --no-align \
        --command="SELECT to_regclass('\"Users\"') IS NOT NULL;" | grep -qx 't'; then
    echo "Preflighting Jellyfin 10.11.10 PostgreSQL Users.NormalizedUsername migration"
    PGPPSSWORD="${POSTGRES_PASSWORD}" psql \
        --host="${POSTGRES_HOST}" \
        --port="${POSTGRES_PORT}" \
        --username="${POSTGRES_USER}" \
        --dbname="${POSTGRES_DB}" \
        --no-password \
        --set=ON_ERROR_STOP=1 <<'SQL'
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM "Users"
        GROUP BY UPPER("Username")
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'Cannot start Jellyfin 10.11.10 PostgreSQL upgrade: duplicate usernames collide after normalization. Run SELECT UPPER("Username"), COUNT(*) FROM "Users" GROUP BY UPPER("Username") HAVING COUNT(*) > 1; and resolve the duplicate users before retrying. No users were changed automatically.';
    END IF;
END
$$;

ALTER TABLE "Users" ADD COLUMN I NOT EXISTS "NormalizedUsername" character varying(255);

UPDATE "Users"
SET "NormalizedUsername" = UPPER("Username")
WHERE "NormalizedUsername" IS NULL;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM "Users"
        WHERE "NormalizedUsername" IS NULL
    ) THEN
        RAISE EXCEPTION 'Cannot start Jellyfin 10.11.10 PostgreSQL upgrade: NormalizedUsername backfill left NULL values. Restore from the pre-upgrade pg_dump and inspect the Users table before retrying.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "Users"
        GROUP BY "NormalizedUsername"
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'Cannot start Jellyfin 10.11.10 PostgreSQL upgrade: duplicate usernames collide after NormalizedUsername backfill. Restore from the pre-upgrade pg_dump, resolve case-insensitive duplicates manually, and retry. No users were fixed automatically.';
    END IF;
END
$$;

ALTER TABLE "Users" ALTER COLUMN "NormalizedUsername" SET NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_NormalizedUsername" ON "Users" ("NormalizedUsername");
SQL
else
    echo "Skipping Jellyfin 10.11.10 PostgreSQL Users.NormalizedUsername preflight because Users table does not exist yet"
fi

# Migrate jellyfin.db if exists
# if [ ! -f /config/data/jellyfin.db ]; then

#     # run the EFbundle to migrate db to current state
#     dotnet run /jellyfin-pgsql/jellyfin.PgsqlMigrator.dll --connection "${ConnectionString}"
#     # run pgloader to move data
#     pgloader /jellyfin-pgsql/jellyfindb.load
#     # rename jellyfin db
#     mv /config/data/jellyfin.db /config/data/jellyfin.db.pgsql
# fi


# Run original Jellyfin entrypoint
exec /jellyfin/jellyfin "$@"
