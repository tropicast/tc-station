#!/usr/bin/env bash
# Builds "Tropicast Station.app" for osx-arm64 or osx-x64.
# TC_VERSION            marketing/build version (default 0.1.0)
# TC_CODESIGN_IDENTITY  "Developer ID Application: ..." identity; default "-" (ad-hoc, local/CI only)
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
version="${TC_VERSION:-0.1.0}"
identity="${TC_CODESIGN_IDENTITY:--}"
output="$root/artifacts/$rid"
bundle="$output/Tropicast Station.app"
entitlements="$root/packaging/macos/entitlements.plist"

rm -rf "$output"
dotnet publish "$root/src/Tropicast.Station.App" -c Release -r "$rid" \
  --self-contained true -p:Version="$version" -o "$output/publish"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
ditto "$output/publish" "$bundle/Contents/MacOS"
cp "$root/src/Tropicast.Station.App/Info.plist" "$bundle/Contents/Info.plist"
plutil -replace CFBundleShortVersionString -string "$version" "$bundle/Contents/Info.plist"
plutil -replace CFBundleVersion -string "$version" "$bundle/Contents/Info.plist"
plutil -replace CFBundleIconFile -string tropicast "$bundle/Contents/Info.plist"
plutil -lint "$bundle/Contents/Info.plist"

iconset="$output/tropicast.iconset"
mkdir -p "$iconset"
icon="$root/src/Tropicast.Station.App/Assets/tropicast-512.png"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$icon" --out "$iconset/icon_${size}x${size}.png" > /dev/null
done
for size in 16 32 128 256; do
  sips -z $((size * 2)) $((size * 2)) "$icon" --out "$iconset/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil -c icns "$iconset" -o "$bundle/Contents/Resources/tropicast.icns"

# Sign nested code first (never --deep): every Mach-O file and managed assembly, then the bundle.
if [[ "$identity" == "-" ]]; then
  sign=(codesign --force --sign -)
else
  sign=(codesign --force --timestamp --options runtime --sign "$identity")
fi
while IFS= read -r -d '' file; do
  # Managed assemblies are nested code to codesign even though they are not Mach-O files.
  if [[ "$file" == *.dll ]] || file -b "$file" | grep -q 'Mach-O'; then
    if [[ "$file" == "$bundle/Contents/MacOS/Tropicast.Station" ]]; then
      continue
    fi
    "${sign[@]}" "$file"
  fi
done < <(find "$bundle/Contents" -type f -print0)
"${sign[@]}" --entitlements "$entitlements" "$bundle/Contents/MacOS/Tropicast.Station"
"${sign[@]}" --entitlements "$entitlements" "$bundle"
codesign --verify --deep --strict --verbose=2 "$bundle"
echo "Built $bundle (identity: $identity)"
