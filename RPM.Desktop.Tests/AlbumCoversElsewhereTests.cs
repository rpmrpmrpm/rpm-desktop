using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="AlbumCovers.SleeveElsewhere"/>: whether the wall asks the cloud or the computer that holds a
/// record for its cover, once this disk has no picture for it.
///
/// <para>The fault it prevents: files deleted while the app runs keep their Synced rows until the next
/// library sync. A wall that trusted the row read nothing from the disk, and answered "no cover" for an
/// album whose cover was in the cloud.</para>
/// </summary>
public class AlbumCoversElsewhereTests
{
    private static LibraryTrack Track(CloudState state, bool peerAdopted = false) => new()
    {
        RelativePath = "Counting Crows/Across A Wire (Live In New York)/33 - A Long December.m4a",
        Folder = "Counting Crows/Across A Wire (Live In New York)",
        FileName = "33 - A Long December.m4a",
        Extension = ".m4a",
        CloudState = state,
        PeerAdopted = peerAdopted,
    };

    [Fact]
    public void A_synced_track_whose_file_was_deleted_asks_elsewhere()
    {
        Assert.True(AlbumCovers.SleeveElsewhere(Track(CloudState.Synced), fileHere: false));
    }

    [Fact]
    public void A_synced_track_whose_file_is_here_has_no_cover_anywhere_else()
    {
        Assert.False(AlbumCovers.SleeveElsewhere(Track(CloudState.Synced), fileHere: true));
    }

    [Fact]
    public void A_local_track_whose_file_is_here_has_no_cover_anywhere_else()
    {
        Assert.False(AlbumCovers.SleeveElsewhere(Track(CloudState.Local), fileHere: true));
    }

    [Fact]
    public void A_cloud_only_track_asks_elsewhere()
    {
        Assert.True(AlbumCovers.SleeveElsewhere(Track(CloudState.CloudOnly), fileHere: false));
    }

    [Fact]
    public void A_borrowed_record_asks_elsewhere()
    {
        Assert.True(AlbumCovers.SleeveElsewhere(Track(CloudState.Local, peerAdopted: true), fileHere: true));
    }
}
