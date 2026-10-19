"""
The ring as a trace file Perfetto will read.

The one thing here that is easy to get wrong and impossible to see afterwards is WHICH END a slice is
stamped at. A record is written when the thing finished and carries how long it took, so a slice has
to start before its own stamp; drawn forwards, every cause sits after its effect and the trace reads
as though the app reacted before it was asked. It still looks like a perfectly good trace.
"""

import json

import numpy as np
import pytest

from rpmscope.perfetto import export
from rpmscope.ring import RECORD, Kind


class FakeRing:
    """Enough of a ring to export: the records, the two clocks, and the names."""

    def __init__(self, records, words):
        self._records = records
        self._words = words
        self.unix_start = 1_700_000_000_000_000_000   # nanoseconds since the epoch
        self.mono_start = 1_000_000_000
        self.path = "/tmp/scope.42.ring"

    def records(self):
        return self._records

    def name(self, i):
        return self._words.get(int(i))


def ring(*rows):
    out = np.zeros(len(rows), dtype=RECORD)
    for i, (kind, nanos, a, thread, name_id, detail) in enumerate(rows):
        out[i]["kind"], out[i]["nanos"], out[i]["a"] = kind, nanos, a
        out[i]["thread"], out[i]["name_id"], out[i]["detail"] = thread, name_id, detail
    return FakeRing(out, {1: "GetAlbumCloudRollup", 2: "read", 3: "TaskCanceledException"})


def written(tmp_path, r):
    out = str(tmp_path / "trace.json")
    export(r, out)
    return json.load(open(out))["traceEvents"]


def test_a_slice_starts_before_the_stamp_it_was_written_at(tmp_path):
    # Stamped at 2s having taken 500ms, so it ran from 1.5s to 2.0s.
    events = written(tmp_path, ring((Kind.GATE_HOLD, 2_000_000_000, 500_000_000, 7, 1, 2)))
    slice_ = next(e for e in events if e["ph"] == "X")

    assert slice_["dur"] == pytest.approx(500_000)          # microseconds
    started = slice_["ts"]
    assert started + slice_["dur"] == pytest.approx(started + 500_000)
    # and it starts 500ms before the record's own moment, not at it
    assert slice_["ts"] == pytest.approx(1_700_000_000_000_000 + 1_000_000 - 500_000, abs=1)


def test_the_drawing_thread_is_named_rather_than_numbered(tmp_path):
    events = written(tmp_path, ring((Kind.FRAME_OVERRUN, 2_000_000_000, 1_000_000, 1, 1, 0)))
    names = {e["args"]["name"] for e in events if e.get("name") == "thread_name"}

    assert "drawing thread" in names, "thread 1 is the one anybody opens this to look at"


def test_a_thread_is_named_once_however_many_records_it_has(tmp_path):
    events = written(tmp_path, ring(
        (Kind.THROW, 2_000_000_000, 0, 9, 3, 0),
        (Kind.THROW, 2_100_000_000, 0, 9, 3, 0),
        (Kind.THROW, 2_200_000_000, 0, 9, 3, 0)))

    assert sum(1 for e in events if e.get("name") == "thread_name") == 1


def test_something_instantaneous_is_a_mark_and_not_a_slice(tmp_path):
    events = written(tmp_path, ring((Kind.THROW, 2_000_000_000, 0, 9, 3, 0)))

    assert [e["ph"] for e in events if e["ph"] in "Xi"] == ["i"]


def test_an_empty_ring_writes_nothing_rather_than_an_empty_trace(tmp_path):
    empty = FakeRing(np.empty(0, dtype=RECORD), {})

    assert export(empty, str(tmp_path / "trace.json")) == 0


def test_a_closing_record_is_a_mark_named_closing(tmp_path):
    events = written(tmp_path, ring((Kind.CLOSING, 2_000_000_000, 0, 1, 0, 0)))

    marks = [e for e in events if e.get("ph") == "i"]
    assert [m["name"] for m in marks] == ["closing"]
