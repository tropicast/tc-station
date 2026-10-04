#!/usr/bin/env bash
# Builds the Tropicast Icecast image and runs the end-to-end suite against disposable containers.
# Needs Docker and the FFmpeg bundle for this platform (scripts/build-ffmpeg.sh linux-x64).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${TC_E2E_IMAGE:-tropicast-station-e2e:local}"

if [[ -z "${TC_E2E_IMAGE:-}" ]]; then
  docker build -t "$image" "$root/tests/Tropicast.Station.E2E.Tests/docker"
fi

TC_E2E_IMAGE="$image" TC_E2E_REQUIRED=1 \
  dotnet test "$root/tests/Tropicast.Station.E2E.Tests" -c Release "$@"
