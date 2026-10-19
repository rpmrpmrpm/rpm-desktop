"""
The window: what every thread is doing, where the stalls are, and what was in the way.

Reads the rings read-only and draws them on one wall-clock axis. It never writes, never connects and
cannot be noticed by the app, which is the property that matters — it goes on working while the app
is frozen, and frozen is when anybody opens it.

**What is on screen, and why in that order.**

* **The timeline**, grouped either by what happened or by which thread it happened on. The second is
  the one that answers "what is every thread doing": a row per thread, so a thread that is busy, a
  thread that is waiting and a thread that is doing nothing are three different pictures rather than
  three readings held in the head at once.
* **Counts on every row label.** "gate held" means nothing; "gate held  41 · 8.2s" is a fact.
* **A wall-clock axis**, because the question is always "what happened when I clicked at 23:41", and
  seconds-since-launch cannot answer it.
* **Drag to select a period.** The selection is summarised in words — how long the ui was
  unavailable inside it, whether it was running or parked, who held what — which is the reading that
  turns a picture into something you can put in a bug report.
* **Counters** as stacked sparklines, each in its own band against its own maximum, with its current
  value printed. One y-axis shared between milliseconds, percentages, megabytes and queue depths is
  unreadable, and making it readable was the whole reason to give each its own band.

Nothing here knows anything about rpm. The lanes are a table, the record layout is the contract, and
another program that writes the same rings gets the same window.
"""

from __future__ import annotations

import sys
import time
from dataclasses import dataclass

import numpy as np
import pyqtgraph as pg
from PySide6 import QtCore, QtGui, QtWidgets

from . import palette as C
from .chain import explain
from .ring import RECORD, Kind, Ring, collector_for, default_dir, newest_ring

#: Marks a merged record as the collector's, so its names are read from the collector's table.
#: Set by the reader, never by a writer — the app's own flags field does not use this bit.
FROM_COLLECTOR = 0x100


@dataclass(frozen=True)
class Lane:
    """One row of the timeline: what it collects, what it is called, how it is drawn."""

    key: str
    label: str
    kinds: tuple[int, ...]
    colour: str
    #: bars carry a duration in `a` and end at their timestamp; ticks are instants.
    bars: bool
    #: what the count means, said in the panel rather than left to be guessed
    means: str


LANES: tuple[Lane, ...] = (
    Lane("input", "input", (Kind.INPUT,), C.USER, False,
         "pointer, key and wheel events, taken with handledEventsToo so nothing swallowed is missed. "
         "the reference row: everything else is read against when somebody actually did something"),
    Lane("stall", "ui thread blocked", (Kind.FRAME_OVERRUN,), C.STALL, True,
         "DispatcherQueue round-trip overrun. a watchdog thread posts a stamped item every 16ms and "
         "records how late it came back, so the measurement is not taken on the thread being measured. "
         "a = wall time missing, b = the ui thread's own cpu delta over it (busy vs parked)"),
    Lane("hold", "db gate held", (Kind.GATE_HOLD,), C.HOLDING, True,
         "a LibraryDatabase connection held exclusively. the name is the caller that had it. "
         "sqlite is one writer, so a hold here is every other write queued behind it"),
    Lane("wait", "db gate waited", (Kind.GATE_WAIT,), C.WAITING, True,
         "time spent queued for that connection before getting it. the name beside it is who was "
         "holding, not who was waiting"),
    Lane("gc", "EE suspended", (Kind.GC_SUSPEND,), C.SUSPEND, False,
         "ThreadSuspend::SuspendEE — every managed thread stopped. from GCSuspendEEBegin over "
         "EventPipe, so it needs the collector attached. note the runtime charges ANY suspension to "
         "GC.GetTotalPauseDuration, including a profiler's and a debugger's"),
    Lane("lock", "monitor contention", (Kind.CONTENTION,), C.LOCK, True,
         "Monitor waits that blocked, from the runtime's ContentionStart/Stop. locks outside the "
         "database's own gates — those are the two rows above"),
    Lane("net", "http", (Kind.HTTP_REQUEST, Kind.HTTP_CONNECTION), C.NETWORK, False,
         "requests started and connections opened, from System.Net.Http's own EventSource"),
    Lane("throw", "first-chance throws", (Kind.THROW,), C.THROW, False,
         "AppDomain.FirstChanceException, whether or not anything caught it. under a debugger each "
         "one blocks the throwing thread in Debugger::SendExceptionHelperAndBlock until vsdbg "
         "answers — on the ui thread that is the frame gone"),
    Lane("said", "trace lines", (Kind.SAID,), C.QUIET, False,
         "Scope.Said: what walldiag, db, net and throws used to write to Debug.WriteLine. here "
         "instead because that call is a debugger round trip per line. the text is in the pane below; "
         "this row is only WHEN they were written"),
)

#: The kinds that carry a thread, and so can be laid out per thread.
THREAD_BARS: tuple[int, ...] = (
    Kind.FRAME_OVERRUN, Kind.GATE_HOLD, Kind.GATE_WAIT, Kind.CONTENTION,
)
THREAD_MARKS: tuple[int, ...] = (
    Kind.WAIT_HANDLE, Kind.THROW, Kind.SAMPLE, Kind.INPUT, Kind.HTTP_REQUEST, Kind.SAID,
)


def _span(nanos: float) -> str:
    """A duration, in the largest unit that keeps it readable."""
    ms = nanos / 1e6
    if ms >= 10_000:
        return f"{ms / 1000:,.1f}s"
    if ms >= 1:
        return f"{ms:,.0f}ms"
    return f"{ms:.2f}ms"


def _at(unix: float) -> str:
    return time.strftime("%H:%M:%S", time.localtime(unix))


