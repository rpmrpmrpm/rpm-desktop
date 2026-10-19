"""
The log pane stays where the reader put it.

It is redrawn ten times a second off a live ring, and `setPlainText` puts the cursor back at the top —
so scrolling up to read something threw you back to the top before you had read it. The fix is not to
restore the scrollbar afterwards but to NOT REBUILD when nothing has changed, because restoring a
position across a rebuild is approximate and doing nothing is exact.
"""

import os

import pytest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

pytest.importorskip("PySide6")

from PySide6 import QtWidgets                                   # noqa: E402

from rpmscope.viewer import LogPane                             # noqa: E402


@pytest.fixture(scope="module")
def app():
    return QtWidgets.QApplication.instance() or QtWidgets.QApplication([])


@pytest.fixture
def pane(app):
    p = LogPane()
    p.offer({"walldiag", "net"})
    p.resize(600, 200)
    return p


def lines(n, channel="walldiag"):
    return [(1_700_000_000 + i, channel, f"line {i}") for i in range(n)]


def test_the_same_lines_twice_do_not_touch_the_document(pane, app):
    pane.show_lines(lines(50), "on screen")
    app.processEvents()
    was = pane.text.document().revision()

    for _ in range(5):
        pane.show_lines(lines(50), "on screen")
        app.processEvents()

    assert pane.text.document().revision() == was, \
        "the document was rebuilt with identical content, which is what threw the reader to the top"


def test_a_new_line_does_rebuild(pane, app):
    pane.show_lines(lines(50), "on screen")
    app.processEvents()
    was = pane.text.document().revision()

    pane.show_lines(lines(51), "on screen")
    app.processEvents()

    assert pane.text.document().revision() != was


def test_scrolling_up_survives_a_beat_that_brings_nothing(pane, app):
    pane.show_lines(lines(200), "on screen")
    app.processEvents()
    bar = pane.text.verticalScrollBar()
    bar.setValue(bar.maximum() // 3)
    parked = bar.value()

    for _ in range(5):
        pane.show_lines(lines(200), "on screen")
        app.processEvents()

    assert bar.value() == parked, "a beat moved the reader off what they were reading"


def test_lines_wrap_rather_than_running_off_the_side(pane):
    # With a horizontal scrollbar, scrolling right to read the end of a 200-character walldiag line
    # blanks every short one — which reads as a log full of empty lines.
    assert pane.text.lineWrapMode() == QtWidgets.QPlainTextEdit.WidgetWidth


def test_turning_a_channel_off_takes_its_lines_away(pane, app):
    pane.show_lines(lines(3, "walldiag") + lines(2, "net"), "on screen")
    app.processEvents()

    pane._toggle("net", False)
    pane.show_lines(lines(3, "walldiag") + lines(2, "net"), "on screen")
    app.processEvents()

    assert "net" not in pane.text.toPlainText()
