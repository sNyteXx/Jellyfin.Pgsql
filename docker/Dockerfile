# Build stage
# Context hint: project root ../
# sudo docker build -t ghcr.io/snytexx/jellyfin.pgsql:12.0-test -f ./Dockerfile ../
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY jellyfin.ruleset .
COPY Jellyfin.Plugin.Pgsql.sln .
COPY build.yaml .
COPY Jellyfin.Plugin.Pgsql/ ./Jellyfin.Plugin.Pgsql/
COPY jellyfin/ ./jellyfin/
COPY patches/server/0001-Fix-PostgreSQL-MigrateRatingLevels-active-reader.patch /tmp/jellyfin-rating-levels.patch

# Apply the single PostgreSQL compatibility fix to the exact Jellyfin 12.0
# submodule revision. No unrelated Jellyfin server customizations are carried.
RUN apt-get update && \
    apt-get install -y patch && \
    cd /src/jellyfin && \
    patch -p1 < /tmp/jellyfin-rating-levels.patch && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Restore and publish the PostgreSQL plugin and patched Jellyfin server.
RUN dotnet restore Jellyfin.Plugin.Pgsql.sln
RUN dotnet restore jellyfin/Jellyfin.Server/Jellyfin.Server.csproj -r linux-x64 -p:AllowMissingPrunePackageData=true
RUN dotnet publish Jellyfin.Plugin.Pgsql.sln -c Release --no-restore -o /app/publish
RUN dotnet publish jellyfin/Jellyfin.Server/Jellyfin.Server.csproj -c Release --no-restore -o /app/jellyfin-server -r linux-x64 --self-contained true -p:AllowMissingPrunePackageData=true

# Final stage - official Jellyfin 12.0 image with the one patched server build
# and the PostgreSQL database provider layered on top.
FROM jellyfin/jellyfin:12.0

# Install PostgreSQL 18 client tools for backup/restore functionality
RUN apt-get update && \
    apt-get install -y wget ca-certificates gnupg lsb-release && \
    wget --quiet -O - https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /usr/share/keyrings/postgresql-keyring.gpg && \
    echo "deb [signed-by=/usr/share/keyrings/postgresql-keyring.gpg] http://apt.postgresql.org/pub/repos/apt/ $(lsb_release -cs)-pgdg main" > /etc/apt/sources.list.d/pgdg.list && \
    apt-get update && \
    apt-get install -y postgresql-client-18 xmlstarlet && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Replace the server files with the build from the exact v12.0 submodule plus
# the PostgreSQL migration compatibility patch. The official web client remains.
COPY --from=build /app/jellyfin-server/ /jellyfin/

# Copy the published plugin and config files
COPY --from=build /app/publish/ /jellyfin-pgsql/plugin/
COPY docker/entrypoint.sh /entrypoint.sh
COPY docker/database.xml /jellyfin-pgsql/database.xml
# COPY docker/jellyfindb.load /jellyfin-pgsql/jellyfindb.load
# COPY docker/jellyfin.PgsqlMigrator.dll /jellyfin-pgsql/jellyfin.PgsqlMigrator.dll

# Make entrypoint executable
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
