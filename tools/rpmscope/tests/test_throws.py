"""
The three readings of a set of throws, and the one that took a night to get right.

`by_type` is what the app could always say on its own, and it is the half that was never in doubt.
`by_site` is the line. `asked_by` is who ASKED — and that is the reading these tests exist for: work
handed to the pool by a library throws with not one frame of ours on its stack, so the caller cannot
be found by walking it. It is carried in the record's parent instead, and if that mapping is wrong the
tool answers confidently and wrongly, which is worse than answering nothing.
"""

import numpy as np

from rpmscope.chain import summarise_throws
from rpmscope.ring import RECORD, Kind


def records(*rows):
    out = np.zeros(len(rows), dtype=RECORD)
    for i, (kind, name_id, detail, job, parent) in enumerate(rows):
        out[i]["kind"] = kind
        out[i]["name_id"] = name_id
        out[i]["detail"] = detail
        out[i]["job"] = job
        out[i]["parent"] = parent
    return out


#: 1 the exception type, 2 the short site, 3 the whole chain, 4/5 a job's name and file.
WORDS = {
    1: "TaskCanceledException",
    2: "BitmapImage.TryOpenSourceAsync",
    3: "BitmapImage.TryOpenSourceAsync < NativeDispatcher.DispatchItems < Program.Main",
    4: "FetchAsync",
    5: "ArtistWallView.xaml.cs",
    6: "IOException",
}


def name(i):
    return WORDS.get(int(i))


def test_types_are_counted():
    seen = summarise_throws(
        records((Kind.THROW, 1, 0, 0, 0), (Kind.THROW, 1, 0, 0, 0), (Kind.THROW, 6, 0, 0, 0)), name)

    assert seen.by_type == {"TaskCanceledException": 2, "IOException": 1}


def test_a_chain_and_a_short_site_are_one_row():
    # The first throw from a line carries its whole chain and the rest carry the short name. Counted
    # apart, one line would appear twice and neither row would be the truth.
    seen = summarise_throws(
        records((Kind.THROW, 1, 3, 0, 0), (Kind.THROW, 1, 2, 0, 0), (Kind.THROW, 1, 2, 0, 0)), name)

    assert seen.by_site == {"BitmapImage.TryOpenSourceAsync": 3}
    assert seen.chains["BitmapImage.TryOpenSourceAsync"].endswith("Program.Main")


def test_who_asked_comes_from_the_parent_job_and_not_from_the_stack():
    seen = summarise_throws(
        records(
            (Kind.JOB_ENQUEUE, 4, 5, 99, 0),
            (Kind.THROW, 1, 2, 0, 99),
            (Kind.THROW, 1, 2, 0, 99)),
        name)

    assert seen.asked_by == {"FetchAsync  (ArtistWallView.xaml.cs)": 2}


def test_a_throw_under_no_job_is_counted_but_blamed_on_nobody():
    seen = summarise_throws(records((Kind.THROW, 1, 2, 0, 0)), name)

    assert seen.by_type == {"TaskCanceledException": 1}
    assert seen.asked_by == {}


def test_a_parent_naming_a_job_that_is_not_in_the_ring_is_not_invented():
    # The ring wraps. A throw can outlive the record that named the job it belongs to, and the honest
    # answer then is silence rather than a guess.
    seen = summarise_throws(records((Kind.THROW, 1, 2, 0, 12345)), name)

    assert seen.asked_by == {}
