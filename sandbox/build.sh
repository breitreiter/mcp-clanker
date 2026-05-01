#!/usr/bin/env bash
# Build the imp sandbox image + ensure the shared nuget cache volume
# exists. Run once at install, or whenever the Dockerfile changes.
#
# Usage:
#   ./sandbox/build.sh                  # builds imp-sandbox:latest
#   ./sandbox/build.sh my-image:tag     # custom tag

set -euo pipefail

IMAGE_TAG="${1:-imp-sandbox:latest}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "[imp-sandbox] building image $IMAGE_TAG from $HERE/Dockerfile"
docker build -t "$IMAGE_TAG" "$HERE"

if ! docker volume inspect imp-nuget >/dev/null 2>&1; then
    echo "[imp-sandbox] creating docker volume imp-nuget"
    docker volume create imp-nuget
else
    echo "[imp-sandbox] docker volume imp-nuget already exists"
fi

echo "[imp-sandbox] done."
echo "  image:  $IMAGE_TAG"
echo "  volume: imp-nuget"
echo ""
echo "Configure imp to use the sandbox by setting Sandbox.Mode=\"Docker\""
echo "in appsettings.json (see appsettings.example.json)."
