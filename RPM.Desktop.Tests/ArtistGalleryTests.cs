using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Tests;

/// <summary>
/// The order an artist's photographs are paged through in, at the top of their page.
///
/// <para>One rule: "open on what is already on screen". The band shows the picture the app chose for
/// that artist, so page one must be that picture, or the first press of an arrow would jump to a
/// picture the user did not ask for.</para>
/// </summary>
public class ArtistGalleryTests
{
    private static readonly string[] Photos =
    [
        "https://fanart.tv/one.jpg",
        "https://fanart.tv/two.jpg",
        "https://theaudiodb.com/three.jpg",
    ];

    /// <summary>The usual case: the chosen picture is somewhere down the list, and it moves to the front
    /// without changing the order of the rest.</summary>
    [Fact]
    public void The_picture_on_screen_leads_and_the_rest_keep_their_order()
    {
        var ordered = ArtistPortraits.WithFirst(Photos, "https://theaudiodb.com/three.jpg");

        Assert.Equal(
            ["https://theaudiodb.com/three.jpg", "https://fanart.tv/one.jpg", "https://fanart.tv/two.jpg"],
            ordered);
    }

    /// <summary>Already first: nothing to do, and nothing changes.</summary>
    [Fact]
    public void A_picture_already_first_leaves_the_list_alone()
        => Assert.Equal(Photos, ArtistPortraits.WithFirst(Photos, "https://fanart.tv/one.jpg"));

    /// <summary>
    /// The picture on screen is not in the list at all: a saved order and a new answer from the art
    /// sources can disagree. It still comes first, because otherwise the carousel could never page back
    /// to the photograph it opened on.
    /// </summary>
    [Fact]
    public void A_picture_the_list_does_not_hold_is_put_in_front_of_it()
    {
        var ordered = ArtistPortraits.WithFirst(Photos, "https://fanart.tv/four.jpg");

        Assert.Equal(4, ordered.Count);
        Assert.Equal("https://fanart.tv/four.jpg", ordered[0]);
        Assert.Equal(Photos, ordered.Skip(1));
    }

    private static readonly string[] Squares =
    [
        "https://assets.fanart.tv/fanart/music/x/artistthumb/sq-1.jpg",
        "https://assets.fanart.tv/fanart/music/x/artistthumb/sq-2.jpg",
    ];

    /// <summary>An artist with square photos shows only those: the band opened on one of them, so the
    /// pager shows the squares and leaves out the backdrops.</summary>
    [Fact]
    public void A_band_that_opened_on_a_square_pages_through_squares_only()
    {
        Assert.True(ArtistPortraits.PagesSquares(Squares[1], Squares));
        Assert.Equal([Squares[1], Squares[0]], ArtistPortraits.WithFirst(Squares, Squares[1]));
    }

    /// <summary>Matched however the address is spelt, as <see cref="ArtistPortraits.WithFirst"/>
    /// matches.</summary>
    [Fact]
    public void The_opening_square_is_recognised_in_another_case()
        => Assert.True(ArtistPortraits.PagesSquares(Squares[0].ToUpperInvariant(), Squares));

    /// <summary>An artist with no square opens on a backdrop, and pages through backdrops.</summary>
    [Fact]
    public void A_band_that_opened_on_a_backdrop_pages_through_backdrops()
    {
        Assert.False(ArtistPortraits.PagesSquares(Photos[0], []));
        Assert.False(ArtistPortraits.PagesSquares(null, []));
    }

    /// <summary>The band opened on a backdrop although squares are known now: the session chose the
    /// backdrop before the squares arrived. The pager follows the picture on screen, so page one is
    /// still what is showing, and it pages through backdrops.</summary>
    [Fact]
    public void A_band_that_opened_on_a_backdrop_keeps_backdrops_even_when_squares_arrive()
        => Assert.False(ArtistPortraits.PagesSquares(Photos[0], Squares));

    /// <summary>Nothing chosen yet (the wall has not picked a picture for this artist), so the sources'
    /// own order is kept.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_picture_on_screen_leaves_the_list_as_it_came(string? shown)
        => Assert.Equal(Photos, ArtistPortraits.WithFirst(Photos, shown));

    /// <summary>Matched however the address is spelt, so the list does not get a second copy of one
    /// photograph when two catalogues capitalise the host differently.</summary>
    [Fact]
    public void A_picture_matched_in_another_case_is_moved_rather_than_added()
    {
        var ordered = ArtistPortraits.WithFirst(Photos, "HTTPS://FANART.TV/TWO.JPG");

        Assert.Equal(3, ordered.Count);
        Assert.Equal("HTTPS://FANART.TV/TWO.JPG", ordered[0]);
        Assert.Equal(["https://fanart.tv/one.jpg", "https://theaudiodb.com/three.jpg"], ordered.Skip(1));
    }

    /// <summary>An artist with one photograph: the list has one item whatever is chosen, and the band's
    /// controls stay hidden for it.</summary>
    [Fact]
    public void One_photograph_is_one_photograph()
        => Assert.Single(ArtistPortraits.WithFirst(["https://fanart.tv/one.jpg"], "https://fanart.tv/one.jpg"));
}
