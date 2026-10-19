"""
The ring as a trace file, so it can be read in Perfetto instead of in this window.

**Why this exists rather than a prettier window.** The live view here has one job nothing else can do:
it keeps working while the app is frozen, off a mapped file, ten times a second, costing the app
nothing. That is worth owning. But everything AFTER the event — scrubbing an hour, searching, pinning
two moments together, reading a flame of nested work — is a solved problem with a very good free tool
behind it, and hand-building a worse copy of `ui.perfetto.dev` is not a good use of anybody's evening.

So: watch it live here, and when something interesting has been caught, export it and open it there.

The format is Chrome's trace-event JSON, which Perfetto reads directly. Timestamps are microseconds.
Durations become slices on a track per thread; everything instantaneous becomes a mark; counters
become the graphs under them.
"""

from __future__ import annotations

import json

from .ring import Kind, Ring

#: Kinds that took time, and so are drawn as a slice with a length.
SPANS: dict[int, str] = {
    Kind.GATE_HOLD: "gate held",
    Kind.GATE_WAIT: "gate waited",
    Kind.FRAME_OVERRUN: "ui stalled",
    Kind.CONTENTION: "lock contention",
}

#: Kinds that happened at an instant.
MARKS: dict[int, str] = {
    Kind.THROW: "throw",
    Kind.INPUT: "input",
    Kind.SAID: "said",
    Kind.SAMPLE: "sample",
    Kind.HTTP_REQUEST: "request",
    Kind.HTTP_CONNECTION: "connection",
    Kind.GC_SUSPEND: "world stopped",
    Kind.GC_RESTART: "world started",
    Kind.WAIT_HANDLE: "waiting on a handle",
    Kind.CLOSING: "closing",
}

PID = 1


def export(ring: Ring, out: str) -> int:
    """Writes every record as a trace event. Returns how many were written."""
    records = ring.records()
    if not len(records):
        return 0

    # Perfetto shows a wall clock if the trace says where zero is. The ring already knows, because it
    # was stamped with both clocks when it was made.
    base_us = ring.unix_start / 1e3
    mono = ring.mono_start

    def at(nanos: int) -> float:
        return base_us + (int(nanos) - mono) / 1e3

    events: list[dict] = [
        {"ph": "M", "pid": PID, "name": "process_name",
         "args": {"name": f"rpm ({ring.path.rsplit('/', 1)[-1]})"}},
    ]

    named: set[int] = set()
    for record in records:
        kind = int(record["kind"])
        thread = int(record["thread"])
        name = ring.name(int(record["name_id"])) or "?"
        detail = ring.name(int(record["detail"])) or ""

        if thread and thread not in named:
            named.add(thread)
            events.append({
                "ph": "M", "pid": PID, "tid": thread, "name": "thread_name",
                # Thread 1 is the one that draws, and it is the row anybody opening this looks at
                # first. Named rather than numbered so it does not have to be worked out.
                "args": {"name": "drawing thread" if thread == 1 else f"thread {thread}"},
            })

        if kind in SPANS:
            # A record is stamped when the thing ENDED and carries how long it took, so the slice
            # starts before its own stamp. Left alone, every cause would sit after its effect.
            took_us = int(record["a"]) / 1e3
            events.append({
                "ph": "X", "pid": PID, "tid": thread or 1,
                "cat": SPANS[kind], "name": f"{SPANS[kind]}: {name}",
                "ts": at(record["nanos"]) - took_us, "dur": max(took_us, 1),
                "args": {"who": name, "detail": detail,
                         "processor time": (
                             "not measured"
                             if int(record["b"]) < 0
                             else f"{int(record['b']) / 1e6:,.1f}ms"
                         )},
            })
        elif kind == Kind.COUNTER:
            events.append({
                "ph": "C", "pid": PID, "name": name,
                "ts": at(record["nanos"]), "args": {name: int(record["a"])},
            })
        elif kind in MARKS:
            # Closing never carries a name, so "closing: ?" would read as a name not caught up yet.
            label = MARKS[kind] if kind == Kind.CLOSING else f"{MARKS[kind]}: {name}"
            events.append({
                "ph": "i", "s": "t", "pid": PID, "tid": thread or 1,
                "cat": MARKS[kind], "name": label[:120],
                "ts": at(record["nanos"]), "args": {"detail": detail},
            })

    with open(out, "w", encoding="utf-8") as f:
        json.dump({"traceEvents": events, "displayTimeUnit": "ms"}, f)
    return len(events)
