using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which square photograph the artist page shows. The track pane shows the act's first square, so the
/// page takes another one where there is one, and the same one, or the backdrop, where there is not.
/// </summary>
public class ArtistPageSquareTests
{
    private static readonly string[] Squares =
    [
        "https://assets.fanart.tv/fanart/music/x/artistthumb/sq-1.jpg",
        "https://assets.fanart.tv/fanart/music/x/artistthumb/sq-2.jpg",
        "https://assets.fanart.tv/fanart/music/x/artistthumb/sq-3.jpg",
    ];

    private const string Backdrop = "https://assets.fanart.tv/fanart/music/x/artistbackground/wide-1.jpg";

    /// <summary>The usual case: the pane shows the first square, and the page shows the next one.</summary>
    [Fact]
    public void The_page_shows_the_next_square_after_the_panes()
        => Assert.Equal(Squares[1], ArtistPortraits.OtherSquare(Squares[0], Squares));

    /// <summary>It is the first square that is not the pane's, wherever the pane's is in the list.</summary>
    [Fact]
    public void A_pane_square_further_down_leaves_the_first_one_for_the_page()
        => Assert.Equal(Squares[0], ArtistPortraits.OtherSquare(Squares[2], Squares));

    /// <summary>An act with one square shows that square on both.</summary>
    [Fact]
    public void One_square_is_shown_on_both()
        => Assert.Equal(Squares[0], ArtistPortraits.OtherSquare(Squares[0], [Squares[0]]));

    /// <summary>An act with no square: the pane wears a backdrop, and so does the page.</summary>
    [Fact]
    public void An_act_with_no_square_keeps_the_backdrop()
        => Assert.Equal(Backdrop, ArtistPortraits.OtherSquare(Backdrop, []));

    /// <summary>The pane chose a backdrop before the squares arrived. Its picture is not one of the
    /// squares, so the page keeps it too, and the two surfaces still agree.</summary>
    [Fact]
    public void A_pane_on_a_backdrop_keeps_the_page_on_it_when_squares_arrive()
        => Assert.Equal(Backdrop, ArtistPortraits.OtherSquare(Backdrop, Squares));

    /// <summary>The same picture from a mirror, on another host or in other capitals, is the same picture
    /// and not a second square.</summary>
    [Theory]
    [InlineData("https://fanart.tv/mirror/sq-1.jpg")]
    [InlineData("HTTPS://ASSETS.FANART.TV/FANART/MUSIC/X/ARTISTTHUMB/SQ-1.JPG")]
    public void A_mirror_of_the_panes_square_is_the_same_picture(string pane)
        => Assert.Equal(Squares[1], ArtistPortraits.OtherSquare(pane, Squares));

    /// <summary>No photograph at all is no photograph on the page either.</summary>
    [Fact]
    public void No_photograph_is_no_photograph()
        => Assert.Null(ArtistPortraits.OtherSquare(null, []));
}
