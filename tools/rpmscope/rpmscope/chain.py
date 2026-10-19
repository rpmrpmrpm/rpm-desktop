"""
Why a frame was late, as a chain.

A plain dump of diagnostics does not say why a frame was late. The useful answer is a sentence: not
"the write gate was busy" but "the wall waited 24,102ms behind MirrorPeerCatalog, which was started
by OpenShelfAsync". This module walks two kinds of edge to build that sentence:

* **parent ids**, which say who asked for a piece of work, and
* **the gate hold that overlapped the stall**, which says what the thread was actually stopped by,

and prints them innermost first, so the last line is the thing to go and look at.

The processor time decides the shape of the answer. A stall with processor time in it is work, and
the fix is to make that work cheaper or move it off this thread. A stall with none is a thread that
was NOT RUNNING, and making its work faster does nothing. The two need opposite fixes and look the
same from outside, so only the measured processor time can tell them apart.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np

from .ring import Kind, Ring


@dataclass
class Link:
    """One line of the chain: what kind of edge it is, and what it says."""

    kind: str
    text: str


@dataclass
class Explanation:
    stall_seq: int
    stall_ns: int
    cpu_ns: int
    links: list[Link] = field(default_factory=list)

    def lines(self) -> list[str]:
        # A negative reading means the watchdog had nothing to subtract from. This happens at the
        # first landing of a run, which is the app's own startup. It is not nought. Nought would say
        # that the thread was parked, and that points to the opposite fix.
        if self.cpu_ns < 0:
            head = (
                f"frame {self.stall_seq} overran {self.stall_ns / 1e6:,.0f}ms  "
                f"(cpu not measured — first landing of the run, no baseline to subtract)"
            )
        else:
            running = (
                "the thread WAS running"
                if self.cpu_ns > self.stall_ns * 0.5
                else "the thread was NOT running"
            )
            head = (
                f"frame {self.stall_seq} overran {self.stall_ns / 1e6:,.0f}ms  "
                f"(cpu {self.cpu_ns / 1e6:,.0f}ms, {running})"
            )
        width = max((len(link.kind) for link in self.links), default=0)
        return [head] + [f"  {link.kind:<{width}}  {link.text}" for link in self.links]

    def __str__(self) -> str:
        return "\n".join(self.lines())


def explain(ring: Ring, stall_seq: int) -> Explanation:
    """Builds the chain for one stall. Raises LookupError if that sequence is not a stall."""
    records = ring.records()
    match = records[records["seq"] == stall_seq]
    if len(match) == 0 or match[0]["kind"] != Kind.FRAME_OVERRUN:
        raise LookupError(f"no stall recorded with sequence {stall_seq}")

    stall = match[0]
    began = int(stall["nanos"]) - int(stall["a"])
    during = records[(records["nanos"] >= began) & (records["nanos"] <= int(stall["nanos"]))]

    out = Explanation(int(stall["seq"]), int(stall["a"]), int(stall["b"]))

    # What held a gate across it, worst first. This is nearly always the answer, when there is one.
    holds = during[during["kind"] == Kind.GATE_HOLD]
    if len(holds):
        worst = holds[np.argmax(holds["a"])]
        gate = ring.name(worst["detail"]) or "gate"
        cpu = int(worst["b"])
        # Negative means "this platform keeps no per-thread processor counter". It is NOT nought.
        # Nought would say that the holder was parked, which is wrong on Windows and Linux.
        # See ScopeClock.ThreadCpuNanos.
        if cpu < 0:
            reading = " (cpu not measured on this platform)"
        elif cpu:
            reading = f" (cpu {cpu / 1e6:,.0f}ms)"
        else:
            reading = " (cpu 0ms), and it was not running either"
        out.links.append(
            Link(
                gate,
                f"held {int(worst['a']) / 1e6:,.0f}ms by {ring.name(worst['name_id'])}{reading}",
            )
        )
        # From the holder's OWN job outward: who reached it, and who started that. Starting from the
        # parent would drop the frame that names the method the hold was taken in.
        out.links.extend(_ancestry(ring, records, int(worst["job"])))

    waits = during[during["kind"] == Kind.GATE_WAIT]
    if len(waits):
        worst = waits[np.argmax(waits["a"])]
        out.links.append(
            Link(
                "waited",
                f"{ring.name(worst['name_id'])} {int(worst['a']) / 1e6:,.0f}ms"
                f" behind {ring.name(worst['detail'])}",
            )
        )

    throws = during[during["kind"] == Kind.THROW]
    if len(throws):
        kinds = sorted({ring.name(t["name_id"]) or "?" for t in throws})
        out.links.append(Link("throws", f"{len(throws)} throws ({', '.join(kinds)})"))

    suspends = during[during["kind"] == Kind.GC_SUSPEND]
    if len(suspends):
        total = int(suspends["a"].sum())
        verdict = "this is it" if total > int(stall["a"]) * 0.5 else "not this"
        out.links.append(
            Link("gc", f"{len(suspends)} suspensions, {total / 1e6:,.0f}ms total, {verdict}")
        )

    if not out.links:
        out.links.append(Link("nothing", "nothing of ours ran or waited inside this frame"))

    return out


def _ancestry(ring: Ring, records: np.ndarray, job_id: int, depth: int = 8) -> list[Link]:
    """
    Walks parent ids outward: who reached this, and who started that.

    Guarded by a visited set rather than by trusting the ids, because a ring that wrapped mid-job can
    hand back a parent whose start record is already gone.
    """
    starts = records[records["kind"] == Kind.JOB_START]
    seen: set[int] = set()
    links: list[Link] = []

    while job_id and job_id not in seen and len(links) < depth:
        seen.add(job_id)
        row = starts[starts["job"] == job_id]
        if len(row) == 0:
            links.append(Link("via", f"job {job_id} (its start is no longer in the ring)"))
            break
        links.append(Link("via", ring.name(row[0]["name_id"]) or f"job {job_id}"))
        job_id = int(row[0]["parent"])

    return links


# ── what the throws add up to ───────────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class Throws:
    """Three readings of the same records, because one of them is never enough.

    `by_type` is the exception type and its count. "206 TaskCanceledException" says that the app
    cancels things on purpose, and says nothing about which code asked for it.
    `by_site` is the line that threw. `asked_by` is who ASKED. It is carried in the record's parent,
    because work that a library hands to the pool throws with no frame of ours on its stack, so
    walking that stack never finds the caller.
    """

    by_type: dict[str, int]
    by_site: dict[str, int]
    chains: dict[str, str]
    asked_by: dict[str, int]


def summarise_throws(records, name) -> Throws:
    """Groups first-chance records. `name` turns a name id into text, or None where there is none."""
    by_type: dict[str, int] = {}
    by_site: dict[str, int] = {}
    chains: dict[str, str] = {}
    asked_by: dict[str, int] = {}

    jobs = {
        int(r["job"]): (name(int(r["name_id"])) or "?", name(int(r["detail"])) or "")
        for r in records
        if int(r["kind"]) == Kind.JOB_ENQUEUE
    }

    for r in records:
        if int(r["kind"]) != Kind.THROW:
            continue

        kind = name(int(r["name_id"])) or "?"
        by_type[kind] = by_type.get(kind, 0) + 1

        if (job := jobs.get(int(r["parent"]))) is not None:
            where = job[1].rsplit("/", 1)[-1]
            asked = f"{job[0]}  ({where})" if where else job[0]
            asked_by[asked] = asked_by.get(asked, 0) + 1

        if not (site := name(int(r["detail"]))):
            continue

        # The FIRST throw from a line carries its whole chain and the rest carry the short name; both
        # are counted against the line the chain starts at, so one line is one row rather than two.
        head = site.split(" < ")[0]
        by_site[head] = by_site.get(head, 0) + 1
        if " < " in site:
            chains[head] = site

    return Throws(by_type, by_site, chains, asked_by)
