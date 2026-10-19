"""
The command line.

Output is fixed in shape and small on purpose. This is read by a person in a terminal and by a model
in a tool call, and both are worse off for a dump: what is wanted from every one of these is a claim,
in as few lines as it takes to be checkable.
"""

from __future__ import annotations

import argparse
import time
import sys

import numpy as np

from .chain import explain
from .ring import Kind, Ring


def ms(nanos: float) -> str:
    return f"{nanos / 1_000_000:,.0f}ms"


def cmd_top(args: argparse.Namespace) -> int:
    """One snapshot: what has been happening in the last few seconds."""
    with Ring(args.dir) as ring:
        records = ring.records()
        if len(records) == 0:
            print("nothing recorded (is rpm running?)")
            return 0

        now = int(records["nanos"].max())
        window = int(args.seconds * 1_000_000_000)
        recent = records[records["nanos"] >= now - window]

        print(f"scope  pid {ring.pid}  {len(records):,} records  last {args.seconds:g}s:")

        holds = recent[recent["kind"] == Kind.GATE_HOLD]
        if len(holds):
            print(f"\n  gate holds ({len(holds)}):")
            for r in holds[np.argsort(-holds["a"])][: args.top]:
                cpu = (
                    "cpu not measured on this platform"
                    if r["b"] < 0
                    else f"cpu {ms(r['b'])}"
                    if r["b"]
                    else "cpu 0, the thread was NOT running"
                )
                print(f"    {ms(r['a']):>10}  {ring.name(r['name_id'])}  ({cpu})")

        waits = recent[recent["kind"] == Kind.GATE_WAIT]
        if len(waits):
            print(f"\n  gate waits ({len(waits)}):")
            for r in waits[np.argsort(-waits["a"])][: args.top]:
                print(
                    f"    {ms(r['a']):>10}  {ring.name(r['name_id'])}"
                    f" behind {ring.name(r['detail'])}"
                )

        stalls = recent[recent["kind"] == Kind.FRAME_OVERRUN]
        throws = recent[recent["kind"] == Kind.THROW]
        print(f"\n  frames over the bar: {len(stalls)}    first-chance throws: {len(throws)}")
        return 0


def cmd_stalls(args: argparse.Namespace) -> int:
    """
    Every frame over the bar, worst first, one line each — and the outages that never ended.

    A stall is recorded when the drawing thread COMES BACK, because only then is its length known.
    So an outage that has not ended yet, or that had not ended when the app was closed, is missing
    from that list entirely — and those are the worst ones. The watchdog says so separately, every
    sixteen milliseconds, for as long as the thread is gone, and that reading is reported first: a
    run whose longest recorded stall was 9.8 seconds had the thread missing for a hundred and
    fifteen, and only one of those two numbers describes what it was like to sit in front of.
    """
    with Ring(args.dir) as ring:
        records = ring.records()

        missing = records[records["kind"] == Kind.DISPATCH_DEPTH]
        if len(missing):
            worst = float(missing["a"].max())
            if worst >= args.over * 1_000_000:
                at = (int(missing[int(np.argmax(missing["a"]))]["nanos"]) - ring.mono_start) / 1e9
                print(f"WORST OUTAGE: the drawing thread was gone for {ms(worst)}, "
                      f"last seen missing at {at:,.0f}s")
                over_a_second = missing[missing["a"] >= 1_000_000_000]
                if len(over_a_second):
                    print(f"  it was missing for over a second in {len(over_a_second):,} of the "
                          f"{len(missing):,} times it was checked")
                print()

        stalls = records[records["kind"] == Kind.FRAME_OVERRUN]
        stalls = stalls[stalls["a"] >= int(args.over * 1_000_000)]
        if len(stalls) == 0:
            print(f"no frames over {args.over:g}ms came back to be measured")
            return 0

        stalls = stalls[np.argsort(-stalls["a"])][: args.top]

        print(f"{'id':>6}  {'took':>10}  {'at':>10}  what was in it")
        for r in stalls:
            at = (int(r["nanos"]) - ring.mono_start) / 1_000_000_000
            during = records[
                (records["nanos"] >= int(r["nanos"]) - int(r["a"]))
                & (records["nanos"] <= int(r["nanos"]))
            ]
            held = during[during["kind"] == Kind.GATE_HOLD]
            throws = during[during["kind"] == Kind.THROW]

            bits = []
            if len(held):
                worst = held[np.argmax(held["a"])]
                bits.append(f"gate {ms(worst['a'])} in {ring.name(worst['name_id'])}")
            if len(throws):
                bits.append(f"{len(throws)} throws")
            if not bits:
                bits.append("nothing of ours ran, see `rpmscope why`")

            print(f"{int(r['seq']):>6}  {ms(r['a']):>10}  {at:>9.1f}s  {', '.join(bits)}")
        return 0


