#!/usr/bin/env bash
#
# Fetches the ffmpeg and fpcalc binaries that the app starts as child processes, for one RID.
#
# This is the macOS and Linux script (fetch-tools.ps1 is the Windows one). The build calls it
# automatically. Run it by hand to pre-seed a RID before you cross-publish for it:
#
#   scripts/fetch-tools.sh osx-x64
#
# Binaries land in RPM.Desktop/NativeLibs/tools/<rid>/ and are gitignored. The versions and the
# SHA-256 of each binary are the ones in scripts/tool-versions.txt. A binary that differs from its
# pinned hash is downloaded again, and a download that differs from the pin is not installed. So a
# version change reaches every machine on its next build, and nothing unpinned is ever put where the
# build copies from.
#
# ffmpeg: the project's own build (scripts/build-ffmpeg.sh), from the GitHub release
#   rpm-ffmpeg-<FFMPEG_VERSION>-<RPM_FFMPEG_BUILD>. The repository is private, so this needs the
#   GitHub CLI with a sign-in (gh auth login), or GH_TOKEN set, as it is in the workflows.
# fpcalc: acoustid/chromaprint official release archives.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
VERSIONS="$SCRIPT_DIR/tool-versions.txt"
# shellcheck source=tool-versions.txt
source "$VERSIONS"
FFMPEG_TAG="rpm-ffmpeg-${FFMPEG_VERSION}-${RPM_FFMPEG_BUILD}"

FORCE=0
RID=""
for arg in "$@"; do
  case "$arg" in
    --force) FORCE=1 ;;
    *) RID="$arg" ;;
  esac
done

# Default to the host RID.
if [ -z "$RID" ]; then
  case "$(uname -s)" in
    Darwin) [ "$(uname -m)" = "arm64" ] && RID="osx-arm64" || RID="osx-x64" ;;
    Linux)  RID="linux-x64" ;;
    *) echo "fetch-tools: unsupported host $(uname -s); pass a RID explicitly" >&2; exit 1 ;;
  esac
fi

case "$RID" in
  osx-arm64) FPCALC_ASSET="chromaprint-fpcalc-${CHROMAPRINT_VERSION}-macos-arm64.tar.gz" ;;
  osx-x64)   FPCALC_ASSET="chromaprint-fpcalc-${CHROMAPRINT_VERSION}-macos-x86_64.tar.gz" ;;
  linux-x64) FPCALC_ASSET="chromaprint-fpcalc-${CHROMAPRINT_VERSION}-linux-x86_64.tar.gz" ;;
  win-x64)   FPCALC_ASSET="chromaprint-fpcalc-${CHROMAPRINT_VERSION}-windows-x86_64.zip" ;;
  *) echo "fetch-tools: unsupported RID '$RID'" >&2; exit 1 ;;
esac

EXE_SUFFIX=""
[ "${RID#win-}" != "$RID" ] && EXE_SUFFIX=".exe"

# The pinned hashes for this RID: FFMPEG_BINARY_SHA256_OSX_ARM64 and so on.
RID_KEY="$(printf '%s' "$RID" | tr '[:lower:]-' '[:upper:]_')"
FFMPEG_PIN_KEY="FFMPEG_BINARY_SHA256_$RID_KEY"
FPCALC_PIN_KEY="FPCALC_BINARY_SHA256_$RID_KEY"
FFMPEG_PIN="${!FFMPEG_PIN_KEY:-}"
FPCALC_PIN="${!FPCALC_PIN_KEY:-}"
for key in "$FFMPEG_PIN_KEY" "$FPCALC_PIN_KEY"; do
  if [ -z "${!key:-}" ]; then
    echo "fetch-tools: tool-versions.txt pins no $key, so nothing for $RID can be checked" >&2
    exit 1
  fi
done

OUT_DIR="$REPO_ROOT/RPM.Desktop/NativeLibs/tools/$RID"
FFMPEG_OUT="$OUT_DIR/ffmpeg$EXE_SUFFIX"
FPCALC_OUT="$OUT_DIR/fpcalc$EXE_SUFFIX"

sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

# True when the file is there and is the pinned binary.
is_pinned() { [ -f "$1" ] && [ "$(sha256 "$1")" = "$2" ]; }

if [ "$FORCE" -eq 0 ] && is_pinned "$FFMPEG_OUT" "$FFMPEG_PIN" && is_pinned "$FPCALC_OUT" "$FPCALC_PIN"; then
  echo "tools: $RID already present, skipping (use --force to refetch)"
  exit 0
fi

mkdir -p "$OUT_DIR"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Each binary is written to $WORK and moved into place, never copied over the old one: macOS
# caches a binary's signature by file and kills a signed binary that was replaced in place
# (exit 137).
install_tool() { rm -f "$2"; mv "$1" "$2"; chmod +x "$2"; }

# Stops before anything is installed when a download is not the pinned binary. $1 the file, $2 what
# it is, $3 the pinned hash, $4 its key in tool-versions.txt.
check_pinned() {
  local got; got="$(sha256 "$1")"
  if [ "$got" != "$3" ]; then
    echo "fetch-tools: $2 has SHA-256 $got; tool-versions.txt pins $3. It was not installed." >&2
    echo "fetch-tools: if tool-versions.txt was just moved to a new build, pin the new binary: $4=$got" >&2
    exit 1
  fi
}

# --- ffmpeg -----------------------------------------------------------------------------------
echo "tools: downloading ffmpeg ${FFMPEG_VERSION}-rpm${RPM_FFMPEG_BUILD} for $RID ..."
if ! command -v gh >/dev/null; then
  echo "fetch-tools: ffmpeg is published in the private repository $RPM_TOOLS_REPO; install the GitHub CLI and run gh auth login" >&2
  exit 1
fi
gh release download "$FFMPEG_TAG" -R "$RPM_TOOLS_REPO" -p "ffmpeg-$RID.gz" -D "$WORK"
gunzip -c "$WORK/ffmpeg-$RID.gz" > "$WORK/ffmpeg$EXE_SUFFIX"
check_pinned "$WORK/ffmpeg$EXE_SUFFIX" "ffmpeg-$RID.gz from $FFMPEG_TAG" "$FFMPEG_PIN" "$FFMPEG_PIN_KEY"
install_tool "$WORK/ffmpeg$EXE_SUFFIX" "$FFMPEG_OUT"

# --- fpcalc -----------------------------------------------------------------------------------
echo "tools: downloading fpcalc ${CHROMAPRINT_VERSION} for $RID ..."
curl -fsSL "https://github.com/acoustid/chromaprint/releases/download/v${CHROMAPRINT_VERSION}/${FPCALC_ASSET}" \
  -o "$WORK/$FPCALC_ASSET"

mkdir -p "$WORK/fp"
case "$FPCALC_ASSET" in
  *.zip) unzip -q "$WORK/$FPCALC_ASSET" -d "$WORK/fp" ;;
  *)     tar -xzf "$WORK/$FPCALC_ASSET" -C "$WORK/fp" ;;
esac

FOUND="$(find "$WORK/fp" -type f -name "fpcalc$EXE_SUFFIX" | head -n 1)"
if [ -z "$FOUND" ]; then
  echo "fetch-tools: fpcalc$EXE_SUFFIX not found inside $FPCALC_ASSET" >&2
  exit 1
fi
check_pinned "$FOUND" "fpcalc$EXE_SUFFIX from $FPCALC_ASSET" "$FPCALC_PIN" "$FPCALC_PIN_KEY"
install_tool "$FOUND" "$FPCALC_OUT"

echo "tools: $RID ready -> $OUT_DIR"
