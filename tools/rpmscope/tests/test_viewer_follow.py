"""
Taking hold of the view stops it following, and letting go of it does not.

The two are the same gesture fighting each other: "Live" re-asserts the range ten times a second, so a
drag or a wheel was undone before the hand came off it — the window simply snapped back and looked
broken. What settles it is that pyqtgraph says whether a range change was the USER's or ours, so
nothing has to guess and nothing has to time anything.
"""

import os

import pytest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

pytest.importorskip("PySide6")
pytest.importorskip("pyqtgraph")

from PySide6 import QtWidgets                                    # noqa: E402

from rpmscope.viewer import Viewer                               # noqa: E402


@pytest.fixture(scope="module")
def app():
    return QtWidgets.QApplication.instance() or QtWidgets.QApplication([])


@pytest.fixture
def viewer(app):
    window = Viewer(None, 120)
    window.resize(1200, 800)
    yield window
    window.beat.stop()
    window.close()


def took_hold(viewer, seconds):
    """What a drag does: pyqtgraph moves the range and then says the move was the user's."""
    view = viewer.timeline.plotItem.vb
    lo, hi = view.viewRange()[0]
    view.setXRange(lo - seconds, hi - seconds, padding=0)
    view.sigRangeChangedManually.emit((True, False))


def test_it_follows_until_somebody_takes_hold(viewer, app):
    assert viewer.follow
    assert viewer._live.isChecked()

    took_hold(viewer, 40)
    app.processEvents()

    assert not viewer.follow, "the window kept following after being dragged, so it snaps back"
    assert not viewer._live.isChecked(), "Live still looks on while the window is not following"


def test_a_view_taken_hold_of_stays_where_it_was_put(viewer, app):
    took_hold(viewer, 40)
    app.processEvents()
    put = viewer.timeline.plotItem.vb.viewRange()[0]

    for _ in range(5):
        viewer.refresh()
        app.processEvents()

    assert viewer.timeline.plotItem.vb.viewRange()[0] == pytest.approx(put, abs=1e-6), \
        "a beat moved the view out from under the reader"


def test_live_comes_back_at_the_width_on_screen(viewer, app):
    took_hold(viewer, 40)
    app.processEvents()
    width = float(np_width(viewer))

    viewer._live.setChecked(True)
    app.processEvents()

    assert viewer.follow
    # The width somebody has chosen by zooming is theirs. Going back to the present should not also
    # take them back to whatever the range buttons last said.
    assert viewer.window_seconds == pytest.approx(width, rel=0.02)


def np_width(viewer) -> float:
    lo, hi = viewer.timeline.plotItem.vb.viewRange()[0]
    return hi - lo


@pytest.mark.parametrize("seconds", [30, 120, 600, 1800])
def test_choosing_a_width_does_not_switch_following_back_on(viewer, app, seconds):
    """The bug as reported: Live could be turned off, and the next button pressed turned it back on.

    Width and following are two settings, and wiring them together left no way to say "show me half an
    hour, ending where I am looking" — and made a toggle look as though it would not stay put.
    """
    viewer._live.setChecked(False)
    app.processEvents()

    viewer._set_window(seconds)
    app.processEvents()
    viewer.refresh()
    app.processEvents()

    assert not viewer.follow, f"the {seconds}s button switched following back on"
    assert not viewer._live.isChecked()
    assert np_width(viewer) == pytest.approx(seconds, rel=0.01)


def test_a_width_chosen_while_held_still_grows_from_the_right_edge(viewer, app):
    # That edge is where the reader has put the thing they are looking at. Widening should show more
    # of what came BEFORE it, not carry it off the screen.
    viewer._live.setChecked(False)
    app.processEvents()
    right = viewer.timeline.plotItem.vb.viewRange()[0][1]

    viewer._set_window(600)
    app.processEvents()

    assert viewer.timeline.plotItem.vb.viewRange()[0][1] == pytest.approx(right, abs=1e-6)


def test_choosing_a_width_while_live_still_follows_the_present(viewer, app):
    assert viewer.follow

    viewer._set_window(30)
    app.processEvents()
    viewer.refresh()
    app.processEvents()

    assert viewer.follow
    assert np_width(viewer) == pytest.approx(30, rel=0.05)
