#!/usr/bin/env bash
#
# Builds the ffmpeg rpm ships, for one RID, from the sources pinned in scripts/tool-versions.txt.
#
#   scripts/build-ffmpeg.sh osx-arm64    on a Mac
#   scripts/build-ffmpeg.sh osx-x64      on a Mac (cross-compiled on Apple silicon, needs nasm)
#   scripts/build-ffmpeg.sh win-x64      on Linux with mingw-w64 and nasm (the GitHub workflow)
#   scripts/build-ffmpeg.sh linux-x64    on Linux with nasm and OpenSSL 3 headers (the GitHub
#                                        workflow, inside amazonlinux:2023, the transcoder's OS)
#
# Options: --work DIR (default $TMPDIR/rpm-ffmpeg) holds downloads and build trees between runs.
#
# Writes RPM.Desktop/NativeLibs/tools/<rid>/ffmpeg[.exe], where the app build picks it up, and
# <work>/dist/ffmpeg-<rid>.gz, which is the asset the GitHub release holds.
#
# The build is LGPL 2.1 or later: no --enable-gpl, --enable-version3 or --enable-nonfree. The
# script reads the licence configure settled on and stops if it is anything else.
# --disable-autodetect means nothing is linked in because it happened to be installed on the build
# machine: every library in the binary is named in this file.
#
# In the binary: every FFmpeg decoder, demuxer, muxer, parser, filter and protocol (all LGPL in
# this configuration); LAME for MP3 encoding; zlib for PNG cover art and compressed containers;
# TLS from the platform (Secure Transport on macOS, Schannel on Windows, the system OpenSSL 3 on
# Linux). Left out: ffplay, ffprobe, capture devices other than lavfi (the tests make tones with
# it), hardware acceleration and documentation.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
# shellcheck source=tool-versions.txt
source "$SCRIPT_DIR/tool-versions.txt"

# The Mac app's own minimum (its LSMinimumSystemVersion). A lower value costs
# nothing; a higher one would stop ffmpeg loading on a Mac that the app supports.
MACOS_MIN=15.0

RID=""
WORK="${TMPDIR:-/tmp}/rpm-ffmpeg"
while [ $# -gt 0 ]; do
  case "$1" in
    --work) WORK="$2"; shift 2 ;;
    -*) echo "build-ffmpeg: unknown option $1" >&2; exit 1 ;;
    *) RID="$1"; shift ;;
  esac
done
[ -n "$RID" ] || { echo "usage: build-ffmpeg.sh <osx-arm64|osx-x64|win-x64|linux-x64> [--work DIR]" >&2; exit 1; }
# Made absolute, because the steps below change directory.
WORK="$(mkdir -p "$WORK" && cd "$WORK" && pwd)"

HOST_OS="$(uname -s)"
JOBS="$( (command -v nproc >/dev/null && nproc) || sysctl -n hw.ncpu)"
EXE=""

