# Build stage
# Run from the repository root after initializing its pinned submodules:
# docker build -t jellyfin-pgsql:12.2-test -f docker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY jellyfin.ruleset .
COPY Jellyfin.Plugin.Pgsql.sln .
COPY build.yaml .
COPY Jellyfin.Plugin.Pgsql/ ./Jellyfin.Plugin.Pgsql/
COPY jellyfin/ ./jellyfin/
COPY patches/server/ /tmp/jellyfin-server-patches/

# Apply all tracked PostgreSQL compatibility patches to the pinned server source.
# A patch that no longer applies must fail the build instead of being skipped.
RUN apt-get update && \
    apt-get install -y --no-install-recommends patch && \
    cd /src/jellyfin && \
    for server_patch in /tmp/jellyfin-server-patches/*.patch; do \
        patch --batch --fuzz=0 -p1 < "$server_patch" || exit 1; \
    done && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Restore and publish the PostgreSQL plugin and patched Jellyfin server.
RUN dotnet restore Jellyfin.Plugin.Pgsql.sln
RUN dotnet restore jellyfin/Jellyfin.Server/Jellyfin.Server.csproj -r linux-x64 -p:AllowMissingPrunePackageData=true
RUN dotnet publish Jellyfin.Plugin.Pgsql.sln -c Release --no-restore -o /app/publish
RUN dotnet publish jellyfin/Jellyfin.Server/Jellyfin.Server.csproj -c Release --no-restore -o /app/jellyfin-server -r linux-x64 --self-contained true -p:AllowMissingPrunePackageData=true

# Keep the official 12.2 web client and runtime, with the matching patched server
# and the custom PostgreSQL provider layered on top.
FROM jellyfin/jellyfin:12.2

ARG SOURCE_REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/sNyteXx/Jellyfin.Pgsql" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.version="12.2"

# Install PostgreSQL 18 client tools for backup/restore functionality
RUN apt-get update && \
    apt-get install -y --no-install-recommends wget ca-certificates gnupg lsb-release && \
    wget --quiet -O - https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /usr/share/keyrings/postgresql-keyring.gpg && \
    echo "deb [signed-by=/usr/share/keyrings/postgresql-keyring.gpg] https://apt.postgresql.org/pub/repos/apt/ $(lsb_release -cs)-pgdg main" > /etc/apt/sources.list.d/pgdg.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends postgresql-client-18 xmlstarlet && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Replace the server with the pinned v12.2 submodule plus our compatibility fixes.
COPY --from=build /app/jellyfin-server/ /jellyfin/

# Copy the published plugin and config files
COPY --from=build /app/publish/ /jellyfin-pgsql/plugin/
COPY docker/entrypoint.sh /entrypoint.sh
COPY docker/database.xml /jellyfin-pgsql/database.xml

# Make entrypoint executable
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
