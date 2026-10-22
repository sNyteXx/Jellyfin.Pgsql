#!/usr/bin/env bash
# Package a complete build context without copying over or deleting source files.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
output="${1:-$script_dir/jellyfin-pgsql-plugin.tar.gz}"

if [ ! -f "$repo_root/jellyfin/Jellyfin.Server/Jellyfin.Server.csproj" ]; then
    echo 'Initialize the pinned server first: git submodule update --init --recursive' >&2
    exit 1
fi
if [ "$#" -gt 1 ]; then
    echo "Usage: $0 [output.tar.gz]" >&2
    exit 1
fi

# Build outside the source tree so an existing archive cannot include itself.
archive="$(mktemp "${TMPDIR:-/tmp}/jellyfin-pgsql-package.XXXXXXXX.tar.gz")"
trap 'rm -f -- "$archive"' EXIT

tar --create --gzip --file "$archive" \
    --directory "$repo_root" \
    --exclude='.git' --exclude='bin' --exclude='obj' \
    --exclude='*.tar.gz' --owner=0 --group=0 --numeric-owner \
    jellyfin.ruleset Jellyfin.Plugin.Pgsql.sln build.yaml \
    Jellyfin.Plugin.Pgsql jellyfin patches docker

mkdir -p -- "$(dirname -- "$output")"
mv -- "$archive" "$output"
printf 'Package created: %s\nBuild after extracting: docker build -f docker/Dockerfile -t jellyfin-pgsql:12.2 .\n' "$output"
