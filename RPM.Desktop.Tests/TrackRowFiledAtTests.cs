using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// A row told where its file went.
///
/// <para>The fault this guards against: a write re-files the files under the naming template and moves the
/// mirror rows with them. But the <see cref="TrackRow"/> objects that a window still holds keep the path
/// they were made with. Every later write then looks for a file that is no longer there, finds nothing,
/// and skips the row as unreadable. This happens silently, so the second window seems to save and saves
/// nothing. Two windows over one set of rows in one sitting is the ordinary case. For example, the album
/// editor's release lookup names the tracks, and then the editor itself is saved.</para>
/// </summary>
public class TrackRowFiledAtTests
{
    private static TrackRow Row(string path) => new(new TrackInfo
    {
        FilePath = path,
        Extension = ".mp3",
        Artist = "The Band",
        Album = "The Record",
        Title = "One",
    });

    [Fact]
    public void A_row_follows_its_file_to_where_it_landed()
    {
        var row = Row("/lib/Old Name/01 One.mp3");

        row.FiledAt("/lib/The Band/The Record/01 One.mp3");

        Assert.Equal("/lib/The Band/The Record/01 One.mp3", row.FilePath);
        Assert.Equal("/lib/The Band/The Record/01 One.mp3", row.Current.FilePath);
    }

    /// <summary>Only the path. Everything else the current side says was read off the file and is still
    /// true of it: and the wanted side, which is what a write actually carries, is untouched.</summary>
    [Fact]
    public void Nothing_but_the_path_moves()
    {
        var row = Row("/lib/a/1.mp3");
        row.Title = "Corrected";

        row.FiledAt("/lib/b/1.mp3");

        Assert.Equal("The Band", row.CurrentArtist);
        Assert.Equal("The Record", row.CurrentAlbum);
        Assert.Equal("One", row.Current.Title);
        Assert.Equal("Corrected", row.Title);
    }

    /// <summary>A file the applier refused never moved, so the row is left naming the path it still
    /// has. Nothing about a refusal should look like a move.</summary>
    [Fact]
    public void A_row_that_did_not_move_is_left_alone()
    {
        var row = Row("/lib/a/1.mp3");

        row.FiledAt("/lib/a/1.mp3");
        row.FiledAt("");
        row.FiledAt("   ");

        Assert.Equal("/lib/a/1.mp3", row.FilePath);
    }

    /// <summary>The grid is bound to these rows, so a move has to be announced.</summary>
    [Fact]
    public void The_move_is_announced()
    {
        var row = Row("/lib/a/1.mp3");
        var told = new List<string?>();
        row.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        row.FiledAt("/lib/b/1.mp3");

        Assert.Contains(nameof(TrackRow.FilePath), told);
        Assert.Contains(nameof(TrackRow.Current), told);
    }
}