class WallClock(pg.AxisItem):
    """
    The axis, in the time the clock on the wall says.

    Seconds-since-launch cannot answer the only question anybody asks of a trace — "what happened
    when I clicked at twenty to twelve" — so the axis is unix seconds and reads as a time of day.
    """

    def tickStrings(self, values, scale, spacing):  # noqa: N802  (pyqtgraph's spelling)
        out = []
        for v in values:
            stamp = time.strftime("%H:%M:%S", time.localtime(v))
            if spacing < 1:
                stamp += f"{v - int(v):.2f}"[1:]
            out.append(stamp)
        return out


class Rings:
    """Both rings, opened lazily, reopened when replaced, and put on one wall clock."""

    def __init__(self, directory: str | None) -> None:
        self.dir = directory or default_dir()
        self.app: Ring | None = None
        self.collector: Ring | None = None
        self._app_seen = 0
        self._collector_seen = 0
        self.note = ""

    def _open(self, name: str) -> Ring | None:
        try:
            return Ring(self.dir, name)
        except (FileNotFoundError, ValueError):
            return None

    def fresh(self) -> np.ndarray:
        """
        Only what has arrived since the last ask.

        Incremental on purpose. The collector can write thousands of records a second into a ring of
        four million, and copying all of them out ten times a second would leave no time to draw. A
        tool whose one job is to stay responsive while the app it watches is frozen has exactly one
        unacceptable failure, and that is freezing.
        """
        parts: list[np.ndarray] = []
        notes: list[str] = []

        app_ring = newest_ring(self.dir)
        if app_ring is None:
            self.note = "no scope.*.ring yet, is the app running?"
            return np.empty(0, dtype=RECORD)

        for attr, name, seen in (
            ("app", app_ring, "_app_seen"),
            ("collector", collector_for(app_ring), "_collector_seen"),
        ):
            ring: Ring | None = getattr(self, attr)

            if ring is not None and ring.written < getattr(self, seen):
                ring.close()          # replaced under us: a fresh run of the app, or of the collector
                ring = None
                setattr(self, seen, 0)

            if ring is None:
                ring = self._open(name)
                setattr(self, attr, ring)

            if ring is None:
                continue

            rows = ring.records(getattr(self, seen))
            setattr(self, seen, ring.written)

            if len(rows):
                if int(rows["name_id"].max()) > len(ring.names):
                    ring.refresh_names()

                # Which table this row's names live in. The two rings number their words
                # independently — they must, or two writers under two locks hand out one id for two
                # words — so a merged record has to carry where it came from. The flags field is
                # the app's and it never sets this bit.
                if attr == "collector":
                    rows = rows.copy()
                    rows["flags"] |= FROM_COLLECTOR

                parts.append(rows)

            notes.append(f"{attr} {ring.written:,}")

        self.note = "   ".join(notes) or "no rings yet, is the app running?"

        if not parts:
            return np.empty(0, dtype=RECORD)

        joined = np.concatenate(parts)
        return joined[np.argsort(joined["nanos"], kind="stable")]

    def name(self, name_id, flags=0) -> str | None:
        """The word for an id, out of the table that issued it. See FROM_COLLECTOR."""
        ring = self.collector if int(flags) & FROM_COLLECTOR else self.app
        return ring.name(int(name_id)) if ring is not None else None

    def clock(self, nanos) -> np.ndarray:
        """
        Monotonic nanoseconds onto unix seconds.

        One ring's heartbeats convert both, because the monotonic clock belongs to neither
        process — which is the whole reason it was chosen. See Ring.unix_seconds for the sleep.
        """
        ring = self.app or self.collector
        if ring is None:
            return np.asarray(nanos, dtype=float) / 1e9
        return ring.unix_seconds(nanos)


