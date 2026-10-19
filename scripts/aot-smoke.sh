#!/usr/bin/env bash
#
# Checks what survives Native AOT, in about a minute.
#
# Publishes tools/AotSmoke as a real AOT binary and runs it. Each check covers
# something the app does that the trimmer cannot see: a type found by an attribute
# scan, a property found by name, a native library found by file name. A check that
# fails is a feature that silently does nothing in the published app.
#
# Run this BEFORE building the app for a runtime identifier that has not been built
# yet. It finds problems such as TagLib# reading no tags, or the OpenGL visualisers
# opening black, much faster than a full app build.
#
# Usage:
#   ./scripts/aot-smoke.sh                       # this machine's RID
#   ./scripts/aot-smoke.sh win-x64               # a named RID (must be run ON that OS:
#                                                #  ILC does not cross-compile)
#   ./scripts/aot-smoke.sh osx-arm64 /path/to/app.app/Contents/MacOS
#                                                # also audit a published app's native layout
#
# On Windows, the same from PowerShell:
#   dotnet publish tools\AotSmoke\AotSmoke.csproj -c Release -r win-x64 -o .aot-smoke
#   .aot-smoke\AotSmoke.exe --natives <the app's publish folder>

set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"

RID="${1:-}"
if [ -z "${RID}" ]; then
    case "$(uname -s)-$(uname -m)" in
        Darwin-arm64)  RID="osx-arm64" ;;
        Darwin-x86_64) RID="osx-x64" ;;
        Linux-aarch64) RID="linux-arm64" ;;
        Linux-x86_64)  RID="linux-x64" ;;
        *) echo "FATAL: cannot guess a runtime identifier for $(uname -s)-$(uname -m)." >&2
           echo "       Pass one: ./scripts/aot-smoke.sh <rid>" >&2
           exit 2 ;;
    esac
fi

AUDIT="${2:-}"
OUT="${REPO_DIR}/.aot-smoke/${RID}"

echo "[1/2] publishing the harness for ${RID} (Native AOT)..."
rm -rf "${OUT}"
dotnet publish "${REPO_DIR}/tools/AotSmoke/AotSmoke.csproj" \
    -c Release -r "${RID}" -o "${OUT}" 2>&1 | tail -5

BIN="${OUT}/AotSmoke"
[ -f "${BIN}.exe" ] && BIN="${BIN}.exe"
if [ ! -x "${BIN}" ]; then
    echo "FATAL: the harness did not build. Nothing was checked." >&2
    exit 1
fi

echo "[2/2] running the checks..."
if [ -n "${AUDIT}" ]; then
    "${BIN}" --natives "${AUDIT}"
else
    "${BIN}"
fi
