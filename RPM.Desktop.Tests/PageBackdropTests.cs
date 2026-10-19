using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Tests;

/// <summary>
/// The ground a detail page lends to the whole window, and the two facts that must match before it
/// is shown: what the page is dressed in, and which page the reader is looking at.
///
/// <para>Each fact below states both, so nothing here depends on the order the tests run in. The thing
/// under test is one static, so the tests reset it between runs. No brushes and no pictures: those are
/// XAML objects, and there is no application here to create them. None of the reasoning depends on
/// them.</para>
/// </summary>
public class PageBackdropTests
{
    private static readonly object AlbumPage = new();
    private static readonly object ArtistPage = new();

    private static PageBackdrop.Ground Sleeve(double veil) =>
        new(Colour: null, Wash: null, WashStrength: 0.5, Veil: veil, Weight: null);

    /// <summary>Clears all three, so each test sets up its own state and does not inherit the last test's
    /// state.</summary>
    private static void Bare()
    {
        PageBackdrop.Closed(AlbumPage);
        PageBackdrop.Closed(ArtistPage);
        PageBackdrop.Reading(null);
    }

    [Fact]
    public void The_page_on_screen_lends_the_window_what_it_is_wearing()
    {
        Bare();
        var ground = Sleeve(0.3);
        PageBackdrop.Opened(AlbumPage, ground);
        PageBackdrop.Reading(AlbumPage);

        Assert.Equal((AlbumPage, (PageBackdrop.Ground?)ground), PageBackdrop.Showing);
    }

    /// <summary>
    /// A track starting does not take the page's ground off the window.
    ///
    /// <para>The artwork covers the ground by being drawn over it. What is under an opaque thing stays
    /// what it was. The ground must stay in place while it is hidden. If it were reset to the app's
    /// wallpaper, the artwork would fade in over the wallpaper instead of over the record the reader had
    /// open.</para>
    /// </summary>
    [Fact]
    public void Playing_something_leaves_the_open_page_s_ground_where_it_is()
    {
        Bare();
        var ground = Sleeve(0.3);
        PageBackdrop.Opened(AlbumPage, ground);
        PageBackdrop.Reading(AlbumPage);

        // The art display cannot report anything here, and that is the point. Nothing it does can change
        // this answer.
        Assert.Equal((AlbumPage, (PageBackdrop.Ground?)ground), PageBackdrop.Showing);
    }

    /// <summary>A page put away still holds the colours it was wearing. It is the shell that says which
    /// page is being looked at, and a page nobody is looking at lends nothing.</summary>
    [Fact]
    public void A_page_that_is_not_being_read_lends_nothing()
    {
        Bare();
        PageBackdrop.Opened(AlbumPage, Sleeve(0.3));
        PageBackdrop.Reading(null);

        Assert.Null(PageBackdrop.Showing);
    }

    /// <summary>Grounds are kept per page. A record opened from an artist's page leaves that artist's
    /// colours underneath. Closing the record restores those colours, not the app's own backdrop.</summary>
    [Fact]
    public void Closing_a_record_opened_from_an_artist_puts_the_artist_back()
    {
        Bare();
        var artist = Sleeve(0.0);
        var album = Sleeve(0.4);

        PageBackdrop.Opened(ArtistPage, artist);
        PageBackdrop.Reading(ArtistPage);
        Assert.Equal((ArtistPage, (PageBackdrop.Ground?)artist), PageBackdrop.Showing);

        PageBackdrop.Opened(AlbumPage, album);
        PageBackdrop.Reading(AlbumPage);
        Assert.Equal((AlbumPage, (PageBackdrop.Ground?)album), PageBackdrop.Showing);

        // The record is closed, and the reader is back on the artist.
        PageBackdrop.Closed(AlbumPage);
        PageBackdrop.Reading(ArtistPage);
        Assert.Equal((ArtistPage, (PageBackdrop.Ground?)artist), PageBackdrop.Showing);
    }

    /// <summary>
    /// The two kinds of "nothing". The difference between them is the main point of this class.
    ///
    /// <para>A page that is open but not yet dressed answers with itself and a null ground. The window
    /// keeps whatever is on it. A page that is not open at all answers with nothing, and the app's own
    /// backdrop belongs there. If these were mixed up, the reader would see the wallpaper flash between
    /// two pages that both have colours of their own.</para>
    /// </summary>
    [Fact]
    public void An_undressed_page_is_not_the_same_as_no_page()
    {
        Bare();

        // Open, its sleeve still coming off the disk.
        PageBackdrop.Opened(AlbumPage, null);
        PageBackdrop.Reading(AlbumPage);
        Assert.Equal((AlbumPage, (PageBackdrop.Ground?)null), PageBackdrop.Showing);

        // Closed.
        PageBackdrop.Closed(AlbumPage);
        Assert.Null(PageBackdrop.Showing);
    }

    /// <summary>A page reached through the tab it belongs to, with no page actually open there, leaves the
    /// window on its own backdrop. The reader is back on the wall.</summary>
    [Fact]
    public void A_pane_with_no_page_open_in_it_leaves_the_backdrop_alone()
    {
        Bare();
        PageBackdrop.Reading(AlbumPage);

        Assert.Null(PageBackdrop.Showing);
    }

    [Fact]
    public void A_write_that_changes_nothing_tells_nobody()
    {
        Bare();
        var ground = Sleeve(0.3);
        PageBackdrop.Opened(AlbumPage, ground);
        PageBackdrop.Reading(AlbumPage);

        var told = 0;
        void Count() => told++;

        PageBackdrop.Changed += Count;
        try
        {
            // Restating what is already true, or dressing a page nobody is looking at, does not change
            // the answer the window reads.
            PageBackdrop.Opened(AlbumPage, ground);
            PageBackdrop.Reading(AlbumPage);
            PageBackdrop.Opened(ArtistPage, Sleeve(0.1));
            Assert.Equal(0, told);

            PageBackdrop.Reading(null);
            Assert.Equal(1, told);
        }
        finally
        {
            PageBackdrop.Changed -= Count;
            Bare();
        }
    }
}