case "$RID" in
  osx-arm64|osx-x64)
    [ "$HOST_OS" = Darwin ] || { echo "build-ffmpeg: $RID builds on macOS only" >&2; exit 1; }
    ARCH="${RID#osx-}"; [ "$ARCH" = x64 ] && ARCH=x86_64
    CC="clang -arch $ARCH"
    TARGET_CFLAGS="-mmacosx-version-min=$MACOS_MIN"
    LDFLAGS="-mmacosx-version-min=$MACOS_MIN"
    LAME_HOST="$( [ "$ARCH" = arm64 ] && echo aarch64 || echo x86_64 )-apple-darwin"
    FFMPEG_TARGET=(--arch="$ARCH" --target-os=darwin --cc="$CC" --enable-cross-compile
                   --enable-pthreads --enable-securetransport)
    NEED_TLS=CONFIG_SECURETRANSPORT
    ;;
  win-x64)
    [ "$HOST_OS" = Linux ] || { echo "build-ffmpeg: win-x64 is cross-compiled on Linux with mingw-w64" >&2; exit 1; }
    CROSS=x86_64-w64-mingw32-
    CC="${CROSS}gcc"
    TARGET_CFLAGS=""
    # Links libgcc and everything else statically, so the .exe needs nothing beside it.
    LDFLAGS="-static"
    LAME_HOST=x86_64-w64-mingw32
    FFMPEG_TARGET=(--arch=x86_64 --target-os=mingw32 --cross-prefix="$CROSS" --enable-cross-compile
                   --pkg-config=pkg-config --enable-w32threads --enable-schannel)
    NEED_TLS=CONFIG_SCHANNEL
    EXE=.exe
    ;;
  linux-x64)
    [ "$HOST_OS" = Linux ] && [ "$(uname -m)" = x86_64 ] || { echo "build-ffmpeg: linux-x64 builds on x86_64 Linux" >&2; exit 1; }
    CC=gcc
    TARGET_CFLAGS=""
    # glibc stays dynamic: a statically linked glibc cannot load the host's NSS modules, and the
    # first DNS lookup then crashes. Everything else built here is static.
    LDFLAGS=""
    LAME_HOST=""
    FFMPEG_TARGET=(--enable-pthreads --enable-openssl)
    NEED_TLS=CONFIG_OPENSSL
    ;;
  *) echo "build-ffmpeg: unsupported RID '$RID'" >&2; exit 1 ;;
esac

# zlib and LAME take -O2 here. ffmpeg is given only the target flags, because its own -O3 comes
# first on the command line and a later -O2 would replace it.
LIB_CFLAGS="-O2 $TARGET_CFLAGS"

SRC="$WORK/src"
BUILD="$WORK/$RID"
PREFIX="$BUILD/prefix"
DIST="$WORK/dist"
OUT_DIR="$REPO_ROOT/RPM.Desktop/NativeLibs/tools/$RID"
mkdir -p "$SRC" "$DIST" "$OUT_DIR"
rm -rf "$BUILD"
mkdir -p "$BUILD" "$PREFIX"

sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

# Downloads once into $SRC and checks the pinned hash every time, so a cached file is checked too.
fetch() {
  local url="$1" file="$SRC/$2" want="$3"
  if [ ! -f "$file" ]; then
    echo "build-ffmpeg: downloading $2"
    curl -fsSL --retry 3 -o "$file.part" "$url"
    mv "$file.part" "$file"
  fi
  local got; got="$(sha256 "$file")"
  if [ "$got" != "$want" ]; then
    echo "build-ffmpeg: $2 has sha256 $got, tool-versions.txt pins $want" >&2
    exit 1
  fi
}

fetch "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz" "ffmpeg-$FFMPEG_VERSION.tar.xz" "$FFMPEG_SHA256"
fetch "https://downloads.sourceforge.net/project/lame/lame/$LAME_VERSION/lame-$LAME_VERSION.tar.gz" "lame-$LAME_VERSION.tar.gz" "$LAME_SHA256"
fetch "https://github.com/madler/zlib/releases/download/v$ZLIB_VERSION/zlib-$ZLIB_VERSION.tar.gz" "zlib-$ZLIB_VERSION.tar.gz" "$ZLIB_SHA256"

# --- zlib -------------------------------------------------------------------------------------
echo "build-ffmpeg: zlib $ZLIB_VERSION for $RID"
tar -xzf "$SRC/zlib-$ZLIB_VERSION.tar.gz" -C "$BUILD"
(
  cd "$BUILD/zlib-$ZLIB_VERSION"
  if [ "$RID" = win-x64 ]; then
    # SHARED_MODE=0 installs libz.a alone, so -lz cannot find a DLL to link against.
    make -f win32/Makefile.gcc PREFIX="$CROSS" SHARED_MODE=0 -j"$JOBS" \
      BINARY_PATH="$PREFIX/bin" INCLUDE_PATH="$PREFIX/include" LIBRARY_PATH="$PREFIX/lib" install >/dev/null
  else
    CC="$CC" CFLAGS="$LIB_CFLAGS" ./configure --static --prefix="$PREFIX" >/dev/null
    make -j"$JOBS" >/dev/null
    make install >/dev/null
  fi
)