class Timeline(pg.PlotWidget):
    """The rows, and the period selected across them."""

    picked = QtCore.Signal(float)
    chose = QtCore.Signal(float, float)

    #: The most marks any row draws. Above it the row is thinned: sixty thousand identical dots say
    #: nothing that six hundred do not, and cost the frame they are drawn in.
    MOST_MARKS = 1500

    def __init__(self) -> None:
        super().__init__(background=C.GROUND, axisItems={"bottom": WallClock(orientation="bottom")})

        self.setMenuEnabled(False)
        self.showGrid(x=True, y=False, alpha=0.15)
        self.setMouseEnabled(x=True, y=False)
        self.plotItem.hideButtons()
        self.plotItem.vb.disableAutoRange()

        # Read downwards, in the order the lanes are written. Nought is the bottom of a plot and the
        # top of a list, and a diagnostic where the first row is at the bottom is one you read wrong
        # every time you glance at it.
        self.plotItem.vb.invertY(True)

        for axis in ("left", "bottom"):
            self.getAxis(axis).setPen(pg.mkPen(C.EDGE))
            self.getAxis(axis).setTextPen(pg.mkPen(C.QUIET))
        self.getAxis("left").setWidth(190)

        self._shape: list[tuple[str, str, bool]] = []
        self._bars: list[pg.BarGraphItem] = []
        self._marks: list[pg.ScatterPlotItem] = []
        self._bands: list[pg.LinearRegionItem] = []

        # Drag across the plot to choose a period. Movable afterwards, because the first drag is
        # never quite the window you wanted and re-dragging is how a tool teaches you not to bother.
        # Alpha set on the colour rather than written into the hex string: an eight-digit #RRGGBBAA
        # is not what QColor parses from a name, and the one it made instead was opaque — the
        # selection came out as a solid slab over the very rows it was meant to be measuring.
        tint = QtGui.QColor(C.PICKED)
        tint.setAlpha(28)
        hover = QtGui.QColor(C.PICKED)
        hover.setAlpha(46)

        self.region = pg.LinearRegionItem(
            brush=pg.mkBrush(tint), pen=pg.mkPen(C.PICKED, width=1), hoverBrush=pg.mkBrush(hover))
        self.region.setZValue(40)
        self.region.hide()
        self.region.sigRegionChanged.connect(self._moved)
        self.addItem(self.region)

        self._cursor = pg.InfiniteLine(angle=90, movable=False, pen=pg.mkPen(C.PICKED, width=1))
        self._cursor.setZValue(45)
        self._cursor.hide()
        self.addItem(self._cursor)

        self.scene().sigMouseClicked.connect(self._clicked)

    def lay_out(self, shape: list[tuple[str, str, bool]]) -> None:
        """
        Gives the plot its rows.

        Does nothing when the shape has not changed, and that is what stopped the flicker: rebuilding
        eight bar items and eight scatter items ten times a second means the scene is empty for part
        of every frame, which is exactly what a flicker is.
        """
        if shape == self._shape:
            return

        for item in self._bars + self._marks + self._bands:
            self.removeItem(item)
        self._bars.clear()
        self._marks.clear()
        self._bands.clear()
        self._shape = shape

        for i, (_, colour, _) in enumerate(shape):
            band = pg.LinearRegionItem(
                values=(i - 0.4, i + 0.4), orientation="horizontal", movable=False,
                brush=pg.mkBrush(QtGui.QColor(C.PANEL)), pen=pg.mkPen(None))
            band.setMovable(False)
            band.setZValue(-40)
            self.addItem(band)
            self._bands.append(band)

            bar = pg.BarGraphItem(x0=[], x1=[], y=[], height=0.6,
                                  brush=pg.mkBrush(colour), pen=pg.mkPen(None))
            bar.setZValue(10)
            self.addItem(bar)
            self._bars.append(bar)

            mark = pg.ScatterPlotItem(x=[], y=[], size=6, symbol="o", pxMode=True,
                                      brush=pg.mkBrush(colour), pen=pg.mkPen(None))
            mark.setZValue(10)
            self.addItem(mark)
            self._marks.append(mark)

        self.setLimits(yMin=-0.8, yMax=len(shape) - 0.2)
        self.setYRange(-0.8, len(shape) - 0.2, padding=0)

    def label(self, texts: list[str]) -> None:
        self.getAxis("left").setTicks([[(i, t) for i, t in enumerate(texts)]])

    def draw_row(self, index: int, at: np.ndarray, took: np.ndarray | None) -> None:
        if took is not None and len(at):
            # A record is stamped when the thing ENDED and carries how long it took, so its bar runs
            # BACKWARDS from the stamp. Drawn forwards, every cause sits after its own effect.
            self._bars[index].setOpts(
                x0=at - took, x1=np.maximum(at, at - took + 1e-4),
                y=np.full(len(at), index, dtype=float), height=0.6)
            self._marks[index].setData(x=[], y=[])
        elif took is not None:
            self._bars[index].setOpts(x0=[], x1=[], y=[], height=0.6)
            self._marks[index].setData(x=[], y=[])
        else:
            if len(at) > self.MOST_MARKS:
                at = at[:: len(at) // self.MOST_MARKS + 1]
            self._marks[index].setData(x=at, y=np.full(len(at), index, dtype=float))
            self._bars[index].setOpts(x0=[], x1=[], y=[], height=0.6)

    def _clicked(self, event) -> None:
        point = self.plotItem.vb.mapSceneToView(event.scenePos())
        self.picked.emit(float(point.x()))

    def _moved(self) -> None:
        low, high = self.region.getRegion()
        self.chose.emit(float(low), float(high))

    def mark(self, at: float) -> None:
        self._cursor.setPos(at)
        self._cursor.show()


class Counters(pg.PlotWidget):
    #: How many bands it has to show, so the window can give it exactly that much room and no more.
    wanted_height = QtCore.Signal(int)

    """
    The numbers, each in a band of its own.

    One y-axis shared between milliseconds, percentages, megabytes and queue depths is unreadable,
    and it was. Every track is normalised into its own band against its own maximum in view, with its
    name and current value printed beside it: a counter is read for its SHAPE, and the one number
    that matters is what it is at now.
    """

    #: Tracks whose readings are events rather than a continuous signal, and are drawn as spikes.
    IMPULSES = frozenset({"ui stall"})

    TRACKS: tuple[tuple[str, str, str], ...] = (
        ("ui stall", "ms", C.STALL),
        ("threadpool-queue-length", "waiting", C.HOLDING),
        ("cpu-usage", "%", C.USER),
        ("alloc-rate", "B/s", C.WORK),
        ("gc-heap-size", "MB", C.NETWORK),
        ("monitor-lock-contention-count", "total", C.LOCK),
    )

    def __init__(self) -> None:
        super().__init__(background=C.GROUND, axisItems={"bottom": WallClock(orientation="bottom")})
        self.setMenuEnabled(False)
        self.setMouseEnabled(x=True, y=False)
        self.plotItem.hideButtons()
        self.plotItem.vb.disableAutoRange()
        self.showGrid(x=True, y=False, alpha=0.15)

        for axis in ("left", "bottom"):
            self.getAxis(axis).setPen(pg.mkPen(C.EDGE))
            self.getAxis(axis).setTextPen(pg.mkPen(C.QUIET))
        self.getAxis("left").setWidth(190)
        self.getAxis("left").setTicks([[]])

        self._curves: list[pg.PlotDataItem] = []
        self._labels: list[pg.TextItem] = []
        self._bands: list[pg.LinearRegionItem] = []
        self._shown: list[str] = []

    def lay_out(self, names: list[str]) -> None:
        """
        Gives the strip a band per counter that actually has readings.

        A band reading "no readings" is worse than no band: it takes the same room as one that says
        something, and there are five of them whenever the collector is not attached. Rebuilt only
        when the SET changes, which is about once a session, so it costs nothing per frame.
        """
        if names == self._shown:
            return

        for item in self._curves + self._labels + self._bands:
            self.removeItem(item)
        self._curves.clear()
        self._labels.clear()
        self._bands.clear()
        self._shown = list(names)

        colours = {n: c for n, _u, c in self.TRACKS}

        for i, name in enumerate(self._shown):
            colour = colours.get(name, C.WORK)

            band = pg.LinearRegionItem(
                values=(i, i + 0.88), orientation="horizontal", movable=False,
                brush=pg.mkBrush(QtGui.QColor(C.PANEL)), pen=pg.mkPen(None))
            band.setZValue(-40)
            self.addItem(band)
            self._bands.append(band)

            under = QtGui.QColor(colour)
            under.setAlpha(46)
            curve = self.plot([], [], pen=pg.mkPen(colour, width=1.5),
                              fillLevel=i, brush=pg.mkBrush(under))
            self._curves.append(curve)

            text = pg.TextItem("", color=C.QUIET, anchor=(0, 1))
            text.setZValue(30)
            self.addItem(text)
            self._labels.append(text)

        self.setLimits(yMin=-0.15, yMax=max(len(self._shown), 1))
        self.setYRange(-0.15, max(len(self._shown), 1), padding=0)

    @staticmethod
    def _spikes(xs: np.ndarray, ys: np.ndarray, floor: float) -> tuple[np.ndarray, np.ndarray]:
        """One reading becomes three points — floor, value, floor — so it stands up on its own."""
        x3 = np.repeat(xs, 3)
        y3 = np.empty(len(xs) * 3, dtype=float)
        y3[0::3] = floor
        y3[1::3] = ys
        y3[2::3] = floor
        return x3, y3

    def draw(self, records: np.ndarray, clock: np.ndarray, rings: Rings,
             view: tuple[float, float]) -> None:
        counters = records[records["kind"] == Kind.COUNTER]
        counter_clock = clock[records["kind"] == Kind.COUNTER]
        names = (np.array([rings.name(c["name_id"], c["flags"]) or "" for c in counters])
                 if len(counters) else np.array([]))

        stalls = records[records["kind"] == Kind.FRAME_OVERRUN]
        stall_clock = clock[records["kind"] == Kind.FRAME_OVERRUN]

        have: list[str] = []
        series: dict[str, tuple[np.ndarray, np.ndarray]] = {}

        for name, _unit, _colour in self.TRACKS:
            if name == "ui stall":
                xs = stall_clock
                ys = stalls["a"] / 1e6 if len(stalls) else np.array([])
            elif len(names):
                pick = names == name
                xs, ys = counter_clock[pick], counters["a"][pick] / 1000
            else:
                xs, ys = np.array([]), np.array([])

            if len(xs):
                have.append(name)
                series[name] = (np.asarray(xs, dtype=float), np.asarray(ys, dtype=float))

        if have != self._shown:
            self.lay_out(have)
            self.wanted_height.emit(max(1, len(have)))
        else:
            self.lay_out(have)

        units = {n: u for n, u, _c in self.TRACKS}
        for i, name in enumerate(self._shown):
            xs, ys = series[name]
            top = float(np.max(ys)) or 1.0
            height = i + (ys / top) * 0.84

            if name in self.IMPULSES:
                # Drawn as spikes, not as a line. A stall is an EVENT: joining two of them with a
                # slope draws a ramp that says the stalls grew into each other, and they did not —
                # there were two, minutes apart, and the line between them is a picture of nothing.
                xs, height = self._spikes(xs, height, i)

            self._curves[i].setData(xs, height)

            now = float(ys[-1])
            peak = f"      peak {top:,.0f}" if top > now * 1.2 else ""
            self._labels[i].setText(f"{name}   {now:,.0f} {units[name]}{peak}", color=C.SAID)
            self._labels[i].setPos(view[0], i + 0.9)


class LogPane(QtWidgets.QWidget):
    """
    The lines the app said, for the period on screen.

    <b>Why they are here at all.</b> They used to go to the debugger's output window, and that is a
    round trip to the debugger per line — the thing this whole tool exists to find. In the ring they
    cost one record and, more to the point, they land on the SAME CLOCK as the stalls and throws they
    are about. So a stall is not read in one window and its explanation in another: drag across the
    stall and this shows what the app was saying while it happened.
    """

    def __init__(self) -> None:
        super().__init__()
        self.channels: set[str] = set()
        self.wanted: set[str] = set()
        self._shown: str | None = None

        self.title = QtWidgets.QLabel("trace lines")
        self.title.setObjectName("section")

        self.filters = QtWidgets.QWidget()
        self._filter_row = QtWidgets.QHBoxLayout(self.filters)
        self._filter_row.setContentsMargins(0, 0, 0, 0)
        self._filter_row.setSpacing(5)
        self._filter_row.addStretch(1)

        self.text = QtWidgets.QPlainTextEdit()
        self.text.setReadOnly(True)
        # Wrapped, not scrolled sideways. A walldiag line is two hundred characters and a net line is
        # thirty, so with a horizontal scrollbar the moment you scroll right to read the end of a long
        # one every short one goes blank — which reads as the log being full of empty lines.
        self.text.setLineWrapMode(QtWidgets.QPlainTextEdit.WidgetWidth)
        self.text.document().setDocumentMargin(0)

        head = QtWidgets.QHBoxLayout()
        head.setContentsMargins(0, 0, 0, 0)
        head.addWidget(self.title)
        head.addWidget(self.filters, 1)

        box = QtWidgets.QVBoxLayout(self)
        box.setContentsMargins(0, 0, 0, 0)
        box.setSpacing(6)
        box.addLayout(head)
        box.addWidget(self.text, 1)

    def offer(self, channels: set[str]) -> None:
        """A button per channel, made the first time that channel says anything. Made rather than
        listed, because which channels a run has depends on what was switched on in it."""
        for channel in sorted(channels - self.channels):
            self.channels.add(channel)
            self.wanted.add(channel)
            button = QtWidgets.QPushButton(channel)
            button.setCheckable(True)
            button.setChecked(True)
            button.setProperty("chip", True)
            button.toggled.connect(lambda on, c=channel: self._toggle(c, on))
            self._filter_row.insertWidget(self._filter_row.count() - 1, button)

    def _toggle(self, channel: str, on: bool) -> None:
        self.wanted.add(channel) if on else self.wanted.discard(channel)
        self._shown = None       # the next beat has something different to say

    def show_lines(self, lines: list[tuple[float, str, str]], period: str) -> None:
        kept = [(t, c, w) for t, c, w in lines if c in self.wanted]
        self.title.setText(f"trace lines  ·  {len(kept):,}  ·  {period}")

        if not kept:
            self._shown = None
            self.text.setPlainText(
                "No Scope.Said records in this range.\n\n"
                "Channels are gated per area by env var in .vscode/launch.json: RPM_WALL_DIAG (walls),\n"
                "RPM_DB_DIAG (LibraryDatabase gates), RPM_EXCEPTION_DIAG (per-site throw stacks).\n"
                "NetDiag writes unconditionally. With all off, only the kinds above are recorded.")
            return

        text = "\n".join(
            f"{_at(t)}.{int(t * 1000) % 1000:03d}  {c:<9.9s}  {w}" for t, c, w in kept)

        # NOT rebuilt unless it changed, because setPlainText puts the cursor back at the top and this
        # runs ten times a second: scroll up to read something and you were thrown back to the top
        # before you had read it. Following the newest line is still what happens while you are AT the
        # newest line, which is the only time it is wanted.
        if text == self._shown:
            return

        bar = self.text.verticalScrollBar()
        was_at_end = bar.value() >= bar.maximum() - 2
        where = bar.value()

        self._shown = text
        self.text.setPlainText(text)
        bar.setValue(bar.maximum() if was_at_end else min(where, bar.maximum()))


class Viewer(QtWidgets.QMainWindow):
    #: How much history the window keeps of its own. Longer than any range button offers.
    KEEP_SECONDS = 1800

    def __init__(self, directory: str | None, window_seconds: float) -> None:
        super().__init__()
        self.setWindowTitle("rpmscope")
        self.resize(1680, 1000)

        self.rings = Rings(directory)
        self.window_seconds = window_seconds
        self.follow = True
        self.by_thread = False
        self.selection: tuple[float, float] | None = None
        self._records: np.ndarray = np.empty(0, dtype=RECORD)

        self.timeline = Timeline()
        self.counters = Counters()
        self.counters.setXLink(self.timeline)
        self.timeline.picked.connect(self._pick)
        self.timeline.chose.connect(self._chose)

        # Taking hold of the view turns following OFF, and it has to, because the two are the same
        # gesture fighting: Live re-asserts the range ten times a second, so a drag or a wheel was
        # undone before the hand came off it. Pyqtgraph tells us when a range change was the USER's
        # rather than ours, which is exactly the distinction needed — nothing here has to guess.
        for plot in (self.timeline, self.counters):
            plot.plotItem.vb.sigRangeChangedManually.connect(self._took_hold)

        self.legend = self._panel()
        self.detail = self._panel()
        self._say_help()

        self.log = LogPane()

        middle = QtWidgets.QSplitter(QtCore.Qt.Vertical)
        middle.addWidget(self.timeline)
        middle.addWidget(self.counters)
        middle.addWidget(self.log)
        middle.setSizes([560, 180, 260])
        self._middle = middle
        self.counters.wanted_height.connect(self._fit_counters)

        split = QtWidgets.QSplitter(QtCore.Qt.Horizontal)
        split.addWidget(self.legend)
        split.addWidget(middle)
        split.addWidget(self.detail)
        split.setSizes([340, 920, 420])
        split.setStretchFactor(1, 1)

        body = QtWidgets.QWidget()
        layout = QtWidgets.QVBoxLayout(body)
        layout.setContentsMargins(12, 10, 12, 12)
        layout.setSpacing(10)
        layout.addWidget(self._bar())
        layout.addWidget(self._how())
        layout.addWidget(split, 1)
        self.setCentralWidget(body)
        self.setStyleSheet(self._style())

        self.beat = QtCore.QTimer(self)
        self.beat.timeout.connect(self.refresh)
        self.beat.start(100)   # ten a second: live, and slow enough that nothing is redrawn mid-read
        self.refresh()

    # --- chrome --------------------------------------------------------------------------------

    def _how(self) -> QtWidgets.QWidget:
        """
        One line saying what is on screen, permanently.

        A tool nobody can read is not a tool, and the two things a person needs before anything here
        means anything are: which way time runs, and what a row IS. Both were left to be worked out.
        """
        strip = QtWidgets.QLabel(
            "One row per record kind, all on CLOCK_UPTIME_RAW, so anything vertically aligned is "
            "concurrent.   Bars run backwards from their stamp: a record is written when the thing "
            "ENDED and carries its duration.   "
            "Drag to measure · click a bar on “ui thread blocked” for its chain.")
        strip.setObjectName("how")
        strip.setWordWrap(True)
        return strip

    def _panel(self) -> QtWidgets.QPlainTextEdit:
        box = QtWidgets.QPlainTextEdit()
        box.setReadOnly(True)
        box.document().setDocumentMargin(0)
        box.setLineWrapMode(QtWidgets.QPlainTextEdit.WidgetWidth)
        return box

    def _say_help(self) -> None:
        self.detail.setPlainText(
            "Nothing picked.\n\n"
            "Click a bar on “ui thread blocked” and its chain appears here: what was holding the "
            "drawing thread, who asked for it, and what else was going on at the time.\n\n"
            "Read the cpu delta first. Wall time with cpu to match is the thread RUNNING — the work is too "
            "slow and wants making cheaper. Wall time with no cpu is the thread PARKED — it was not "
            "doing the work at all, and making the work faster changes nothing. Identical from "
            "outside, opposite fixes.")

    def _bar(self) -> QtWidgets.QWidget:
        row = QtWidgets.QWidget()
        layout = QtWidgets.QHBoxLayout(row)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(6)

        self._live = QtWidgets.QPushButton("Live")
        self._live.setCheckable(True)
        self._live.setChecked(True)
        self._live.toggled.connect(self._set_follow)
        layout.addWidget(self._live)

        for label, seconds in (("30s", 30), ("2m", 120), ("10m", 600), ("30m", 1800)):
            button = QtWidgets.QPushButton(label)
            button.clicked.connect(lambda _=False, s=seconds: self._set_window(s))
            layout.addWidget(button)

        layout.addSpacing(20)

        self._grouping = QtWidgets.QPushButton("Group by thread")
        self._grouping.setCheckable(True)
        self._grouping.toggled.connect(self._set_grouping)
        layout.addWidget(self._grouping)

        clear = QtWidgets.QPushButton("Clear selection")
        clear.clicked.connect(self._clear_selection)
        layout.addWidget(clear)

        layout.addStretch(1)
        self.status = QtWidgets.QLabel("")
        self.status.setObjectName("status")
        layout.addWidget(self.status)
        return row

    def _style(self) -> str:
        return f"""
            QMainWindow, QWidget {{ background: {C.GROUND}; color: {C.SAID};
                font-family: '.AppleSystemUIFont', 'Helvetica Neue', sans-serif; font-size: 12px; }}
            QPlainTextEdit {{ background: {C.PANEL}; border: 1px solid {C.EDGE};
                border-radius: 10px; padding: 12px; color: {C.SAID};
                font-family: Menlo, monospace; font-size: 11px;
                selection-background-color: {C.SURFACE1}; }}
            QPushButton {{ background: {C.PANEL}; border: 1px solid {C.EDGE};
                border-radius: 7px; padding: 6px 13px; color: {C.QUIET}; }}
            QPushButton:hover {{ border-color: {C.OVERLAY0}; color: {C.SAID}; }}
            QPushButton:checked {{ background: {C.SURFACE1}; border-color: {C.OVERLAY1};
                color: {C.SAID}; }}
            QLabel#status {{ color: {C.FAINT}; }}
            QLabel#how {{ color: {C.FAINT}; padding: 2px 2px 0 2px; }}
            QLabel#section {{ color: {C.QUIET}; font-weight: 600; padding: 0 8px 0 2px; }}
            QPushButton[chip="true"] {{ padding: 2px 9px; border-radius: 9px;
                font-size: 11px; color: {C.FAINT}; }}
            QPushButton[chip="true"]:checked {{ color: {C.SAID}; background: {C.SURFACE0};
                border-color: {C.SURFACE2}; }}
            QSplitter::handle {{ background: {C.GROUND}; width: 10px; height: 10px; }}
        """

    def _fit_counters(self, bands: int) -> None:
        """
        The strip gets the room its bands need and the timeline gets the rest.

        Counters only appear when the collector is attached, and a fixed third of the window standing
        empty for the two thirds of the time it is not is the sort of thing that makes a tool feel
        like it is not working.
        """
        # THREE sizes, because the splitter holds three things. Setting two collapsed the log pane to
        # nothing and it looked like the feature had not been built — which is exactly how an empty
        # band makes a tool feel broken, the thing this method exists to avoid.
        sizes = self._middle.sizes()
        total = sum(sizes)
        wanted = min(60 + bands * 62, 460) if bands else 0
        log = sizes[2] if len(sizes) > 2 and sizes[2] > 0 else 260
        self._middle.setSizes([max(total - wanted - log, 200), wanted, log])

    def _took_hold(self, *_) -> None:
        """The view was moved by hand. Stop following, and say so on the button rather than leaving
        somebody to wonder why the picture stopped moving."""
        if self.follow:
            self._live.setChecked(False)   # which calls _set_follow

    def _set_follow(self, on: bool) -> None:
        self.follow = on
        if on:
            # Back to the present at the width that is on screen, so "Live" lands where the eye
            # expects rather than at whatever width was last chosen from the buttons.
            view = self.timeline.plotItem.vb.viewRange()[0]
            self.window_seconds = max(view[1] - view[0], 1.0)

    def _set_window(self, seconds: float) -> None:
        """
        How WIDE the window is. Not whether it follows — that is Live's, and only Live's.

        The two were the same button here, so choosing a width switched following back on and there was
        no way to say "show me half an hour, ending where I am looking". Turning Live off then worked
        and was undone by the very next thing anybody pressed, which reads as a toggle that does not
        stay put rather than as two settings quietly fighting.

        Held still, the width grows from the RIGHT edge, because that edge is where somebody has put
        the thing they are reading and widening should show more of what came before it rather than
        move it.
        """
        self.window_seconds = seconds

        if self.follow:
            return                          # the next beat re-ranges to the present at this width

        view = self.timeline.plotItem.vb
        right = view.viewRange()[0][1]
        view.setXRange(right - seconds, right, padding=0)

    def _set_grouping(self, by_thread: bool) -> None:
        self.by_thread = by_thread
        self._grouping.setText("Group by kind" if by_thread else "Group by thread")

    def _clear_selection(self) -> None:
        self.selection = None
        self.timeline.region.hide()
        self.timeline._cursor.hide()
        self._say_help()

    # --- the beat ------------------------------------------------------------------------------

    def refresh(self) -> None:
        arrived = self.rings.fresh()
        if len(arrived):
            self._records = (
                np.concatenate([self._records, arrived]) if len(self._records) else arrived)
            newest = int(self._records["nanos"].max())
            self._records = self._records[
                self._records["nanos"] >= newest - self.KEEP_SECONDS * 1_000_000_000]

        records = self._records
        if not len(records):
            self.status.setText(self.rings.note)
            self.legend.setPlainText(
                "Nothing recorded yet.\n\n" + self.rings.note +
                "\n\nThe app writes its ring at startup. The collector writes the other one.")
            return

        clock = self.rings.clock(records["nanos"])
        now = float(clock.max())

        if self.follow:
            self.timeline.setXRange(now - self.window_seconds,
                                    now + self.window_seconds * 0.01, padding=0)

        view = self.timeline.plotItem.vb.viewRange()[0]
        inside = (clock >= view[0]) & (clock <= view[1])
        shown, shown_clock = records[inside], clock[inside]

        if self.by_thread:
            self._draw_threads(shown, shown_clock)
        else:
            self._draw_kinds(shown, shown_clock)

        self.counters.draw(shown, shown_clock, self.rings, view)

        # The lines follow the SELECTION where there is one and the window otherwise, which is the
        # whole reason they are in this window rather than in a console: a stall is dragged across and
        # what the app was saying while it happened is underneath it.
        if self.selection is not None:
            lo, hi = self.selection
            picked = (clock >= lo) & (clock <= hi)
            self._feed_log(records[picked], f"the {hi - lo:,.1f}s you picked")
        else:
            self._feed_log(shown, f"the {view[1] - view[0]:,.0f}s on screen")

        self._say_selection(records, clock)

        self.status.setText(
            f"{self.rings.note}    {len(records):,} held    {int(inside.sum()):,} in view    "
            f"now {time.strftime('%H:%M:%S')}")

    def _feed_log(self, shown: np.ndarray, period: str) -> None:
        """The lines within whatever is on screen — the selection if there is one, the window if not."""
        said = shown[shown["kind"] == Kind.SAID]
        channels = {self.rings.name(int(r["name_id"])) or "?" for r in said}
        self.log.offer(channels)
        self.log.show_lines(
            [(float(self.rings.clock(int(r["nanos"]))),
              self.rings.name(int(r["name_id"])) or "?",
              self.rings.name(int(r["detail"])) or "")
             for r in said],
            period)

    def _draw_kinds(self, records: np.ndarray, clock: np.ndarray) -> None:
        self.timeline.lay_out([(lane.label, lane.colour, lane.bars) for lane in LANES])

        labels: list[str] = []
        told: list[str] = []

        for i, lane in enumerate(LANES):
            pick = np.isin(records["kind"], lane.kinds)
            rows, at = records[pick], clock[pick]
            self.timeline.draw_row(i, at, rows["a"] / 1e9 if lane.bars else None)

            if not len(rows):
                # Named but not counted. That a row is EMPTY is a reading in itself — "the database
                # was not the problem" is most of an answer — so it stays on screen; it simply stops
                # carrying a number, because eight rows each showing "0" look like eight things to
                # check and none of them is.
                labels.append(f"{lane.label}")
                continue

            if lane.bars:
                total = float(rows["a"].sum())
                labels.append(f"{lane.label}   {len(rows):,} · {_span(total)}")
                told.append(
                    f"{lane.label}\n  {len(rows):,} of them, {_span(total)} altogether, "
                    f"worst {_span(float(rows['a'].max()))}\n  {lane.means}")
            else:
                labels.append(f"{lane.label}   {len(rows):,}")
                told.append(f"{lane.label}\n  {len(rows):,}\n  {lane.means}")

        self.timeline.label(labels)
        # The heading says the period, because every number under it is only about that period and
        # the commonest way to misread this window is to take them for the whole run.
        view = self.timeline.plotItem.vb.viewRange()[0]
        head = f"WHAT HAPPENED IN THE {view[1] - view[0]:,.0f} SECONDS ON SCREEN"
        self.legend.setPlainText(
            f"{head}\n\n" + "\n\n".join(told) if told
            else f"{head}\n\nNothing at all. Either the app is idle, or the diagnostics that "
                 "would say more are off — see .vscode/launch.json.")

    def _draw_threads(self, records: np.ndarray, clock: np.ndarray) -> None:
        """
        A row per thread — the view that answers "what is every thread doing".

        Ordered by how much of the window each thread accounts for rather than by number, so the ones
        worth looking at are at the top and a quiet thread never pushes a busy one off the screen.
        """
        wanted = np.isin(records["kind"], THREAD_BARS + THREAD_MARKS)
        rows, at = records[wanted], clock[wanted]

        if not len(rows):
            self.timeline.lay_out([("nothing on any thread", C.FAINT, False)])
            self.timeline.label(["nothing on any thread"])
            self.legend.setPlainText(
                "THREADS IN VIEW\n\nNothing in this window carries a thread.\n\n"
                "Stalls, gate holds and waits, contention and throws all do. So do stack samples, "
                "and those need the collector run with --stacks.")
            return

        weight = {
            int(t): float(rows["a"][rows["thread"] == t].sum())
                    + int((rows["thread"] == t).sum()) * 1e6
            for t in np.unique(rows["thread"])
        }
        order = sorted(weight, key=lambda t: -weight[t])[:14]

        self.timeline.lay_out([(f"thread {t}", C.WORK, False) for t in order])

        labels: list[str] = []
        told: list[str] = []

        for i, tid in enumerate(order):
            mine = rows["thread"] == tid
            these, when = rows[mine], at[mine]

            timed = np.isin(these["kind"], THREAD_BARS)
            if timed.any():
                self.timeline.draw_row(i, when[timed], these["a"][timed] / 1e9)
            else:
                self.timeline.draw_row(i, when, None)

            busy = float(these["a"].sum())
            what: dict[str, int] = {}
            for r in these:
                key = Kind(int(r["kind"])).name.lower().replace("_", " ")
                what[key] = what.get(key, 0) + 1

            labels.append(f"thread {tid}   {len(these):,}" + (f" · {_span(busy)}" if busy else ""))
            told.append(
                f"thread {tid}\n  "
                + ", ".join(f"{n} {k}" for k, n in sorted(what.items(), key=lambda kv: -kv[1]))
                + (f"\n  {_span(busy)} accounted for" if busy else ""))

        self.timeline.label(labels)
        self.legend.setPlainText("THREADS IN VIEW\n\n" + "\n\n".join(told))

    # --- selection and blame -------------------------------------------------------------------

    def _chose(self, low: float, high: float) -> None:
        self.selection = (low, high)

    def _say_selection(self, records: np.ndarray, clock: np.ndarray) -> None:
        if self.selection is None:
            return

        low, high = self.selection
        rows = records[(clock >= low) & (clock <= high)]
        length = max(high - low, 1e-9)

        out = [f"SELECTED  {_at(low)} → {_at(high)}   ({length:,.1f}s)", ""]

        stalls = rows[rows["kind"] == Kind.FRAME_OVERRUN]
        if len(stalls):
            stalled = float(stalls["a"].sum())
            burned = float(stalls["b"].sum())
            out += [
                f"the ui was unavailable {_span(stalled)} of {length:,.1f}s"
                f"   ({stalled / 1e9 / length * 100:.0f}%)",
                f"  {len(stalls)} stalls, worst {_span(float(stalls['a'].max()))}",
                f"  running for {_span(burned)} of that   ({burned * 100 / max(stalled, 1):.0f}%)",
                "",
                ("  -> mostly BUSY: make the work cheaper, or move it off this thread"
                 if burned > stalled * 0.5 else
                 "  -> mostly PARKED: it was not running at all, so faster work will not help"),
                "",
            ]
        else:
            out += ["the ui kept up: nothing over the bar in this period", ""]

        for kind, title in ((Kind.GATE_HOLD, "held the database"),
                            (Kind.GATE_WAIT, "kept out of the database")):
            these = rows[rows["kind"] == kind]
            if not len(these):
                continue
            by: dict[str, list[float]] = {}
            for r in these:
                by.setdefault(self.rings.name(r["name_id"], r["flags"]) or "?", []).append(
                    float(r["a"]))
            out.append(f"{title}  ({len(these)})")
            for who, spans in sorted(by.items(), key=lambda kv: -sum(kv[1]))[:8]:
                out.append(f"  {_span(sum(spans)):>9}   {who}  x{len(spans)}")
            out.append("")

        suspends = int((rows["kind"] == Kind.GC_SUSPEND).sum())
        if suspends:
            out += [f"world stopped {suspends:,} times"
                    + ("   (the profiler does this: see --stacks)" if suspends > 200 else ""), ""]

        throws = rows[rows["kind"] == Kind.THROW]
        if len(throws):
            by_type: dict[str, int] = {}
            for t in throws:
                key = self.rings.name(t["name_id"], t["flags"]) or "?"
                by_type[key] = by_type.get(key, 0) + 1
            out.append(f"threw {len(throws):,}   ({len(throws) / length:,.0f}/s)")
            for name, count in sorted(by_type.items(), key=lambda kv: -kv[1])[:6]:
                out.append(f"  {count:>9,}   {name}")

        self.detail.setPlainText("\n".join(out))

    def _pick(self, at: float) -> None:
        records = self._records
        if not len(records):
            return

        clock = self.rings.clock(records["nanos"])
        pick = records["kind"] == Kind.FRAME_OVERRUN
        stalls, stall_clock = records[pick], clock[pick]
        if not len(stalls):
            return

        nearest = int(np.argmin(np.abs(stall_clock - at)))

        # A click far from any stall means something else — usually the beginning of a drag.
        if abs(stall_clock[nearest] - at) > self.window_seconds * 0.03:
            return

        chosen = stalls[nearest]
        self.timeline.mark(float(stall_clock[nearest]))
        self.selection = None
        self.timeline.region.hide()

        text = "no app ring open"
        if self.rings.app is not None:
            try:
                text = str(explain(self.rings.app, int(chosen["seq"])))
            except LookupError as e:
                text = str(e)

        began = int(chosen["nanos"]) - int(chosen["a"])
        during = records[(records["nanos"] >= began) & (records["nanos"] <= int(chosen["nanos"]))]

        also = ["", "also inside that window (overlap, not blame):"]
        for kind, label in ((Kind.SAMPLE, "stack samples"),
                            (Kind.GC_SUSPEND, "world stopped"),
                            (Kind.CONTENTION, "lock contentions"),
                            (Kind.HTTP_REQUEST, "requests started"),
                            (Kind.THROW, "throws"),
                            (Kind.INPUT, "things you did")):
            n = int((during["kind"] == kind).sum())
            if n:
                also.append(f"  {n:>7,}   {label}")

        if not int((during["kind"] == Kind.SAMPLE).sum()):
            also += ["", "  no stack samples in that window: either the collector is not running",
                     "  with --stacks, or no managed code ran at all and the thread was in",
                     "  native code or was never scheduled."]

        self.detail.setPlainText(
            f"STALL at {_at(float(stall_clock[nearest]))}\n\n" + text + "\n" + "\n".join(also))


def run(directory: str | None = None, window_seconds: float = 120) -> int:
    pg.setConfigOptions(antialias=False, foreground=C.SAID, background=C.GROUND)
    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication(sys.argv)
    viewer = Viewer(directory, window_seconds)
    viewer.show()
    return app.exec()
