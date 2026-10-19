using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// Reading a stored session back into a world that has moved on.
///
/// <para>Everything the app writes down about its own screen is a claim about how things were, and
/// between two runs any of it can stop being true: a file is deleted, a library is pointed at another
/// folder, a record is offloaded, a column is renamed, a shelf has shrunk. The rule the whole restore
/// is built on is here: a value that cannot be honoured is dropped, and the default stands.</para>
/// </summary>
public class SessionRestoreTests
{
    // ── the window's own frame (Windows; macOS has AppKit do this) ──

    /// <summary>One 2560×1440 screen with the desktop origin at 0,0.</summary>
    private static readonly (int Left, int Top, int Right, int Bottom) OneScreen = (0, 0, 2560, 1440);

    /// <summary>Two screens side by side: the second one to the right.</summary>
    private static readonly (int Left, int Top, int Right, int Bottom) TwoScreens = (0, 0, 5120, 1440);

    [Fact]
    public void A_window_that_still_fits_opens_where_it_was()
        => Assert.Equal((100, 80, 1600, 1000),
            SessionRestore.WindowFrame(100, 80, 1600, 1000, OneScreen));

    [Fact]
    public void A_window_on_the_second_screen_opens_there_while_it_is_plugged_in()
        => Assert.Equal((3000, 100, 1600, 1000),
            SessionRestore.WindowFrame(3000, 100, 1600, 1000, TwoScreens));

    /// <summary>If the laptop is off the desk, putting the window back where it was would open the app on
    /// no screen at all. It would be running and in the task switcher, but invisible.</summary>
    [Fact]
    public void A_window_on_a_screen_that_is_gone_opens_where_the_system_puts_it()
        => Assert.Null(SessionRestore.WindowFrame(3000, 100, 1600, 1000, OneScreen));

    /// <summary>Hanging off the right edge with only a sliver showing is the same problem.</summary>
    [Fact]
    public void A_window_with_nothing_left_to_grab_is_refused()
        => Assert.Null(SessionRestore.WindowFrame(2500, 100, 1600, 1000, OneScreen));

