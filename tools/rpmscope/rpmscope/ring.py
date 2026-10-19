"""
The reader.

Twenty lines of struct layout that have to agree, byte for byte, with
``RPM.Core/Diagnostics/ScopeRecord.cs``. Nothing in either language checks the agreement, so
``tests/test_ring.py`` reads a fixture the .NET side wrote and asserts the numbers come back.

Mapped read-only. This process cannot slow RPM down and cannot corrupt what it is reading: there is
no handshake to get wrong, no acknowledgement to withhold, and a record torn under us is skipped
rather than repaired.
"""

from __future__ import annotations

import mmap
import os
import struct
import sys
from enum import IntEnum

import numpy as np

HEADER_BYTES = 128

#: "RPMSCOPE" and "RPMSCPNM", each read as a little-endian eight-byte word.
MAGIC = 0x45504F43534D5052
NAMES_MAGIC = 0x4D4E5043534D5052

OFF_MAGIC = 0
OFF_VERSION = 8
OFF_RECORD_SIZE = 12
OFF_CAPACITY = 16
OFF_CURSOR = 24
OFF_PID = 32
OFF_MONO_START = 40
OFF_UNIX_START = 48

RECORD = np.dtype(
    [
        ("seq", "<i8"),
        ("nanos", "<i8"),
        ("job", "<i8"),
        ("parent", "<i8"),
        ("a", "<i8"),
        ("b", "<i8"),
        ("name_id", "<i4"),
        ("detail", "<i4"),
        ("kind", "<u2"),
        ("thread", "<u2"),
        ("flags", "<i4"),
    ]
)
assert RECORD.itemsize == 64, "the record is a cache line, and both languages say so"


class Kind(IntEnum):
    """Mirrors ScopeKind. The numbers are permanent: they may be added to and never renumbered."""

    NONE = 0

    JOB_ENQUEUE = 1
    JOB_ADMIT = 2
    JOB_START = 3
    JOB_BLOCK = 4
    JOB_RESUME = 5
    JOB_END = 6
    JOB_FAULT = 7
    JOB_CANCEL = 8

    GATE_WAIT = 10
    GATE_HOLD = 11

    FRAME_BEGIN = 20
    FRAME_END = 21
    FRAME_OVERRUN = 22
    LAYOUT = 23
    RENDER = 24

    INPUT = 30
    DISPATCH_DEPTH = 31

    THROW = 40

    CLOCK_SYNC = 50

    SAMPLE = 60
    GC_SUSPEND = 61
    GC_RESTART = 62
    CONTENTION = 63
    WAIT_HANDLE = 64
    POOL_QUEUE = 65
    COUNTER = 66
    THREAD_CPU = 67
    HTTP_REQUEST = 68
    HTTP_CONNECTION = 69
    SAID = 70
    CLOSING = 71


def default_dir() -> str:
    """
    Where RPM puts its rings, unless it was told otherwise. The same folder as `Scope.Dir` in
    RPM.Core/Diagnostics/Scope.cs: ~/Library/Caches/rpm on macOS, %LOCALAPPDATA%\\RPM\\scope on
    Windows, $XDG_CACHE_HOME/rpm or ~/.cache/rpm elsewhere.
    """
    asked = os.environ.get("RPM_SCOPE_DIR")
    if asked:
        return asked
    if sys.platform == "win32":
        return os.path.join(os.environ.get("LOCALAPPDATA") or os.path.expanduser("~"), "RPM", "scope")
    if sys.platform == "darwin":
        return os.path.expanduser("~/Library/Caches/rpm")
    return os.path.join(os.environ.get("XDG_CACHE_HOME") or os.path.expanduser("~/.cache"), "rpm")


def newest_ring(directory: str | None = None) -> str | None:
    """
    The most recently written app ring in a directory.

    Rings are named after the process that wrote them — `scope.<pid>.ring` — because two writers on
    one file corrupt each other silently, and coordinating them is the kind of arrangement that works
    until it does not. The cost is that a reader has to choose, and the newest is the right choice:
    it is the run somebody is asking about.
    """
    directory = directory or default_dir()
    if not os.path.isdir(directory):
        return None

    rings = [
        os.path.join(directory, f)
        for f in os.listdir(directory)
        if f.startswith("scope.") and f.endswith(".ring")
    ]
    return max(rings, key=os.path.getmtime) if rings else None


def collector_for(app_ring: str) -> str:
    """The collector ring that belongs beside an app ring, by the pid in its name."""
    pid = os.path.basename(app_ring).removeprefix("scope.").removesuffix(".ring")
    return os.path.join(os.path.dirname(app_ring), f"collector.{pid}.ring")