# --- LAME -------------------------------------------------------------------------------------
echo "build-ffmpeg: LAME $LAME_VERSION for $RID"
tar -xzf "$SRC/lame-$LAME_VERSION.tar.gz" -C "$BUILD"
(
  cd "$BUILD/lame-$LAME_VERSION"
  # Only the encoder library: ffmpeg decodes MP3 with its own decoder.
  ./configure --prefix="$PREFIX" ${LAME_HOST:+--host="$LAME_HOST"} \
    --disable-shared --enable-static --disable-frontend --disable-decoder \
    --disable-gtktest --disable-dependency-tracking \
    CC="$CC" CFLAGS="$LIB_CFLAGS" LDFLAGS="$LDFLAGS" >/dev/null
  make -j"$JOBS" >/dev/null
  make install >/dev/null
)

# --- ffmpeg -----------------------------------------------------------------------------------
echo "build-ffmpeg: ffmpeg $FFMPEG_VERSION-rpm$RPM_FFMPEG_BUILD for $RID"
tar -xJf "$SRC/ffmpeg-$FFMPEG_VERSION.tar.xz" -C "$BUILD"
cd "$BUILD/ffmpeg-$FFMPEG_VERSION"
./configure \
  --prefix="$PREFIX" \
  --extra-version="rpm$RPM_FFMPEG_BUILD" \
  "${FFMPEG_TARGET[@]}" \
  --disable-autodetect \
  --disable-debug \
  --disable-doc \
  --disable-programs --enable-ffmpeg \
  --disable-devices --enable-indev=lavfi \
  --disable-hwaccels \
  --disable-shared --enable-static \
  --enable-zlib \
  --enable-libmp3lame \
  --extra-cflags="-I$PREFIX/include $TARGET_CFLAGS" \
  --extra-ldflags="-L$PREFIX/lib $LDFLAGS" \
  > "$BUILD/configure.log"

# The licence is read from what configure wrote, not assumed from the flags above.
LICENSE_LINE="$(grep -E '^#define FFMPEG_LICENSE ' config.h)"
if [ "$LICENSE_LINE" != '#define FFMPEG_LICENSE "LGPL version 2.1 or later"' ]; then
  echo "build-ffmpeg: configure settled on $LICENSE_LINE, not LGPL 2.1 or later" >&2
  exit 1
fi

# Everything rpm asks ffmpeg for, by the name config.h gives it. A component missing here would
# otherwise surface as one feature failing on one platform, on a user's machine.
NEEDED=(
  "$NEED_TLS" CONFIG_ZLIB CONFIG_LIBMP3LAME
  CONFIG_FILE_PROTOCOL CONFIG_PIPE_PROTOCOL CONFIG_HTTP_PROTOCOL CONFIG_HTTPS_PROTOCOL CONFIG_TLS_PROTOCOL
  CONFIG_HLS_DEMUXER CONFIG_CONCAT_DEMUXER CONFIG_MPEGPS_DEMUXER
  CONFIG_PCM_S16LE_DEMUXER CONFIG_PCM_S16BE_DEMUXER CONFIG_PCM_F32LE_DEMUXER
  CONFIG_FLAC_ENCODER CONFIG_ALAC_ENCODER CONFIG_AAC_ENCODER CONFIG_AC3_ENCODER CONFIG_LIBMP3LAME_ENCODER
  CONFIG_PCM_S16LE_ENCODER CONFIG_PCM_S24LE_ENCODER CONFIG_PCM_S32LE_ENCODER CONFIG_PCM_F32LE_ENCODER
  CONFIG_FLAC_MUXER CONFIG_MP4_MUXER CONFIG_IPOD_MUXER CONFIG_MP3_MUXER CONFIG_MATROSKA_MUXER
  CONFIG_ADTS_MUXER CONFIG_WAV_MUXER CONFIG_PCM_F32LE_MUXER
  CONFIG_ARESAMPLE_FILTER CONFIG_ATEMPO_FILTER CONFIG_ADECLICK_FILTER CONFIG_VOLUME_FILTER
  CONFIG_EQUALIZER_FILTER CONFIG_SILENCEDETECT_FILTER CONFIG_SINE_FILTER CONFIG_LAVFI_INDEV
)
missing=()
for name in "${NEEDED[@]}"; do
  grep -qE "^#define $name 1\$" config.h config_components.h 2>/dev/null || missing+=("$name")
