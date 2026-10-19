#!/bin/bash
# Take one picture of the room, headless, and wait for it.
#
#   tools/roomlab/shot.sh a.png "t=8&uFrames=6&uSize=1.6"
#
# `firefox --headless --screenshot` cannot be used for this. It composites a WebGL
# canvas as black whatever the shader drew, and it exits at the load event, which is
# before the cover textures have arrived. So Firefox is launched as an ordinary
# headless browser instead, the page posts its own canvas back to the server (see
# shotMode in index.html), and this waits for the file and then closes it.
#
# A throwaway profile, so nothing here touches the Firefox you have open.
set -u
NAME="${1:?usage: shot.sh <name.png> [query]}"
QUERY="${2:-}"
PORT="${ROOMLAB_PORT:-8777}"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/shots/$NAME"
FIREFOX="${FIREFOX:-/Applications/Firefox.app/Contents/MacOS/firefox}"

rm -f "$OUT"
PROFILE="$(mktemp -d)"
"$FIREFOX" --headless --no-remote --profile "$PROFILE" \
  "http://localhost:$PORT/inline?shot=$NAME&$QUERY" >/dev/null 2>&1 &
PID=$!

for _ in $(seq 1 120); do
  [ -s "$OUT" ] && break
  sleep 0.25
done

kill "$PID" 2>/dev/null
wait "$PID" 2>/dev/null
rm -rf "$PROFILE"

NOTE="${OUT%.png}.txt"
if [ -s "$OUT" ]; then
  echo "$NAME $(wc -c < "$OUT" | tr -d ' ') bytes"
  if [ -s "$NOTE" ]; then sed "s/^/  /" "$NOTE"; fi
else
  echo "$NAME FAILED: nothing posted within 30s" >&2
  exit 1
fi
