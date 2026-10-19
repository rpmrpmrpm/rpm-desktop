"""
Stacks taken from outside, without stopping anything.

The sampler that stops the app to look at it has two faults that cannot be designed away: the person
using the app cannot use it, so the thing being reproduced never happens; and the app cannot tell
being stopped from being stuck, so the instrument reads its own pauses as stalls and feeds itself.
Both were met, in that order.

`dotnet-stack` asks the runtime for every managed thread's stack over the diagnostics pipe. Nothing is
suspended, nothing is stopped, and the app goes on being used while it answers — which is the only
arrangement in which the fault appears at all.
"""

from __future__ import annotations

import os
import subprocess
import time

from .ring import Kind, Ring, newest_ring

STACK = os.path.expanduser("~/.dotnet/tools/dotnet-stack")


def take(pid: int, why: str, into) -> bool:
    """One report, appended. False when the runtime would not answer."""
    try:
        done = subprocess.run(
            [STACK, "report", "-p", str(pid)],
            capture_output=True, text=True, timeout=90)
    except Exception as e:
        into.write(f"\n### {why}\n[could not ask: {e}]\n")
        into.flush()
        return False

    body = done.stdout.strip()
    into.write(f"\n### {why}\n")
    into.write(body if body else f"[nothing came back; stderr: {done.stderr.strip()[:400]}]\n")
    into.write("\n")
    into.flush()
    return bool(body)


def hunt(over_ms: float = 1200, rounds: int = 6, patience: float = 300,
         directory: str | None = None, out: str | None = None) -> int:
    """Waits for the drawing thread to go missing and takes everyone's stack, without pausing."""
    began = time.monotonic()

    path = newest_ring(directory)
    while path is None and time.monotonic() - began < patience:
        print("   waiting for the app to write a ring...", flush=True)
        time.sleep(1)
        path = newest_ring(directory)

    if path is None:
        print("the app never wrote a ring", flush=True)
        return 1

    ring = Ring(directory, path)
    out = out or os.path.join(ring.dir, "stacks.txt")
    seen = 0
    caught = 0
    said = began
    looked = began

    print(f"watching pid {ring.pid}; taking stacks when the thread is gone {over_ms:.0f}ms",
          flush=True)
    print(f"nothing is paused. use the app normally. stacks go to {out}", flush=True)

    with open(out, "w") as into:
        into.write(f"# stacks taken while the drawing thread was missing, pid {ring.pid}\n")
        into.flush()

        try:
            while caught < rounds and time.monotonic() - began < patience:
                time.sleep(0.05)
                now = time.monotonic()

                if now - looked > 2:
                    looked = now
                    latest = newest_ring(directory)
                    if latest and latest != ring.path:
                        ring.close()
                        ring = Ring(directory, latest)
                        seen = 0
                        print(f"   app replaced: now pid {ring.pid}", flush=True)

                if now - said > 15:
                    said = now
                    print(f"   ...{now - began:.0f}s, {caught} caught", flush=True)

                rows = ring.records(seen)
                if not len(rows):
                    continue
                seen = ring.written

                gone = rows[rows["kind"] == Kind.DISPATCH_DEPTH]
                if not len(gone) or float(gone["a"].max()) < over_ms * 1e6:
                    continue

                missing = float(gone["a"].max()) / 1e6
                caught += 1
                print(f"   stall {caught}: thread gone {missing:,.0f}ms, taking stacks", flush=True)
                take(ring.pid, f"stall {caught}: drawing thread gone {missing:,.0f}ms", into)

                # A pause of our own would be a lie in the next reading; a wait is only politeness to
                # the pipe, which takes about a second to answer.
                time.sleep(1.5)

        except KeyboardInterrupt:
            print("   asked to stop", flush=True)
        finally:
            ring.close()

    print(f"\ncaught {caught}. stacks in {out}", flush=True)
    return 0
