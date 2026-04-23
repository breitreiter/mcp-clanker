#!/usr/bin/env bash
# Build the clanker sandbox image + ensure the shared nuget cache volume
# exists. Run once at install, or whenever the Dockerfile changes.
#
# Usage:
#   ./sandbox/build.sh                  # builds clanker-sandbox:latest
#   ./sandbox/build.sh my-image:tag     # custom tag

set -euo pipefail

IMAGE_TAG="${1:-clanker-sandbox:latest}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "[clanker-sandbox] building image $IMAGE_TAG from $HERE/Dockerfile"
docker build -t "$IMAGE_TAG" "$HERE"

if ! docker volume inspect clanker-nuget >/dev/null 2>&1; then
    echo "[clanker-sandbox] creating docker volume clanker-nuget"
    docker volume create clanker-nuget
else
    echo "[clanker-sandbox] docker volume clanker-nuget already exists"
fi

echo "[clanker-sandbox] done."
echo "  image:  $IMAGE_TAG"
echo "  volume: clanker-nuget"
echo ""
echo "Configure clanker to use the sandbox by setting Sandbox.Mode=\"Docker\""
echo "in appsettings.json (see appsettings.example.json)."