def cmd_why(args: argparse.Namespace) -> int:
    """The chain for one stall. The product of this whole tool."""
    with Ring(args.dir) as ring:
        try:
            print(explain(ring, args.id))
        except LookupError as e:
            print(e, file=sys.stderr)
            return 1
    return 0


def cmd_throws(args: argparse.Namespace) -> int:
    """First-chance exceptions by type, by line, and by who asked. Four hundred in a second is a
    design, not an error rate — and the type on its own has twice been the half that was never in
    doubt. The grouping is in chain.summarise_throws, where it can be tested."""
    from .chain import summarise_throws

    with Ring(args.dir) as ring:
        records = ring.records()
        throws = records[records["kind"] == Kind.THROW]
        if len(throws) == 0:
            print("no throws recorded")
            return 0

        seen = summarise_throws(records, ring.name)
        span = (int(throws["nanos"].max()) - int(throws["nanos"].min())) / 1e9 or 1

        print(f"{len(throws):,} throws over {span:.1f}s ({len(throws) / span:,.0f}/s):")
        for name, count in sorted(seen.by_type.items(), key=lambda kv: -kv[1])[: args.top]:
            print(f"  {count:>7,}  {name}")

        if seen.asked_by:
            print("\nasked for by:")
            for name, count in sorted(seen.asked_by.items(), key=lambda kv: -kv[1])[: args.top]:
                print(f"  {count:>7,}  {name}")

        if seen.by_site:
            print("\nby the line that threw:")
            for name, count in sorted(seen.by_site.items(), key=lambda kv: -kv[1])[: args.top]:
                print(f"  {count:>7,}  {name}")
                for step in seen.chains.get(name, "").split(" < ")[1:]:
                    print(f"           < {step}")
        return 0