class Ring:
    """One ring, and the names beside it."""

    def __init__(self, directory: str | None = None, ring_name: str | None = None) -> None:
        self.dir = directory or default_dir()

        if ring_name is None:
            found = newest_ring(self.dir)
            if found is None:
                raise FileNotFoundError(f"no scope.*.ring in {self.dir}")
            self.path = found
        elif os.path.isabs(ring_name):
            self.path = ring_name
        else:
            self.path = os.path.join(self.dir, ring_name)

        self._file = open(self.path, "rb")
        self._map = mmap.mmap(self._file.fileno(), 0, access=mmap.ACCESS_READ)

        (magic,) = struct.unpack_from("<q", self._map, OFF_MAGIC)
        if magic != MAGIC:
            raise ValueError(f"{self.path} is not a scope ring")

        (capacity,) = struct.unpack_from("<q", self._map, OFF_CAPACITY)
        self.capacity = int(capacity)
        (self.pid,) = struct.unpack_from("<i", self._map, OFF_PID)
        (self.mono_start,) = struct.unpack_from("<q", self._map, OFF_MONO_START)
        (self.unix_start,) = struct.unpack_from("<q", self._map, OFF_UNIX_START)

        self._slots = np.frombuffer(
            self._map, dtype=RECORD, count=self.capacity, offset=HEADER_BYTES
        )

        # The table beside THIS ring, never a shared one: two writers appending to one file under
        # two locks hand out the same id for different words, and a reader cannot tell.
        self.names_path = self.path.removesuffix(".ring") + ".names"
        self.names = read_names(self.names_path)

        # The heartbeats read so far (see unix_seconds), and the slot to read on from.
        self._sync_mono = np.empty(0, dtype=np.int64)
        self._sync_unix = np.empty(0, dtype=np.int64)
        self._sync_from = 0

    @property
    def written(self) -> int:
        """How many records the writer says it has written. Read afresh every time: it moves."""
        (value,) = struct.unpack_from("<q", self._map, OFF_CURSOR)
        return int(value)

    def name(self, name_id: int) -> str | None:
        """The text for an id. Zero is no name, and so is an id this table has not caught up with."""
        name_id = int(name_id)
        if 1 <= name_id <= len(self.names):
            return self.names[name_id - 1]
        return None

    def records(self, from_seq: int = 0) -> np.ndarray:
        """
        Everything written since ``from_seq``, oldest first.

        A record whose sequence does not match its slot is half-written or already overwritten, and
        is dropped. That is the whole of the synchronisation, and it is why there is none.
        """
        written = self.written
        oldest = max(0, written - self.capacity)
        start = max(from_seq, oldest)
        if start >= written:
            return np.empty(0, dtype=RECORD)

        slots = np.arange(start, written)
        rows = self._slots[slots & (self.capacity - 1)]
        return rows[rows["seq"] == slots + 1]

    def unix_seconds(self, nanos) -> np.ndarray:
        """
        Monotonic nanoseconds onto unix seconds, correct across a sleep.

        The ring is stamped with CLOCK_UPTIME_RAW, which stops while the machine sleeps, so the one
        pair of stamps in the header puts every record after a sleep early by the length of the sleep.
        The app writes both clocks into a CLOCK_SYNC record once a second, and each record is read
        against the last of those at or before it. A record older than every surviving heartbeat is
        read against the first one; a ring with no heartbeat yet is read against the header.
        """
        nanos = np.asarray(nanos, dtype=np.int64)
        self._read_syncs()

        if len(self._sync_mono) == 0:
            return (self.unix_start + (nanos - self.mono_start)) / 1e9

        at = np.clip(np.searchsorted(self._sync_mono, nanos, side="right") - 1, 0, len(self._sync_mono) - 1)
        return (self._sync_unix[at] + (nanos - self._sync_mono[at])) / 1e9

    def _read_syncs(self) -> None:
        """Adds the heartbeats written since the last call. They arrive in stamp order, one writer."""
        fresh = self.records(self._sync_from)
        if len(fresh) == 0:
            return

        self._sync_from = int(fresh["seq"][-1])
        syncs = fresh[fresh["kind"] == Kind.CLOCK_SYNC]
        if len(syncs):
            self._sync_mono = np.concatenate([self._sync_mono, syncs["nanos"].astype(np.int64)])
            self._sync_unix = np.concatenate([self._sync_unix, syncs["a"].astype(np.int64)])

    def refresh_names(self) -> None:
        """Re-reads the name table, for when an id turns up that is newer than what we read."""
        self.names = read_names(self.names_path)

    def close(self) -> None:
        # The slot array exports a pointer INTO the map, and mmap refuses to close under one. Drop it
        # first. Everything handed out by records() is a copy — fancy indexing makes one — so no
        # caller is left holding a view onto a page that is about to go away.
        self._slots = np.empty(0, dtype=RECORD)
        self._map.close()
        self._file.close()

    def __enter__(self) -> "Ring":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


def read_names(path: str) -> list[str]:
    """
    The name table, in order.

    The count at the head is raised only after an entry is completely written, so reading exactly
    that many entries can never read into one that is half there.
    """
    if not os.path.exists(path):
        return []

    with open(path, "rb") as f:
        head = f.read(12)
        if len(head) < 12:
            return []

        magic, count = struct.unpack("<qi", head)
        if magic != NAMES_MAGIC:
            return []

        out: list[str] = []
        for _ in range(count):
            length_bytes = f.read(2)
            if len(length_bytes) < 2:
                break
            (length,) = struct.unpack("<H", length_bytes)
            text = f.read(length)
            if len(text) < length:
                break
            out.append(text.decode("utf-8", errors="replace"))
        return out
