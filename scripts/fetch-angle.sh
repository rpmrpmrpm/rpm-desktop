#!/usr/bin/env bash
#
# Fetches the ANGLE pair (libEGL.dylib, libGLESv2.dylib) that the OpenGL visualisers run on, for one
# macOS RID. Uno's GLCanvasElement opens libEGL by name and Uno ships no copy for macOS, so without
# this pair every visualiser and Pegasus open black.
#
# The build calls this when a file is missing or is not the pinned one (FetchRpmAngle in
# RPM.Desktop.csproj). Run it by hand to pre-seed a RID before publishing for it:
#
#   scripts/fetch-angle.sh osx-x64
#
# The pair lands in RPM.Desktop/NativeLibs/angle/<rid>/, which is gitignored. The release and the
# SHA-256 of each file are pinned in scripts/tool-versions.txt (ANGLE_RELEASE, ANGLE_*_SHA256_<RID>).
# A download that differs from its pin is not installed. The release assets are public, so no
# sign-in is needed.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
# shellcheck source=tool-versions.txt
source "$SCRIPT_DIR/tool-versions.txt"

FORCE=0
RID=""
for arg in "$@"; do
  case "$arg" in
    --force) FORCE=1 ;;
    *) RID="$arg" ;;
  esac
done

if [ -z "$RID" ]; then
  [ "$(uname -s)" = "Darwin" ] || { echo "fetch-angle: pass a macOS RID (osx-arm64 or osx-x64)" >&2; exit 1; }
  [ "$(uname -m)" = "arm64" ] && RID="osx-arm64" || RID="osx-x64"
fi

case "$RID" in
  osx-arm64) ASSET="angle-macos-arm64.tar.gz" ;;
  osx-x64)   ASSET="angle-macos-x64.tar.gz" ;;
  *) echo "fetch-angle: ANGLE ships only in the Mac app, and there is no build for '$RID'" >&2; exit 1 ;;
esac

RID_KEY="$(printf '%s' "$RID" | tr '[:lower:]-' '[:upper:]_')"
EGL_PIN_KEY="ANGLE_LIBEGL_SHA256_$RID_KEY"
GLES_PIN_KEY="ANGLE_LIBGLESV2_SHA256_$RID_KEY"
for key in ANGLE_REPO ANGLE_RELEASE "$EGL_PIN_KEY" "$GLES_PIN_KEY"; do
  if [ -z "${!key:-}" ]; then
    echo "fetch-angle: tool-versions.txt sets no $key, so nothing for $RID can be fetched or checked" >&2
    exit 1
  fi
done

OUT_DIR="$REPO_ROOT/RPM.Desktop/NativeLibs/angle/$RID"

sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }
is_pinned() { [ -f "$1" ] && [ "$(sha256 "$1")" = "$2" ]; }

if [ "$FORCE" -eq 0 ] && is_pinned "$OUT_DIR/libEGL.dylib" "${!EGL_PIN_KEY}" \
                      && is_pinned "$OUT_DIR/libGLESv2.dylib" "${!GLES_PIN_KEY}"; then
  echo "angle: $RID already present, skipping (use --force to refetch)"
  exit 0
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

URL="https://github.com/$ANGLE_REPO/releases/download/$ANGLE_RELEASE/$ASSET"
echo "angle: downloading $ASSET from $ANGLE_REPO $ANGLE_RELEASE ..."
curl -fsSL --retry 3 "$URL" -o "$WORK/$ASSET"
mkdir -p "$WORK/x"
tar -xzf "$WORK/$ASSET" -C "$WORK/x"

# Both files are checked before either is installed, so a bad download leaves the old pair whole.
# Both mismatches are reported, so a new release can be pinned in one pass.
MISMATCH=0
check_pinned() {   # $1 file name, $2 pin key
  local got
  if [ ! -f "$WORK/x/$1" ]; then
    echo "fetch-angle: $ASSET has no $1 at its top level." >&2
    MISMATCH=1
    return
  fi
  got="$(sha256 "$WORK/x/$1")"
  if [ "$got" != "${!2}" ]; then
    echo "fetch-angle: $1 from $ASSET has SHA-256 $got; tool-versions.txt pins ${!2}." >&2
    echo "fetch-angle: if ANGLE_RELEASE was just moved to a new release, pin the new file: $2=$got" >&2
    MISMATCH=1
  fi
}
check_pinned libEGL.dylib "$EGL_PIN_KEY"
check_pinned libGLESv2.dylib "$GLES_PIN_KEY"
if [ "$MISMATCH" -ne 0 ]; then
  echo "fetch-angle: nothing was installed." >&2
  exit 1
fi

# Each file is moved into place and never copied over the old one: macOS caches a binary's
# signature by file and kills a signed binary that was replaced in place (exit 137). The file is
# touched after the move because the archive keeps the build's date, and the build copies the pair
# with PreserveNewest. An output folder that holds an older pair with a later date would keep it.
mkdir -p "$OUT_DIR"
for f in libEGL.dylib libGLESv2.dylib; do
  rm -f "$OUT_DIR/$f"
  mv "$WORK/x/$f" "$OUT_DIR/$f"
  touch "$OUT_DIR/$f"
done

echo "angle: $RID ready -> $OUT_DIR"
