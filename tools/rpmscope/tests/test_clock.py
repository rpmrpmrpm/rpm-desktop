import os
import struct

import numpy as np

from rpmscope.ring import OFF_MONO_START, OFF_UNIX_START, Kind, Ring
from test_chain import build_ring

SECOND = 1_000_000_000

#: The app's monotonic clock and the wall clock when the ring was made.
MONO_START = 500 * SECOND
UNIX_START = 1_790_000_000 * SECOND

#: How long the machine slept between the second and third heartbeat. The monotonic clock the ring
#: is stamped with (CLOCK_UPTIME_RAW) does not advance while the machine sleeps; the wall clock does.
SLEPT = 1000 * SECOND


def _ring_with_a_sleep(directory: str) -> Ring:
    records = [
        {"nanos": MONO_START + 1 * SECOND, "kind": Kind.CLOCK_SYNC, "a": UNIX_START + 1 * SECOND},
        {"nanos": MONO_START + 2 * SECOND, "kind": Kind.CLOCK_SYNC, "a": UNIX_START + 2 * SECOND},
        {"nanos": MONO_START + 2 * SECOND + SECOND // 2, "kind": Kind.SAID, "name": "audio",
         "detail": "before the sleep"},
        # Asleep here: the wall clock moves on by SLEPT and the monotonic clock does not.
        {"nanos": MONO_START + 3 * SECOND, "kind": Kind.CLOCK_SYNC, "a": UNIX_START + 3 * SECOND + SLEPT},
        {"nanos": MONO_START + 3 * SECOND + SECOND // 2, "kind": Kind.SAID, "name": "audio",
         "detail": "after the sleep"},
    ]
    build_ring({"names": ["audio", "before the sleep", "after the sleep"], "records": records}, directory)

    path = os.path.join(directory, "scope.ring")
    with open(path, "r+b") as f:
        f.seek(OFF_MONO_START)
        f.write(struct.pack("<q", MONO_START))
        f.seek(OFF_UNIX_START)
        f.write(struct.pack("<q", UNIX_START))

    return Ring(directory)


def test_a_line_after_a_sleep_is_put_at_the_hour_it_was_said(tmp_path):
    ring = _ring_with_a_sleep(str(tmp_path))
    said = ring.records()
    said = said[said["kind"] == Kind.SAID]

    unix = ring.unix_seconds(said["nanos"])

    assert unix[0] == (UNIX_START + 2 * SECOND + SECOND // 2) / 1e9
    assert unix[1] == (UNIX_START + 3 * SECOND + SLEPT + SECOND // 2) / 1e9


def test_a_record_older_than_every_heartbeat_is_read_against_the_first(tmp_path):
    ring = _ring_with_a_sleep(str(tmp_path))

    unix = ring.unix_seconds(np.array([MONO_START + SECOND // 2], dtype=np.int64))

    assert unix[0] == (UNIX_START + SECOND // 2) / 1e9


def test_a_ring_with_no_heartbeat_yet_falls_back_to_the_header(tmp_path):
    build_ring({"names": [], "records": []}, str(tmp_path))
    path = os.path.join(str(tmp_path), "scope.ring")
    with open(path, "r+b") as f:
        f.seek(OFF_MONO_START)
        f.write(struct.pack("<q", MONO_START))
        f.seek(OFF_UNIX_START)
        f.write(struct.pack("<q", UNIX_START))
    ring = Ring(str(tmp_path))

    unix = ring.unix_seconds(np.array([MONO_START + 7 * SECOND], dtype=np.int64))

    assert unix[0] == (UNIX_START + 7 * SECOND) / 1e9
