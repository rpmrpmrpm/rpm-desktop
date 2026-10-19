using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where a row of the Now playing list gets its picture from.
///
/// <para>The rule that matters is the borrowed one. A record played off a friend's shelf has no file on
/// this disk. Its path names a folder in the mirror, and there is no cover image in it. The list drew every
/// one of those rows as an empty square, while the same record's sleeve was in the header just above. So a
/// row with no picture beside it must go and ask for one.</para>
///
/// <para>The other half: a record that IS here, and simply has no cover.jpg beside it, asks nobody. Its
/// files are the whole answer. A network request for each row of a local album with no artwork would fetch
/// hundreds of times to draw the same empty square.</para>
/// </summary>
public sealed class QueueRowSleeveTests
{
    [Fact]
    public void The_album_folders_cover_is_the_picture_when_there_is_one()
    {
        Assert.Equal(RowSleeve.FolderImage,
                     PlaylistTrackRow.SleeveFor("/music/Phosphorescent/cover.jpg", fileOnDisk: true));
    }

    [Fact]
    public void A_record_this_machine_does_not_hold_is_asked_for_elsewhere()
    {
        // A borrowed track: the path is a line in the friend's mirror, with no file behind it.
        Assert.Equal(RowSleeve.Elsewhere, PlaylistTrackRow.SleeveFor(null, fileOnDisk: false));
    }

    [Fact]
    public void A_record_that_is_here_with_no_cover_beside_it_asks_nobody()
    {
        Assert.Equal(RowSleeve.None, PlaylistTrackRow.SleeveFor(null, fileOnDisk: true));
    }

    [Fact]
    public void A_folder_cover_is_read_even_where_the_track_itself_has_gone()
    {
        // The picture is a file of its own: it is there to be read whether or not the song beside it is.
        Assert.Equal(RowSleeve.FolderImage,
                     PlaylistTrackRow.SleeveFor("/music/Phosphorescent/cover.jpg", fileOnDisk: false));
    }
}
