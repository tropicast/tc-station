#!/usr/bin/env bash
# Builds a self-contained linux-x64 AppImage: artifacts/package/Tropicast-Station-<version>-x86_64.AppImage
# Requires the FFmpeg bundle (scripts/build-ffmpeg.sh linux-x64) and curl.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
version="${TC_VERSION:-0.1.0}"
rid=linux-x64
work="$root/artifacts/package/linux-x64"
appdir="$work/Tropicast-Station.AppDir"
out="$root/artifacts/package"
tool_version=1.9.0
tool_sha256=46fdd785094c7f6e545b61afcfb0f3d98d8eab243f644b4b17698c01d06083d1
tool="$work/appimagetool-x86_64.AppImage"

if [[ ! -x "$root/artifacts/ffmpeg/$rid/ffmpeg" ]]; then
  echo "Missing FFmpeg bundle: run scripts/build-ffmpeg.sh $rid first." >&2
  exit 1
fi

rm -rf "$appdir" "$work/publish"
mkdir -p "$appdir/usr/bin" "$out"
dotnet publish "$root/src/Tropicast.Station.App" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -o "$work/publish"
cp -a "$work/publish/." "$appdir/usr/bin/"
test -x "$appdir/usr/bin/ffmpeg/ffmpeg"
test -f "$appdir/usr/bin/ffmpeg/THIRD_PARTY_NOTICES"
cp "$root/THIRD_PARTY_NOTICES" "$appdir/usr/bin/THIRD_PARTY_NOTICES"

cp "$root/packaging/linux/tropicast-station.desktop" "$appdir/tropicast-station.desktop"
cp "$root/src/Tropicast.Station.App/Assets/tropicast-512.png" "$appdir/tropicast-station.png"
ln -sf tropicast-station.png "$appdir/.DirIcon"
cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/bin/Tropicast.Station" "$@"
EOF
chmod +x "$appdir/AppRun"
if command -v desktop-file-validate > /dev/null; then
  desktop-file-validate "$appdir/tropicast-station.desktop"
fi

if [[ ! -x "$tool" ]]; then
  curl --fail --location --silent --show-error \
    "https://github.com/AppImage/appimagetool/releases/download/$tool_version/appimagetool-x86_64.AppImage" -o "$tool"
  printf '%s  %s\n' "$tool_sha256" "$tool" | sha256sum --check
  chmod +x "$tool"
fi

image="$out/Tropicast-Station-$version-x86_64.AppImage"
rm -f "$image"
# --appimage-extract-and-run avoids needing FUSE (absent on CI runners and containers).
ARCH=x86_64 "$tool" --appimage-extract-and-run --no-appstream "$appdir" "$image"
chmod +x "$image"
echo "Built $image"
