using Microsoft.UI.Xaml.Input;
using RPM.Desktop.Presentation.Pegasus;
using Windows.Foundation;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which element an arrow on the TV remote moves the focus to in Pegasus, decided from where things are
/// laid out (RemoteSearch.Pick). Sizes are in points, roughly those of real TV screens.
/// </summary>
public class RemoteSearchPickTests
{
    // A wall row: covers 240 wide with 20 between them.
    private static readonly Rect Left = new(0, 0, 240, 270);
    private static readonly Rect Middle = new(260, 0, 240, 270);
    private static readonly Rect RightOne = new(520, 0, 240, 270);

    [Fact]
    public void Right_goes_to_the_next_cover_in_the_row()
        => Assert.Equal(1, RemoteSearch.Pick(Middle, [Left, RightOne], FocusNavigationDirection.Right, acrossFree: true));

    [Fact]
    public void Left_from_the_first_cover_has_nowhere_to_go()
        => Assert.Equal(-1, RemoteSearch.Pick(Left, [Middle, RightOne], FocusNavigationDirection.Left, acrossFree: true));

    [Fact]
    public void Sideways_never_goes_to_another_row()
        // Below and to the right, sharing no height with the focused cover.
        => Assert.Equal(-1, RemoteSearch.Pick(Middle, [new Rect(520, 300, 240, 270)], FocusNavigationDirection.Right, acrossFree: true));

    [Fact]
    public void Down_takes_the_cover_straight_below_over_its_neighbours()
        => Assert.Equal(1, RemoteSearch.Pick(Middle,
            [new Rect(0, 300, 240, 270), new Rect(260, 300, 240, 270), new Rect(520, 300, 240, 270)],
            FocusNavigationDirection.Down, acrossFree: true));

    /// <summary>Down from the end of the wall's next-to-last row, where the last row is short: the last
    /// record, which is the nearest across, inside the list.</summary>
    [Fact]
    public void Down_in_a_list_reaches_the_last_record_of_a_short_last_row()
        => Assert.Equal(1, RemoteSearch.Pick(RightOne,
            [new Rect(0, 300, 240, 270), new Rect(260, 300, 240, 270)],
            FocusNavigationDirection.Down, acrossFree: true));

    [Fact]
    public void Down_out_of_a_list_does_not_go_to_another_column()
        => Assert.Equal(-1, RemoteSearch.Pick(RightOne,
            [new Rect(0, 300, 240, 270), new Rect(260, 300, 240, 270)],
            FocusNavigationDirection.Down, acrossFree: false));

    /// <summary>A record page: the play key in the left column, the track list in the right one. Down from
    /// the play key must not jump far down the track list. Focus bounces back instead.</summary>
    [Fact]
    public void Down_from_a_page_s_play_key_does_not_cross_to_the_track_list()
        => Assert.Equal(-1, RemoteSearch.Pick(new Rect(180, 570, 230, 35),
            [new Rect(640, 620, 470, 30), new Rect(640, 650, 470, 30)],
            FocusNavigationDirection.Down, acrossFree: false));

    /// <summary>Home's quick selection, second column: the banner's arrow at the far left is nearer,
    /// but only the mini player's keys stand above in this column.</summary>
    [Fact]
    public void Up_takes_what_is_in_the_column_over_something_nearer_beside_it()
        => Assert.Equal(1, RemoteSearch.Pick(new Rect(330, 340, 310, 40),
            [new Rect(20, 145, 30, 25), new Rect(607, 100, 65, 15)],
            FocusNavigationDirection.Up, acrossFree: false));

    [Fact]
    public void Something_behind_the_focused_element_is_not_ahead_of_it()
        => Assert.Equal(-1, RemoteSearch.Pick(Middle, [Left], FocusNavigationDirection.Right, acrossFree: true));

    [Fact]
    public void A_neighbour_touching_the_focused_element_counts()
        => Assert.Equal(0, RemoteSearch.Pick(Middle, [new Rect(500, 0, 240, 270)], FocusNavigationDirection.Right, acrossFree: true));

    /// <summary>Home's quick selection: cards in a column that overlap by a couple of points. The next one
    /// down is still the next one, not the one after it.</summary>
    [Fact]
    public void Down_takes_a_neighbour_that_overlaps_by_a_few_points()
        => Assert.Equal(0, RemoteSearch.Pick(new Rect(0, 0, 617, 84),
            [new Rect(0, 82, 617, 84), new Rect(0, 164, 617, 84)],
            FocusNavigationDirection.Down, acrossFree: false));

    /// <summary>Home's quick selection: Right from a column's last card. The card above its neighbour
    /// shares a few points of height with it too, and went first; the one beside it is the one.</summary>
    [Fact]
    public void Right_from_a_column_s_last_card_goes_to_the_card_beside_it_not_the_one_above()
        => Assert.Equal(1, RemoteSearch.Pick(new Rect(0, 252, 617, 84),
            [new Rect(630, 170, 617, 86), new Rect(630, 256, 617, 86)],
            FocusNavigationDirection.Right, acrossFree: false));

    /// <summary>Two things whose tops differ by a few points are one row: the one in the column wins
    /// although the other starts a little higher.</summary>
    [Fact]
    public void A_row_whose_tops_differ_by_a_few_points_is_still_one_row()
        => Assert.Equal(1, RemoteSearch.Pick(Middle,
            [new Rect(0, 296, 240, 270), new Rect(260, 302, 240, 270)],
            FocusNavigationDirection.Down, acrossFree: true));
}
