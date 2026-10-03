#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
  echo "Build the macOS bundle on macOS with .NET 10 and Xcode Command Line Tools." >&2
  exit 1
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-osx-arm64}"
case "$rid" in
  osx-arm64|osx-x64) ;;
  *) echo "Expected osx-arm64 or osx-x64." >&2; exit 1 ;;
esac
output="$root/artifacts/$rid"
bundle="$output/Tropicast Station.app"

dotnet publish "$root/src/Tropicast.Station.App" -c Release -r "$rid" \
  --self-contained true -o "$output/publish"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
ditto "$output/publish" "$bundle/Contents/MacOS"
cp "$root/src/Tropicast.Station.App/Info.plist" "$bundle/Contents/Info.plist"
plutil -lint "$bundle/Contents/Info.plist"
codesign --force --deep --sign - "$bundle"
codesign --verify --deep --strict "$bundle"
echo "Built $bundle"
