"""
What the drawing thread is doing DURING a stall, sampled rather than glimpsed.

One stack is one moment. It says where the thread was, not where it spends its time, and the
difference matters: the first stack taken inside one of these stalls found the thread running a
button's hover animation, which is either the whole story or a coincidence, and a single sample
cannot tell you which.

So this waits for the thread to go missing and then takes its stack again and again for as long as
it is gone, and counts what it finds. A frame that turns up in fourteen samples of twenty is an
answer; one that turns up once is where the thread happened to be.

Each sample stops the app for a moment, which lengthens the stall being measured. It does not move
the thread, which is the only thing being asked.
"""

from __future__ import annotations

import collections
import time

from .catch import Adapter, frames
from .ring import Kind, Ring, newest_ring


def ui_thread(adapter: Adapter) -> tuple[int, str] | None:
    """The thread the app draws on. Named on a good day, and the first one on any other."""
    listed = adapter.call("get_threads")
    ids: list[tuple[int, str]] = []
    for line in listed.split("\n"):
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) == 2 and cells[0].isdigit():
            ids.append((int(cells[0]), cells[1]))

    if not ids:
        return None
    for tid, name in ids:
        if name == "Main Thread":
            return tid, name
    return ids[0]


def during(port: int = 10101, over_ms: float = 1200, samples: int = 20,
           gap: float = 0.35, patience: float = 300, directory: str | None = None) -> int:
    """
    Waits for the drawing thread to go missing and samples it, over and over, across as many stalls
    as it takes to have something worth reading.

    <b>Accumulated across stalls, not taken from one.</b> The first version stopped after the first
    stall, and the first stall of a run is the start-up one, which is short and yields a sample or
    two — one stack again, dressed up as a profile. What is wanted is where the thread SPENDS its
    time, and that is a count over many looks.
    """
    adapter = Adapter(port, patience=12)

    # Armed BEFORE the app exists, on purpose. The stall depends on what is clicked in the first
    # seconds after the window appears, so anything started afterwards is already too late.
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
    seen = 0            # from the beginning: the stall we are here for may already have started
    said = began
    looked = began

    last_release = 0.0
    tops: collections.Counter[str] = collections.Counter()
    ours: collections.Counter[str] = collections.Counter()
    taken = 0
    stalls = 0

    note = open(f"{ring.dir}/stacks.txt", "w")
    print(f"watching pid {ring.pid}; sampling whenever the thread is gone {over_ms:.0f}ms", flush=True)
    print(f"every stack is written to {ring.dir}/stacks.txt as it is taken", flush=True)

    try:
        while taken < samples and time.monotonic() - began < patience:
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
                print(f"   ...{now - began:.0f}s, {taken} samples from {stalls} stalls", flush=True)

            rows = ring.records(seen)
            if not len(rows):
                continue
            seen = ring.written

            out = rows[rows["kind"] == Kind.DISPATCH_DEPTH]
            if not len(out) or float(out["a"].max()) < over_ms * 1e6:
                continue

            # Not one of ours. The app cannot tell being stopped from being stuck, so without this
            # the sampler samples its own pauses and reports them as the app's stalls.
            if adapter.ours(now) or now - last_release < 2.0:
                continue

            stalls += 1
            print(f"   stall {stalls}: thread gone {float(out['a'].max()) / 1e6:,.0f}ms, sampling",
                  flush=True)
            taken += gather(adapter, tops, ours, most=min(8, samples - taken), gap=gap,
                            note=note)
            last_release = time.monotonic()

    except KeyboardInterrupt:
        print("\n   asked to stop", flush=True)
    finally:
        adapter.release()
        ring.close()
        note.close()

    return say(tops, ours, taken, stalls)


def gather(adapter: Adapter, tops, ours, most: int, gap: float, note=None) -> int:
    """Samples the drawing thread while it is gone. Returns how many stacks it actually got."""
    taken = 0
    misses = 0

    for _ in range(most):
        if misses >= 4:
            break

        if not adapter.hold():
            misses += 1
            time.sleep(gap)
            continue

        try:
            found = ui_thread(adapter)
            stack = (frames(adapter.call("get_stack_trace", threadId=found[0], levels=30))
                     if found is not None else [])
        finally:
            adapter.release()

        if not stack:
            misses += 1
            time.sleep(gap)
            continue

        taken += 1

        # Written out as it is taken. The first version kept the count in memory and printed at the
        # end, so interrupting it — which is what one does to a tool that is taking too long — threw
        # away every sample it had.
        if note is not None:
            note.write("--- sample ---\n" + "\n".join(stack) + "\n")
            note.flush()

        tops[stack[0].strip().lstrip("> ").split("(")[0][:110]] += 1
        for line in stack:
            if line.strip().startswith(">>"):
                ours[line.strip().lstrip("> ").split("(")[0][:110]] += 1

        time.sleep(gap)

    return taken


def say(tops, ours, taken: int, stalls: int) -> int:
    """What the counting found."""
    if not taken:
        print("\nno samples taken. was a debug session running, and did anything stall?", flush=True)
        return 1

    print(f"\n{taken} samples of the drawing thread, across {stalls} stalls\n", flush=True)
    print("TOP OF STACK — where it actually was:", flush=True)
    for what, count in tops.most_common(10):
        print(f"  {count:>3}/{taken}  {what}", flush=True)

    print("\nOUR FRAMES — what it was doing, anywhere in the stack:", flush=True)
    for what, count in ours.most_common(16):
        print(f"  {count:>3}/{taken}  {what}", flush=True)

    return 0
