#!/usr/bin/env bash
# Builds artifacts/package/Tropicast-Station-<version>-<rid>.dmg for osx-arm64 or osx-x64.
# Without TC_CODESIGN_IDENTITY the app and image are ad-hoc signed (not notarized, Gatekeeper will warn).
# With TC_CODESIGN_IDENTITY plus TC_NOTARY_APPLE_ID / TC_NOTARY_TEAM_ID / TC_NOTARY_PASSWORD
# (app-specific password) the app and image are notarized and stapled.
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
  echo "Package the macOS image on macOS." >&2
  exit 1
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-osx-arm64}"
version="${TC_VERSION:-0.1.0}"
identity="${TC_CODESIGN_IDENTITY:--}"
notarize=false
if [[ "$identity" != "-" && -n "${TC_NOTARY_APPLE_ID:-}" && -n "${TC_NOTARY_TEAM_ID:-}" && -n "${TC_NOTARY_PASSWORD:-}" ]]; then
  notarize=true
fi
bundle="$root/artifacts/$rid/Tropicast Station.app"
out="$root/artifacts/package"
stage="$root/artifacts/$rid/dmg"
image="$out/Tropicast-Station-$version-$rid.dmg"

submit() {
  xcrun notarytool submit "$1" --wait --timeout 30m \
    --apple-id "$TC_NOTARY_APPLE_ID" --team-id "$TC_NOTARY_TEAM_ID" --password "$TC_NOTARY_PASSWORD"
}

bash "$root/scripts/build-macos.sh" "$rid"
mkdir -p "$out"

if $notarize; then
  zip="$root/artifacts/$rid/notarize.zip"
  ditto -c -k --keepParent "$bundle" "$zip"
  submit "$zip"
  xcrun stapler staple "$bundle"
  xcrun stapler validate "$bundle"
fi

rm -rf "$stage" "$image"
mkdir -p "$stage"
ditto "$bundle" "$stage/Tropicast Station.app"
ln -s /Applications "$stage/Applications"
cp "$root/THIRD_PARTY_NOTICES" "$stage/THIRD_PARTY_NOTICES.txt"
hdiutil create -volname "Tropicast Station" -srcfolder "$stage" -ov -format UDZO "$image"
if [[ "$identity" != "-" ]]; then
  codesign --force --timestamp --sign "$identity" "$image"
else
  codesign --force --sign - "$image"
fi
codesign --verify --verbose=2 "$image"

if $notarize; then
  submit "$image"
  xcrun stapler staple "$image"
  xcrun stapler validate "$image"
  spctl --assess --type open --context context:primary-signature --verbose=2 "$image"
else
  echo "NOTE: $image is not notarized; macOS Gatekeeper will require manual approval." >&2
fi
echo "Built $image"
