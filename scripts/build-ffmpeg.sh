#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:?Specify linux-x64, linux-arm64, win-x64, osx-arm64 or osx-x64}"
case "$rid" in
  linux-x64|linux-arm64) tls=(--enable-openssl); suffix="" ;;
  win-x64) tls=(--enable-schannel); suffix=".exe" ;;
  osx-arm64|osx-x64) tls=(--enable-securetransport); suffix="" ;;
  *) echo "Unsupported RID: $rid" >&2; exit 1 ;;
esac
if [[ "$rid" == osx-* ]]; then
  export MACOSX_DEPLOYMENT_TARGET=13.0
fi
work="$root/artifacts/ffmpeg-build/$rid"
prefix="$work/prefix"
output="$root/artifacts/ffmpeg/$rid"
jobs="${TC_BUILD_JOBS:-4}"
mkdir -p "$work" "$prefix" "$output/sources" "$output/licenses"

download() {
  local url="$1" name="$2" hash="$3"
  if [[ ! -f "$work/$name" ]]; then
    curl --fail --location --silent --show-error "$url" -o "$work/$name"
  fi
  printf '%s  %s\n' "$hash" "$work/$name" | shasum -a 256 --check
  cp "$work/$name" "$output/sources/$name"
}
download https://ffmpeg.org/releases/ffmpeg-8.0.1.tar.xz ffmpeg-8.0.1.tar.xz \
  05ee0b03119b45c0bdb4df654b96802e909e0a752f72e4fe3794f487229e5a41
download https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz lame-3.100.tar.gz \
  ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e
tar -xf "$work/ffmpeg-8.0.1.tar.xz" -C "$work"
tar -xf "$work/lame-3.100.tar.gz" -C "$work"
host=()
arch=()
linkFlags="-L$prefix/lib"
case "$rid" in
  win-x64) host=(--host=x86_64-w64-mingw32); arch=(--target-os=mingw32 --disable-pthreads --enable-w32threads); linkFlags+=" -static -static-libgcc" ;;
  osx-arm64) export CC="clang -arch arm64"; host=(--host=aarch64-apple-darwin); arch=(--arch=aarch64 --enable-cross-compile --target-os=darwin) ;;
  osx-x64) export CC="clang -arch x86_64"; host=(--host=x86_64-apple-darwin); arch=(--arch=x86_64 --enable-cross-compile --target-os=darwin) ;;
esac
(
  cd "$work/lame-3.100"
  ./configure --prefix="$prefix" --disable-shared --enable-static --disable-frontend \
    --disable-dependency-tracking "${host[@]}"
  make -j"$jobs"
  make install
)
if [[ "$rid" == linux-* ]]; then
  download https://github.com/openssl/openssl/releases/download/openssl-3.5.4/openssl-3.5.4.tar.gz \
    openssl-3.5.4.tar.gz 967311f84955316969bdb1d8d4b983718ef42338639c621ec4c34fddef355e99
  tar -xf "$work/openssl-3.5.4.tar.gz" -C "$work"
  (
    cd "$work/openssl-3.5.4"
    ./config --prefix="$prefix" --libdir=lib no-shared no-tests no-module
    make -j"$jobs" build_libs
    make install_dev
  )
  cp "$work/openssl-3.5.4/LICENSE.txt" "$output/licenses/OpenSSL-APACHE-2.0.txt"
fi
(
  cd "$work/ffmpeg-8.0.1"
  export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
  ./configure --prefix="$prefix" --disable-autodetect --disable-everything \
    --disable-doc --disable-debug --disable-ffplay --disable-ffprobe --disable-x86asm \
    --enable-static --disable-shared --enable-version3 --enable-ffmpeg --enable-network \
    --enable-libmp3lame --enable-encoder=libmp3lame,pcm_s16le --enable-decoder=pcm_f32le,mp3 \
    --enable-demuxer=pcm_f32le,mp3 --enable-parser=mpegaudio --enable-muxer=mp3,wav \
    --enable-filter=aresample,aformat,anull --enable-protocol=pipe,file,tcp,udp,tls,http,https,icecast \
    --pkg-config-flags=--static --extra-cflags="-I$prefix/include" \
    --cc="${CC:-cc}" --extra-ldflags="$linkFlags" "${tls[@]}" "${arch[@]}"
  make -j"$jobs" ffmpeg
)
cp "$work/ffmpeg-8.0.1/ffmpeg$suffix" "$output/ffmpeg$suffix"
cp "$work/ffmpeg-8.0.1/COPYING.LGPLv3" "$output/licenses/FFmpeg-LGPL-3.0.txt"
cp "$work/ffmpeg-8.0.1/COPYING.GPLv3" "$output/licenses/GPL-3.0-referenced-by-LGPL.txt"
cp "$work/lame-3.100/COPYING" "$output/licenses/LAME-LGPL-2.0.txt"
cp "$root/THIRD_PARTY_NOTICES" "$output/"
cp "$root/scripts/build-ffmpeg.sh" "$output/sources/"
"$output/ffmpeg$suffix" -hide_banner -encoders 2>&1 | grep libmp3lame
"$output/ffmpeg$suffix" -hide_banner -protocols 2>&1 | grep -w tls
"$output/ffmpeg$suffix" -hide_banner -demuxers 2>&1 | grep -w f32le
echo "Built LGPL FFmpeg bundle: $output"
