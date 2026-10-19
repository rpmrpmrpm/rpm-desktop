"""
Catch a stall in the act, and take the stacks with the operating system's own sampler.

**Triggered off the ring, not off processor use.** The app's watchdog posts a stamped dispatcher item
every sixteen milliseconds and writes a `DispatchDepth` record carrying how long the outstanding one
has been waiting — from a thread that keeps running while the drawing thread does not. So "the
interface is stuck NOW" is a fact another process can read for free, out of a mapped file, while the
app is beachballed. Processor use cannot answer it: startup burns several cores and passes through
single-core phases indistinguishable from a stall, so a threshold either fires on the launch or has to
wait the launch out, and the stall can happen before the window is even up. Both were tried.

**The stacks come from `sample(1)`.** It needs nothing from the runtime, so it keeps working while the
runtime is frozen, which is the one property that matters here. The two alternatives are both worse
and both were used before this: the EventPipe sample profiler stops the world at a kilohertz and on
macOS can consume the entire wall clock, and pausing the debugger to look lengthens the stall being
looked at and, done wrong, does not let go.

**One capture, then gone.** No loop over runs, no re-arming, one deadline over the whole thing. A
watcher that re-attaches on every launch cost this investigation a day: it was silent, it was in
`/tmp`, and killing it fixed the app until the next launch — which is the shape of a fault that looks
intermittent and is not.
"""

from __future__ import annotations

import os
import subprocess
import sys
import time

from .ring import Kind, Ring

#: Records older than this are somebody else's problem. A ring left by a PREVIOUS run is full of
#: perfectly real stalls that are over, and firing on one of those is how an hour went into reporting
#: on an app that had already been restarted.
FRESH_NS = 2_000_000_000


def pid_of(ring_path: str) -> str | None:
    """The process a ring belongs to, out of its name. Taken from the ring rather than looked up, so
    the sample is of the process whose stall was just read and not of whatever is running now."""
    stem = os.path.basename(ring_path).removeprefix("scope.").removesuffix(".ring")
    return stem if stem.isdigit() else None


def stuck_for(ring: Ring) -> float:
    """How long the drawing thread has been missing, as of the last few records. Zero when it is
    keeping up, or when everything in the ring is too old to be about this run."""
    records = ring.records()
    deep = records[records["kind"] == Kind.DISPATCH_DEPTH]
    if len(deep) == 0:
        return 0.0

    now = time.clock_gettime_ns(time.CLOCK_UPTIME_RAW)
    fresh = deep[deep["nanos"] > now - FRESH_NS]
    return float(fresh["a"].max()) / 1e6 if len(fresh) else 0.0


def watch(
    directory: str | None,
    stuck_ms: float,
    out: str,
    seconds: float,
    say=print,
) -> int:
    """Waits for the drawing thread to be gone longer than `stuck_ms`, samples once, and returns."""
    deadline = time.time() + seconds
    say(f"armed: sampling when the drawing thread has been gone {stuck_ms:.0f}ms "
        f"(giving up at {time.strftime('%H:%M:%S', time.localtime(deadline))})")

    while time.time() < deadline:
        try:
            with Ring(directory) as ring:
                gone = stuck_for(ring)
                pid = pid_of(ring.path)
        except Exception:
            # A ring being replaced under us is ordinary: the app is restarted constantly while a
            # fault is being chased, and there is nothing to report about the gap.
            time.sleep(0.4)
            continue

        if gone > stuck_ms and pid is not None:
            say(f"STUCK {gone:.0f}ms at {time.strftime('%H:%M:%S')} — sampling {pid}")
            subprocess.run(
                ["sample", pid, "4", "-file", out],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            say(f"captured to {out}")
            say("read it with: grep -c SuspendEE, and look at the drawing thread first")
            return 0

        time.sleep(0.4)

    say("no stall that long inside the deadline")
    return 1


def main() -> int:
    out = sys.argv[1] if len(sys.argv) > 1 else "/tmp/rpm-stall.txt"
    stuck = float(sys.argv[2]) if len(sys.argv) > 2 else 1500
    return watch(None, stuck, out, float(sys.argv[3]) if len(sys.argv) > 3 else 900)


if __name__ == "__main__":
    raise SystemExit(main())
