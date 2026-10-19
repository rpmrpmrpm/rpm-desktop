#!/bin/bash
#
# Shows what the UI thread is doing while it is stuck.
#
# The wall diagnostic can rule causes out (the garbage collector, the database gates, a decode, the
# app's own timed code), but it cannot say what a thread with no managed frames is inside, because the
# only thread that could report that is the one that is not answering. So this asks the operating
# system instead.
#
# macOS `sample` looks at every thread of a running process a thousand times a second and prints the
# stacks it saw, heaviest first. Native frames come out with names. If the thread is inside Skia
# painting a frame, inside a blur, or waiting on a mutex, the symbol says so.
#
#   scripts/stall-sample.sh              # find rpm, sample it for 5 seconds
#   scripts/stall-sample.sh 10           # ...for 10
#
# Start it, then make the wall stutter. The report is written to /tmp and the top of it is printed.

set -uo pipefail

seconds="${1:-5}"

# Pick the app, not the launcher.
#
# `dotnet run` matches "RPM.Desktop" too, and it is the parent: its main thread waits in
# Process.WaitForExit for the whole run, which looks like a main thread stuck in a managed wait at 0%
# CPU, the same as the hang this script is for. So prefer a match whose own parent is also a match,
# which is the child.
#
# Written for bash 3.2, which is what macOS ships: no mapfile, no associative arrays.
candidates=$(pgrep -f "RPM.Desktop")
if [ -z "$candidates" ]; then
  echo "rpm does not appear to be running (looked for a process matching RPM.Desktop)." >&2
  exit 1
fi

pid=""
for candidate in $candidates; do
  parent=$(ps -o ppid= -p "$candidate" 2>/dev/null | tr -d ' ')
  for other in $candidates; do
    if [ "$parent" = "$other" ]; then
      pid="$candidate"
      break 2
    fi
  done
done

# If no match has a matching parent, only one process matched: it is the app, with no launcher.
if [ -z "$pid" ]; then
  pid=$(echo "$candidates" | head -1)
fi

out="/tmp/rpm-stall-$(date +%H%M%S).txt"
echo "sampling app pid $pid for ${seconds}s — make the genres wall stutter now"
sample "$pid" "$seconds" -file "$out" >/dev/null 2>&1

echo
echo "── the main thread ──"
# The UI thread is the process's main thread under Uno-Skia. Its stack is the one to read: the thread
# pool, the models and the sound device are allowed to be busy. This prints from the main thread's
# heading to the next thread's, which is where its own call graph ends.
awk '
  /Thread_.*(Main Thread|main-thread)/ { inside = 1 }
  inside && /^ *[0-9]+ Thread_/ && !/(Main Thread|main-thread)/ { exit }
  inside { print }
' "$out" | head -70

echo
echo "full report: $out"
echo "grep it for Sk (Skia), pthread_cond (waiting), or mach_msg (blocked on a port)."
