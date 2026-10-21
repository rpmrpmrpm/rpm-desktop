using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="StaleRecordCovers.Find"/> is a pure rule over a node list and a way to ask whether a path
/// exists. It is kept apart from <c>MainPage.Panes.cs</c> so it can be asked directly, with a fake disk,
/// rather than only observed through a real disk and a real database.
/// </summary>
public class StaleRecordCoversTests
{
    private static PlaylistNode RecordsNode(long id, params string[] folders) =>
        new(id, null, PlaylistNodeKind.Playlist, "list", 0, false, new PlaylistCover.Records(folders));

    [Fact]
    public void Every_folder_present_is_not_stale()
    {
        var node = RecordsNode(1, "/Users/x/Music/A", "/Users/x/Music/B");

        var stale = StaleRecordCovers.Find([node], _ => true);

        Assert.Empty(stale);
    }

    [Fact]
    public void A_folder_gone_from_the_machines_own_disk_is_stale()
    {
        // No /Volumes prefix. This is an ordinary path on the boot disk, which this treats as always
        // mounted. So the folder's own absence is the whole answer.
        var node = RecordsNode(2, "/Users/x/Music/Retagged Away");

        var stale = StaleRecordCovers.Find([node], _ => false);

        Assert.Equal([2L], stale);
    }

    [Fact]
    public void A_folder_gone_because_its_whole_volume_is_unmounted_is_not_stale()
    {
        // Neither the folder nor "/Volumes/Backup" answers. The external drive is not plugged in. This is
        // not the album having been retagged out of the playlist.
        var node = RecordsNode(3, "/Volumes/Backup/Music/Some Album");

        var stale = StaleRecordCovers.Find([node], _ => false);

        Assert.Empty(stale);
    }

    [Fact]
    public void A_folder_gone_from_a_volume_that_is_still_mounted_is_stale()
    {
        // The volume itself answers, but the album folder under it does not. This is a genuine retag, and
        // the disk is there to prove it.
        var node = RecordsNode(4, "/Volumes/Backup/Music/Some Album");

        var stale = StaleRecordCovers.Find([node], path => path == "/Volumes/Backup");

        Assert.Equal([4L], stale);
    }

    [Fact]
    public void Only_records_covers_are_ever_considered()
    {
        var automatic = new PlaylistNode(5, null, PlaylistNodeKind.Playlist, "list", 0, false, new PlaylistCover.Automatic());
        var picture = new PlaylistNode(6, null, PlaylistNodeKind.Playlist, "list", 0, false, new PlaylistCover.Picture());
        var figure = new PlaylistNode(7, null, PlaylistNodeKind.Playlist, "list", 0, false, new PlaylistCover.Figure(0, 1, CoverMark.None));

        var stale = StaleRecordCovers.Find([automatic, picture, figure], _ => false);

        Assert.Empty(stale);
    }

    // ── Linux mount conventions ──────────────────────────────────────────────────────────────────────────────────
    // RPM.Desktop.csproj ships linux-arm, linux-arm64 and linux-x64. So a removable drive that goes missing
    // there must be told apart from a retag, in the same way as on macOS. There are three conventions:
    // udisks2's modern /run/media/<user>/<volume>, the older /media/<user>/<volume>, and a manually
    // mounted /mnt/<volume>.

    [Fact]
    public void A_folder_gone_because_a_run_media_volume_is_unmounted_is_not_stale()
    {
        var node = RecordsNode(8, "/run/media/alex/Backup/Music/Some Album");

        var stale = StaleRecordCovers.Find([node], _ => false);

        Assert.Empty(stale);
    }

    [Fact]
    public void A_folder_gone_from_a_mounted_run_media_volume_is_stale()
    {
        var node = RecordsNode(9, "/run/media/alex/Backup/Music/Some Album");

        var stale = StaleRecordCovers.Find([node], path => path == "/run/media/alex/Backup");

        Assert.Equal([9L], stale);
    }

    [Fact]
    public void A_folder_gone_because_a_media_volume_is_unmounted_is_not_stale()
    {
        var node = RecordsNode(10, "/media/alex/Backup/Music/Some Album");

        var stale = StaleRecordCovers.Find([node], _ => false);

        Assert.Empty(stale);
    }

    [Fact]
    public void A_folder_gone_from_a_mounted_media_volume_is_stale()
    {
        var node = RecordsNode(11, "/media/alex/Backup/Music/Some Album");

        var stale = StaleRecordCovers.Find([node], path => path == "/media/alex/Backup");

        Assert.Equal([11L], stale);
    }

    [Fact]
    public void A_folder_gone_because_an_mnt_volume_is_unmounted_is_not_stale()
    {
        var node = RecordsNode(12, "/mnt/Backup/Music/Some Album");

        var stale = StaleRecordCovers.Find([node], _ => false);

        Assert.Empty(stale);
    }

    [Fact]
    public void A_folder_gone_from_a_windows_drive_that_is_there_is_stale()
    {
        var node = RecordsNode(14, @"D:\Music\Retagged Away");

        var stale = StaleRecordCovers.Find([node], path => path == @"D:\");

        Assert.Equal([14L], stale);
    }

    [Fact]
    public void A_folder_on_a_windows_drive_that_is_unplugged_is_not_stale()
    {
        // Neither the folder nor the drive answers: the USB drive is not plugged in.
        var node = RecordsNode(15, @"E:\Music\Some Album");

        var stale = StaleRecordCovers.Find([node], _ => false);

        Assert.Empty(stale);
    }

    [Fact]
    public void A_folder_on_a_network_share_that_cannot_be_reached_is_not_stale()
    {
        var node = RecordsNode(16, @"\\nas\music\Some Album");

        Assert.Empty(StaleRecordCovers.Find([node], _ => false));
        Assert.Equal([16L], StaleRecordCovers.Find([node], path => path == @"\\nas\music"));
    }

    [Fact]
    public void A_folder_gone_from_a_mounted_mnt_volume_is_stale()
    {
        var node = RecordsNode(13, "/mnt/Backup/Music/Some Album");

        var stale = StaleRecordCovers.Find([node], path => path == "/mnt/Backup");

        Assert.Equal([13L], stale);
    }
}
