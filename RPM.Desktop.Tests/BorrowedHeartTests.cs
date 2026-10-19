using RPM.Core.Cloud;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// What a line inside an opened record may be MARKED with, on somebody else's shelf.
///
/// <para>
/// A borrowed song has a row in the mirror held for that computer, and the mark on it is the
/// listener's own: what they think of a record on a friend's disk, kept on this machine and never sent
/// back to the friend. So the heart is offered on every line. What still depends on the shelf is the
/// CLOUD mark beside it, which on a borrowed song shows how the music arrives, not whose disk it is on
/// (see <c>BorrowedShelfMarksTests</c>).
/// </para>
/// </summary>
public class BorrowedHeartTests
{
    private static TrackInfo Track(string title, uint number) => new()
    {
        FilePath = $"/music/ABBA/Arrival/{number:00} {title}.flac",
        Extension = ".flac",
        Title = title,
        Artist = "ABBA",
        AlbumArtist = "ABBA",
        Album = "Arrival",
        TrackNumber = number,
        DurationSeconds = 200,
    };

    private static readonly TrackInfo[] Record =
    [
        Track("When I Kissed the Teacher", 1),
        Track("Dancing Queen", 2),
    ];

    /// <summary>Always on a favourite, on hover for the rest, and never on a line that is not a song:
    /// a disc heading cannot be a favourite.</summary>
    [Theory]
    [InlineData(ShelfReading.Own)]
    [InlineData(ShelfReading.BorrowedFromOnline)]
    [InlineData(ShelfReading.BorrowedFromOffline)]
    public void Every_song_is_offered_a_heart_whosever_shelf_it_is_on(ShelfReading shelf)
    {
        var lines = AlbumTrackItem.Build(Record, shelf: shelf);
        var line = lines.First(l => l.IsTrack);

        Assert.False(line.ShowHeart);

        line.IsHovered = true;
        Assert.True(line.ShowHeart);

        line.IsHovered = false;
        line.IsFavorite = true;
        Assert.True(line.ShowHeart);
    }

    /// <summary>
    /// The lender leaves the network, or the listener does. The marks are read again on the lines
    /// already on screen (the record is not rebuilt), and the heart does not change.
    ///
    /// <para>Otherwise a mark made a moment earlier would disappear as soon as the shelf is read
    /// again, so the same mark would be shown two ways within a second.</para>
    /// </summary>
    [Fact]
    public void Reading_the_marks_again_leaves_the_hearts_where_they_are()
    {
        var lines = AlbumTrackItem.Build(Record, shelf: ShelfReading.Own);
        var line = lines.First(l => l.IsTrack);
        line.IsFavorite = true;
        Assert.True(line.ShowHeart);

        AlbumTrackItem.ApplyCloud(lines, Record, shelf: ShelfReading.BorrowedFromOnline);
        Assert.True(line.ShowHeart);

        AlbumTrackItem.ApplyCloud(lines, Record, shelf: ShelfReading.Own);
        Assert.True(line.ShowHeart);
    }
}
