using RPM.Core.Models;
using RPM.Desktop.Presentation.RPaMp.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// The second line of a row on the Now playing card: artist and album on one line, as on the phone's Now
/// playing page. A tag that is missing is left out with its separator, so a row never starts or ends on a dot.
/// </summary>
public sealed class DeckRowBylineTests
{
    private static DeckRow Row(string? artist, string? album) =>
        new(new PlaylistTrack(new TrackInfo
        {
            FilePath = "/lib/a/1.flac",
            Extension = ".flac",
            Title = "Song",
            Artist = artist,
            Album = album,
        }, radio: null), DeckRow.NoCovers);

    [Fact]
    public void Artist_and_album_are_joined_by_a_dot() =>
        Assert.Equal("Sting · Fields of Gold", Row("Sting", "Fields of Gold").Byline);

    [Fact]
    public void A_missing_album_leaves_the_artist_alone() =>
        Assert.Equal("Sting", Row("Sting", null).Byline);

    [Fact]
    public void A_missing_artist_leaves_the_album_alone() =>
        Assert.Equal("Fields of Gold", Row("  ", "Fields of Gold").Byline);

    [Fact]
    public void No_tags_give_an_empty_line() =>
        Assert.Equal("", Row(null, null).Byline);

    [Fact]
    public void A_row_given_another_track_shows_that_tracks_line()
    {
        var row = Row("Sting", "Fields of Gold");
        row.Show(new PlaylistTrack(new TrackInfo
        {
            FilePath = "/lib/b/1.flac",
            Extension = ".flac",
            Title = "Prologue",
            Artist = "Loreena McKennitt",
            Album = "The Book of Secrets",
        }, radio: null));
        Assert.Equal("Loreena McKennitt · The Book of Secrets", row.Byline);
    }
}