def cmd_stopped(args: argparse.Namespace) -> int:
    """
    How much of the wall clock the process spent with every thread suspended.

    The one number that settles an argument about a runtime that is not running. A thread parked at
    1% processor and a world that is stopped look identical from inside the app, and only this can
    tell them apart — it is measured from the collector's stream, which is written by another
    process and so keeps arriving while this one is stopped.

    Suspensions are paired with the restart that follows them, because the runtime reports the two
    ends as separate events and the length is the gap between them.
    """
    try:
        from .ring import collector_for, newest_ring

        app_ring = newest_ring(args.dir)
        if app_ring is None:
            print("no app ring yet", file=sys.stderr)
            return 1
        ring = Ring(args.dir, collector_for(app_ring))
    except FileNotFoundError:
        print("no collector ring: run tools/RpmScope.Collector against the app first", file=sys.stderr)
        return 1

    with ring:
        records = ring.records()
        if not len(records):
            print("the collector ring is empty")
            return 0

        span = (int(records["nanos"].max()) - int(records["nanos"].min())) / 1e9 or 1.0
        starts = records[records["kind"] == Kind.GC_SUSPEND]
        ends = records[records["kind"] == Kind.GC_RESTART]

        if not len(starts):
            print(f"over {span:,.0f}s the world was never stopped.")
            return 0

        begun = starts["nanos"].astype(np.int64)
        over = ends["nanos"].astype(np.int64)
        following = np.searchsorted(over, begun, side="left")
        paired = following < len(over)
        held = (over[following[paired]] - begun[paired]) / 1e6
        held = held[(held >= 0) & (held < 60_000)]

        if not len(held):
            print(f"{len(starts):,} suspensions over {span:,.0f}s, none of them paired with a restart")
            return 0

        share = held.sum() / 10 / span
        print(f"the world was stopped {held.sum() / 1000:,.2f}s of {span:,.0f}s"
              f"   =  {share:.1f}% of wall time")
        print(f"  {len(held):,} suspensions   ({len(held) / span:,.0f}/s)")
        print(f"  mean {held.mean():.2f}ms   p99 {np.percentile(held, 99):.1f}ms"
              f"   worst {held.max():,.1f}ms")

        reasons: dict[str, int] = {}
        for r in starts:
            reasons[ring.name(r["name_id"]) or "?"] = reasons.get(ring.name(r["name_id"]) or "?", 0) + 1
        print("  " + ", ".join(f"{n:,} {k}" for k, n in sorted(reasons.items(), key=lambda kv: -kv[1])))

        if reasons.get("SuspendOther", 0) > len(held) * 0.5:
            print()
            print("  NOTE: most of these say SuspendOther, which is what the sample profiler")
            print("  looks like. If the collector is running with --stacks, this reading is")
            print("  measuring the instrument. Run it without --stacks to measure the app.")

        if share > 5:
            print()
            print(f"  {share:.0f}% is enough to be the whole of a stall. A thread that is not")
            print("  running because the world is stopped cannot be helped by faster work.")
        return 0


def cmd_log(args: argparse.Namespace) -> int:
    """The lines the app said, in wall-clock time, with the stalls they belong beside.

    These used to go to the debugger's output window. They do not any more, and the reason is the
    subject of the rest of this tool: a line reaching an attached debugger is a round trip to it, and
    a diagnostic that writes one per realized row stops the process a few hundred times a second. In
    the ring a line costs one record and lands on the same clock as everything else — so a stall and
    the lines around it are read TOGETHER, which is what was actually wanted from a log all along.

    The Closing record is shown too, as "closing", whatever the channel filter: it is written when the
    app closes normally, so it is the last line of a run that did not crash.
    """
    with Ring(args.dir) as ring:
        records = ring.records()
        said = records[(records["kind"] == Kind.SAID) | (records["kind"] == Kind.CLOSING)]

        if args.channel:
            wanted = set(args.channel)
            said = said[[int(r["kind"]) == Kind.CLOSING or (ring.name(int(r["name_id"])) or "") in wanted
                         for r in said]]

        if len(said) == 0:
            channels = sorted({ring.name(int(r["name_id"])) or "?"
                               for r in records[records["kind"] == Kind.SAID]})
            print("nothing said" + (f" on that channel. There is: {', '.join(channels)}"
                                    if channels else ""))
            return 0

        said = said[-args.tail:]
        for r, unix in zip(said, ring.unix_seconds(said["nanos"])):
            when = time.strftime("%H:%M:%S", time.localtime(unix))
            if int(r["kind"]) == Kind.CLOSING:
                print(f"{when}.{int(unix * 1000) % 1000:03d}  {'closing':<9.9s}  normal close")
                continue
            print(f"{when}.{int(unix * 1000) % 1000:03d}  "
                  f"{ring.name(int(r['name_id'])) or '?':<9.9s}  {ring.name(int(r['detail'])) or ''}")
        return 0


def cmd_export(args: argparse.Namespace) -> int:
    """The ring as a Perfetto trace. See perfetto.py for why this exists rather than a nicer window."""
    from .perfetto import export

    with Ring(args.dir) as ring:
        written = export(ring, args.out)

    if not written:
        print("nothing in the ring to export")
        return 1

    print(f"{written:,} events → {args.out}")
    print("open https://ui.perfetto.dev and drop the file on it (it stays on your machine)")
    return 0