done
if [ ${#missing[@]} -gt 0 ]; then
  echo "build-ffmpeg: configure left out ${missing[*]} (see $BUILD/configure.log)" >&2
  exit 1
fi

make -j"$JOBS" >/dev/null
BIN="$BUILD/ffmpeg-$FFMPEG_VERSION/ffmpeg$EXE"

# What the binary loads at run time. Only the operating system's own libraries are allowed.
case "$RID" in
  osx-*)
    codesign -s - -f "$BIN" 2>/dev/null
    foreign="$(otool -L "$BIN" | tail -n +2 | awk '{print $1}' | grep -vE '^(/usr/lib/|/System/Library/)' || true)"
    ;;
  win-x64)
    foreign="$(${CROSS}objdump -p "$BIN" | awk '/DLL Name:/ {print $3}' | grep -iE '^lib|zlib|mp3lame|pthread|gcc' || true)"
    ;;
  linux-x64)
    foreign="$(readelf -d "$BIN" | awk -F'[][]' '/NEEDED/ {print $2}' \
      | grep -vE '^(libc|libm|libdl|libpthread|librt|ld-linux-x86-64|libssl|libcrypto|libz)\.so' || true)"
    ;;
esac
if [ -n "$foreign" ]; then
  echo "build-ffmpeg: $RID ffmpeg links libraries that are not part of the OS:" >&2
  echo "$foreign" >&2
  exit 1
fi

# A run, where this machine can run the result: a tone through the encoders rpm uses.
if [ "$RID" != win-x64 ] && { [ "$RID" != osx-x64 ] || arch -x86_64 /usr/bin/true 2>/dev/null; }; then
  T="$BUILD/smoke"; mkdir -p "$T"
  "$BIN" -hide_banner -loglevel error -f lavfi -i "sine=frequency=440:duration=2" -c:a libmp3lame -b:a 320k "$T/a.mp3"
  "$BIN" -hide_banner -loglevel error -i "$T/a.mp3" -c:a flac -compression_level 8 "$T/a.flac"
  "$BIN" -hide_banner -loglevel error -i "$T/a.flac" -c:a aac -b:a 256k -movflags +faststart -f mp4 "$T/a.m4a"
  "$BIN" -hide_banner -loglevel error -i "$T/a.m4a" -filter:a "adeclick,atempo=1.05" -ac 1 -ar 22050 -f f32le "$T/a.raw"
  [ -s "$T/a.raw" ] || { echo "build-ffmpeg: the smoke run produced no audio" >&2; exit 1; }
  echo "build-ffmpeg: smoke run passed"
fi

# Removed first: macOS caches a binary's signature by file, and a signed binary copied over an
# older one in place is killed at launch (exit 137).
rm -f "$OUT_DIR/ffmpeg$EXE"
cp "$BIN" "$OUT_DIR/ffmpeg$EXE"
chmod +x "$OUT_DIR/ffmpeg$EXE"
gzip -9 -n -c "$BIN" > "$DIST/ffmpeg-$RID.gz"

echo "build-ffmpeg: $RID done"
echo "  $OUT_DIR/ffmpeg$EXE ($(du -h "$BIN" | cut -f1))"
echo "  $DIST/ffmpeg-$RID.gz"
