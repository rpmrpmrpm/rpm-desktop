using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// The cloud marks on the lines inside a BORROWED record.
///
/// <para>Every row of a borrowed shelf is written down as a cloud row, whether or not its owner has
/// cleared it off their own disk. That keeps the shelf playable when the owner's computer is off. Read
/// the way the user's own collection is read, every borrowed song would claim to use none of this disk.
/// That is true of all of them, so it tells the listener nothing. On somebody else's shelf the mark
/// answers the question the listener has: where is this coming from.</para>
/// </summary>
public class BorrowedShelfMarksTests
{
    private static TrackInfo Track(string title, CloudState state) => new()
    {
        FilePath = $"/music/Lender/Record/{title}.flac",
        Extension = ".flac",
        Title = title,
        Album = "Record",
        CloudState = state,
    };

    private static List<AlbumCloudBadge> Marks(
        IReadOnlyList<TrackInfo> tracks, ShelfReading shelf)
    {
        var lines = AlbumTrackItem.Build(tracks, classical: false, uploadsStopped: false, shelf);
        return [.. lines.Where(l => l.Info is not null).Select(l => l.CloudBadge)];
    }

    /// <summary>
    /// A record half in the lender's cloud and half on the lender's disk, with the lender's computer on
    /// the network. The lines have different states, so each one shows a mark. The marks say which half
    /// stops playing when that computer goes off.
    /// </summary>
    [Fact]
    public void A_borrowed_record_marks_the_songs_that_need_their_computer()
    {
        var tracks = new List<TrackInfo>
        {
            Track("1", CloudState.CloudOnly),
            Track("2", CloudState.Local),
        };

        Assert.Equal(
            [AlbumCloudBadge.Full, AlbumCloudBadge.PeerLink],
            Marks(tracks, ShelfReading.BorrowedFromOnline));
    }

    /// <summary>The lender's computer is off, so what is left of the record comes only from the store.
    /// On a borrowed shelf that is the one case where the cyan mark is correct.</summary>
    [Fact]
    public void A_borrowed_record_goes_cyan_when_the_lender_does_not_answer()
    {
        var tracks = new List<TrackInfo>
        {
            Track("1", CloudState.CloudOnly),
            Track("2", CloudState.Local),
        };

        Assert.Equal(
            [AlbumCloudBadge.Offloaded, AlbumCloudBadge.PeerLink],
            Marks(tracks, ShelfReading.BorrowedFromOffline));
    }

    /// <summary>The same rows on the user's own shelf are read as usual. Borrowing does not change what a
    /// cloud mark means on the user's own shelf.</summary>
    [Fact]
    public void The_users_own_shelf_is_untouched()
    {
        var tracks = new List<TrackInfo>
        {
            Track("1", CloudState.CloudOnly),
            Track("2", CloudState.Local),
        };

        Assert.Equal(
            [AlbumCloudBadge.Offloaded, AlbumCloudBadge.None],
            Marks(tracks, ShelfReading.Own));
    }

    /// <summary>A record whose songs all agree says it once, on its tile. The lines stay bare, as the
    /// drawer already does for the user's own shelf. Borrowing does not change this.</summary>
    [Fact]
    public void A_borrowed_record_that_agrees_with_itself_leaves_its_lines_bare()
    {
        var tracks = new List<TrackInfo>
        {
            Track("1", CloudState.CloudOnly),
            Track("2", CloudState.CloudOnly),
        };

        Assert.All(
            Marks(tracks, ShelfReading.BorrowedFromOnline),
            badge => Assert.Equal(AlbumCloudBadge.None, badge));
    }
}