def cmd_catch(args: argparse.Namespace) -> int:
    """Wait for the drawing thread to actually be gone, then take the stacks. See catch.py."""
    from .catch import watch

    return watch(args.dir, args.stuck, args.out, args.seconds)


def cmd_sample(args: argparse.Namespace) -> int:
    """Sample the drawing thread repeatedly during a stall. Needs a debug session."""
    from .sample import during

    return during(args.port, args.over, args.samples, directory=args.dir)


def cmd_probe(args: argparse.Namespace) -> int:
    """Take every thread's stack during a stall, without pausing the app."""
    from .probe import hunt

    return hunt(args.over, args.times, args.patience, args.dir)


def cmd_view(args: argparse.Namespace) -> int:
    """The window. Imported here so the rest of the tool needs no Qt."""
    from .viewer import run

    return run(args.dir, args.seconds)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="rpmscope", description="Reads RPM's Scope rings.")
    parser.add_argument("--dir", default=None, help="where the rings are (default: RPM's cache)")
    sub = parser.add_subparsers(dest="command", required=True)

    top = sub.add_parser("top", help="one snapshot of the last few seconds")
    top.add_argument("--seconds", type=float, default=10)
    top.add_argument("--top", type=int, default=8)
    top.set_defaults(func=cmd_top)

    stalls = sub.add_parser("stalls", help="every frame over the bar, worst first")
    stalls.add_argument("--over", type=float, default=150, help="milliseconds")
    stalls.add_argument("--top", type=int, default=20)
    stalls.set_defaults(func=cmd_stalls)

    why = sub.add_parser("why", help="the wait chain for one stall")
    why.add_argument("id", type=int, help="the id from `rpmscope stalls`")
    why.set_defaults(func=cmd_why)

    throws = sub.add_parser("throws", help="first-chance exceptions by type")
    throws.add_argument("--top", type=int, default=10)
    throws.set_defaults(func=cmd_throws)

    stopped = sub.add_parser(
        "stopped", help="how much wall time the runtime spent with every thread suspended")
    stopped.set_defaults(func=cmd_stopped)

    log = sub.add_parser("log", help="the lines the app said, in wall-clock time")
    log.add_argument(
        "--channel", action="append",
        help="only this channel: walldiag, db, net, throws (repeatable)")
    log.add_argument("--tail", type=int, default=200, help="how many lines (default 200)")
    log.set_defaults(func=cmd_log)

    export = sub.add_parser("export", help="write the ring as a Perfetto trace")
    export.add_argument("--out", default="/tmp/rpm-trace.json", help="where to write it")
    export.set_defaults(func=cmd_export)

    catch = sub.add_parser(
        "catch",
        help="wait for the ui to actually be gone, then sample it once")
    catch.add_argument(
        "--stuck", type=float, default=1500,
        help="milliseconds the drawing thread must have been missing (default 1500)")
    catch.add_argument("--out", default="/tmp/rpm-stall.txt", help="where to write the sample")
    catch.add_argument(
        "--seconds", type=float, default=900, help="give up after this long (default 900)")
    catch.set_defaults(func=cmd_catch)

    sample = sub.add_parser(
        "sample", help="sample the drawing thread over and over DURING a stall, and count what it is in")
    sample.add_argument("--over", type=float, default=1200, help="milliseconds gone before sampling")
    sample.add_argument("--samples", type=int, default=20)
    sample.add_argument("--port", type=int, default=10101)
    sample.set_defaults(func=cmd_sample)

    probe = sub.add_parser(
        "probe", help="stacks during a stall, taken from outside without pausing the app")
    probe.add_argument("--over", type=float, default=1200)
    probe.add_argument("--times", type=int, default=6)
    probe.add_argument("--patience", type=float, default=300)
    probe.set_defaults(func=cmd_probe)

    view = sub.add_parser("view", help="the window: timeline, counters, now, blame")
    view.add_argument("--seconds", type=float, default=30, help="how much time is on screen")
    view.set_defaults(func=cmd_view)

    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except FileNotFoundError:
        print("no ring found, is rpm running, or is RPM_SCOPE_DIR set?", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