    /// <summary>Mostly off the edge is fine as long as there is enough to see and drag back.</summary>
    [Fact]
    public void A_window_hanging_over_the_edge_with_a_handhold_is_kept()
        => Assert.Equal((2000, 100, 1600, 1000),
            SessionRestore.WindowFrame(2000, 100, 1600, 1000, OneScreen));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1600, 0)]
    [InlineData(120, 900)]
    [InlineData(60000, 1000)]
    public void A_size_no_window_was_ever_used_at_is_refused(int width, int height)
        => Assert.Null(SessionRestore.WindowFrame(100, 80, width, height, OneScreen));

    // ── enums, stored by name ──

    [Fact]
    public void A_stored_name_reads_back_as_its_value()
        => Assert.Equal(AppView.RPaMp, SessionRestore.Enum<AppView>("RPaMp"));

    /// <summary>A view renamed or removed since the file was written. The app opens on the library,
    /// which is where closing anything goes anyway.</summary>
    [Fact]
    public void A_name_that_no_longer_resolves_is_no_value_at_all()
        => Assert.Null(SessionRestore.Enum<AppView>("Jukebox"));

    [Fact]
    public void Nothing_stored_is_no_value_at_all()
        => Assert.Null(SessionRestore.Enum<AppView>(null));

    /// <summary>Enums are stored by NAME on purpose. A number stored by another version of the app must
    /// not be read as whichever value happens to sit at that ordinal now.</summary>
    [Fact]
    public void A_number_is_not_a_name()
        => Assert.Null(SessionRestore.Enum<AppView>("1"));

    // ── the search box ──

    [Fact]
    public void A_search_comes_back_trimmed()
        => Assert.Equal("bowie", SessionRestore.Search("  bowie "));

    [Fact]
    public void An_empty_search_is_no_search()
        => Assert.Equal("", SessionRestore.Search("   "));

    /// <summary>A whole document pasted into the box is not a search to reopen on.</summary>
    [Fact]
    public void A_search_longer_than_a_search_is_dropped()
        => Assert.Equal("", SessionRestore.Search(new string('x', SessionRestore.MaxSearchLength + 1)));

    // ── the left pane's split ──

    [Fact]
    public void A_sane_split_is_kept()
        => Assert.Equal((1.0, 1.4), SessionRestore.Split(1.0, 1.4));

    /// <summary>Half a split is not a split. A stored zero, from an old or half-written file, would open
    /// the app with the library squeezed into the corner.</summary>
    [Theory]
    [InlineData(0, 1.4)]
    [InlineData(1.0, 0)]
    [InlineData(double.NaN, 1.4)]
    [InlineData(1.0, double.PositiveInfinity)]
    [InlineData(90, 1.4)]
    public void A_split_that_is_not_a_split_is_refused_whole(double left, double right)
        => Assert.Null(SessionRestore.Split(left, right));

    // ── scroll offsets ──

    [Fact]
    public void A_scroll_within_reach_is_kept()
        => Assert.Equal(400, SessionRestore.Scroll(400, 900));

    /// <summary>The shelf has shrunk since. Records were deleted, or a search is still in force. The
    /// bottom is as far as it goes.</summary>
    [Fact]
    public void A_scroll_past_the_end_lands_at_the_end()
        => Assert.Equal(120, SessionRestore.Scroll(400, 120));

    [Theory]
    [InlineData(-5, 900)]
    [InlineData(double.NaN, 900)]
    [InlineData(400, 0)]
    public void A_scroll_with_nothing_to_scroll_is_the_top(double stored, double scrollable)
        => Assert.Equal(0, SessionRestore.Scroll(stored, scrollable));

    // ── table columns ──

    [Fact]
    public void A_dragged_column_width_comes_back()
        => Assert.Equal(180, SessionRestore.ColumnWidth(180));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(4000)]
    [InlineData(double.NaN)]
    public void A_width_no_column_could_have_been_dragged_to_is_dropped(double stored)
        => Assert.Null(SessionRestore.ColumnWidth(stored));

    [Fact]
    public void A_sort_on_a_column_that_is_still_there_is_kept()
        => Assert.Equal("album", SessionRestore.SortColumn("album", key => key is "album" or "artist"));

    /// <summary>The column was renamed, dropped, or never belonged to the mirror. The table opens in
    /// its own order.</summary>
    [Fact]
    public void A_sort_on_a_column_that_is_gone_is_dropped()
        => Assert.Null(SessionRestore.SortColumn("bpm", key => key is "album" or "artist"));

    [Fact]
    public void No_stored_sort_is_no_sort()
        => Assert.Null(SessionRestore.SortColumn(null, _ => true));

    // The app opens on home with nothing behind it, so there is no stored open record to restore and
    // nothing to check here. See MainPage.RestoreHome.

    // ── the queue ──

    /// <summary>The ordinary case: everything the queue held is still here, in the order it was
    /// in.</summary>
    [Fact]
    public void A_queue_whose_tracks_are_all_here_comes_back_whole()
    {
        var queue = SessionRestore.Queue<string>(["a.flac", "b.flac"], p => p);
        Assert.Equal(["a.flac", "b.flac"], queue);
    }

    /// <summary>A track deleted, offloaded to a machine this one cannot reach, or belonging to a library
    /// this install is no longer pointed at: it is simply not in the queue that comes back.</summary>
    [Fact]
    public void Tracks_that_no_longer_resolve_are_left_out()
    {
        var queue = SessionRestore.Queue<string>(["a.flac", "gone.flac", "b.flac"],
            p => p == "gone.flac" ? null : p);
        Assert.Equal(["a.flac", "b.flac"], queue);
    }

    [Fact]
    public void A_queue_nobody_stored_is_empty()
        => Assert.Empty(SessionRestore.Queue<string>(null, p => p));

    /// <summary>A DJ set over a large collection is thousands of records, and a session file is not where a
    /// playlist lives. The head of it, which is where the needle is, comes back.</summary>
    [Fact]
    public void A_queue_longer_than_the_cap_keeps_its_head()
    {
        var stored = Enumerable.Range(0, SessionRestore.MaxQueue + 50).Select(i => $"{i}.flac").ToList();
        var queue = SessionRestore.Queue<string>(stored, p => p);
        Assert.Equal(SessionRestore.MaxQueue, queue.Count);
        Assert.Equal("0.flac", queue[0]);
    }

    // ── the needle ──

    [Fact]
    public void The_needle_comes_back_where_it_was()
        => Assert.Equal((1, 42d), SessionRestore.Cursor(1, 42, "b.flac", ["a.flac", "b.flac", "c.flac"]));

    /// <summary>Tracks ahead of it did not come back, so the position it was stored at now holds
    /// somebody else. The track itself is what the needle belongs to.</summary>
    [Fact]
    public void The_needle_follows_its_track_when_the_queue_has_shrunk()
        => Assert.Equal((0, 42d), SessionRestore.Cursor(1, 42, "b.flac", ["b.flac", "c.flac"]));

    /// <summary>The record that was playing is the one that has gone. The queue is still worth keeping, and
    /// it opens at the top with nothing resumed. Playing something the listener did not leave playing is
    /// worse than not resuming at all.</summary>
    [Fact]
    public void A_needle_whose_track_is_gone_starts_the_queue_over()
        => Assert.Equal((0, 0d), SessionRestore.Cursor(1, 42, "b.flac", ["a.flac", "c.flac"]));

    [Fact]
    public void An_empty_queue_has_no_needle()
        => Assert.Equal((-1, 0d), SessionRestore.Cursor(1, 42, "b.flac", []));

    /// <summary>A second into a record is not a place worth coming back to, and neither is a stored
    /// position that is not a number.</summary>
    [Theory]
    [InlineData(0.4)]
    [InlineData(double.NaN)]
    public void A_position_at_the_very_start_is_the_start(double seconds)
        => Assert.Equal((0, 0d), SessionRestore.Cursor(0, seconds, "a.flac", ["a.flac"]));
}
