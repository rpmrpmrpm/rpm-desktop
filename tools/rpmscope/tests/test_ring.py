import os
import tempfile

import pytest

from rpmscope.ring import RECORD, Kind, Ring

FIXTURE = os.path.join(tempfile.gettempdir(), "rpmscope-fixture")


@pytest.fixture(scope="session")
def fixture_dir():
    """The .NET side writes it. If it has not been written, say so rather than skipping quietly."""
    if not os.path.exists(os.path.join(FIXTURE, "scope.ring")):
        pytest.fail(
            "no fixture: run `dotnet test RPM.Core.Tests/RPM.Core.Tests.csproj "
            "--filter FullyQualifiedName~ScopeFormatTests` first"
        )
    return FIXTURE


def test_reads_what_dotnet_wrote(fixture_dir):
    ring = Ring(fixture_dir)
    records = ring.records()

    assert len(records) == 2

    hold = records[0]
    assert hold["kind"] == Kind.GATE_HOLD
    assert hold["nanos"] == 1_000_000_000
    assert hold["thread"] == 7
    assert hold["job"] == 111
    assert hold["parent"] == 222
    assert hold["a"] == 19_371_000_000
    assert hold["b"] == 4_000_000
    assert ring.name(hold["name_id"]) == "GetAlbumCloudRollup"
    assert ring.name(hold["detail"]) == "read"
    assert hold["flags"] == 0x0201

    frame = records[1]
    assert frame["kind"] == Kind.FRAME_OVERRUN
    assert frame["a"] == 1_516_000_000


def test_a_record_is_sixty_four_bytes():
    assert RECORD.itemsize == 64


def test_names_resolve_and_zero_is_no_name(fixture_dir):
    ring = Ring(fixture_dir)
    assert ring.name(0) is None
    assert "read" in ring.names


def test_a_ring_closes_cleanly(fixture_dir):
    # The numpy view over the mapped slots exports a pointer into it, and mmap refuses to close while
    # one is out. Every command in the CLI opens a ring in a `with`, so this is the whole tool.
    with Ring(fixture_dir) as ring:
        assert len(ring.records()) == 2

    ring = Ring(fixture_dir)
    records = ring.records()
    ring.close()
    assert len(records) == 2, "records handed out before the close must survive it"
