import json
import os
import struct

import numpy as np
import pytest

from rpmscope.chain import explain
from rpmscope.ring import HEADER_BYTES, MAGIC, NAMES_MAGIC, RECORD, Ring

HERE = os.path.dirname(__file__)


def build_ring(spec: dict, directory: str) -> None:
    """Writes a ring and a name table from the JSON fixture, in the format ScopeRing writes."""
    names = spec["names"]
    ids = {name: i + 1 for i, name in enumerate(names)}

    with open(os.path.join(directory, "scope.names"), "wb") as f:
        f.write(struct.pack("<qi", NAMES_MAGIC, len(names)))
        for name in names:
            encoded = name.encode("utf-8")
            f.write(struct.pack("<H", len(encoded)))
            f.write(encoded)

    capacity = 256
    rows = np.zeros(capacity, dtype=RECORD)
    for slot, record in enumerate(spec["records"]):
        rows[slot]["seq"] = slot + 1
        rows[slot]["nanos"] = record["nanos"]
        rows[slot]["kind"] = record["kind"]
        rows[slot]["job"] = record.get("job", 0)
        rows[slot]["parent"] = record.get("parent", 0)
        rows[slot]["a"] = record.get("a", 0)
        rows[slot]["b"] = record.get("b", 0)
        rows[slot]["name_id"] = ids.get(record.get("name", ""), 0)
        rows[slot]["detail"] = ids.get(record.get("detail", ""), 0)

    with open(os.path.join(directory, "scope.ring"), "wb") as f:
        header = bytearray(HEADER_BYTES)
        struct.pack_into("<q", header, 0, MAGIC)
        struct.pack_into("<i", header, 8, 1)
        struct.pack_into("<i", header, 12, 64)
        struct.pack_into("<q", header, 16, capacity)
        struct.pack_into("<q", header, 24, len(spec["records"]))
        struct.pack_into("<i", header, 32, 4242)
        f.write(header)
        f.write(rows.tobytes())


@pytest.fixture
def nineteen_second_hold(tmp_path):
    with open(os.path.join(HERE, "fixtures", "nineteen-second-hold.json")) as f:
        build_ring(json.load(f), str(tmp_path))
    return Ring(str(tmp_path))


def _the_stall(ring):
    return int([r for r in ring.records() if r["kind"] == 22][0]["seq"])


def test_the_chain_names_the_holder_and_who_asked_for_it(nineteen_second_hold):
    ring = nineteen_second_hold
    lines = explain(ring, _the_stall(ring)).lines()
    text = "\n".join(lines)

    assert "19,380ms" in lines[0]
    assert "the thread was NOT running" in lines[0]

    # The chain, and the order is the point: the holder, then who reached it, then who started that.
    assert "GetAlbumCloudRollup" in text
    assert "CloudBadgeOf" in text
    assert "RefreshBadges" in text
    assert "OnSharedShelfChanged" in text
    assert text.index("GetAlbumCloudRollup") < text.index("CloudBadgeOf")
    assert text.index("CloudBadgeOf") < text.index("RefreshBadges")
    assert text.index("RefreshBadges") < text.index("OnSharedShelfChanged")


def test_the_chain_counts_the_throws_inside_the_stall(nineteen_second_hold):
    ring = nineteen_second_hold
    text = "\n".join(explain(ring, _the_stall(ring)).lines())
    assert "2 throws" in text
    assert "IOException" in text


def test_work_that_was_not_in_the_chain_is_left_out(nineteen_second_hold):
    # ImportScan ran during the stall but nothing links it to the holder, so it is not asserted to be
    # the cause. A chain that names everything that happened is a dump again.
    ring = nineteen_second_hold
    assert "ImportScan" not in "\n".join(explain(ring, _the_stall(ring)).lines())


def test_a_stall_with_nothing_in_it_says_so(tmp_path):
    build_ring(
        {
            "names": ["ui"],
            "records": [
                {"kind": 22, "nanos": 5000000000, "name": "ui", "a": 800000000, "b": 780000000}
            ],
        },
        str(tmp_path),
    )
    text = "\n".join(explain(Ring(str(tmp_path)), 1).lines())
    assert "nothing of ours" in text
    assert "the thread WAS running" in text


def test_an_unknown_stall_is_an_error_not_a_guess(nineteen_second_hold):
    with pytest.raises(LookupError):
        explain(nineteen_second_hold, 9999)
