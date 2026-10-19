using System.ComponentModel;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// A line in the import grid says what its row says.
///
/// <para>Every cell on a leaf reads from the <see cref="TrackRow"/> behind it. The row is written to from
/// several places: the tag panel, a recognition result, a mass edit, and the numbering. The numbering gets
/// its own copies of the rows and writes values onto them. So the line must repaint on every write. This
/// cannot depend on each caller remembering to repaint it.</para>
/// </summary>
public class ImportNodeTests
{
    private static (ImportNode Node, TrackRow Row) Leaf(
        string title = "Orinoco Flow", string file = "track01.flac", int discCount = 0)
    {
        var row = new TrackRow(new TrackInfo { FilePath = $"/m/{file}", Extension = ".flac" })
        {
            Title = title,
        };
        var album = new ImportNode { Name = "Watermark", GroupArtist = "Enya", GroupAlbum = "Watermark",
                                     GroupDiscCount = discCount };
        var node = new ImportNode { Name = title, Row = row, Parent = album };
        return (node, row);
    }

    private static List<string?> Watch(ImportNode node)
    {
        var raised = new List<string?>();
        node.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    /// <summary>An album kept by a split cannot be filed, so its box is not offered. The line must show a
    /// mark of its own, or nothing on screen says why. Without that mark, the line would look like a record
    /// in one file, which is the mark that means "split me", on a file that has already been
    /// split.</summary>
    [Fact]
    public void The_album_a_split_kept_says_so_and_offers_no_box()
    {
        var (node, row) = Leaf();
        row.Folder = "Sting/The Dream Of The Blue Turtles/Originals";

        Assert.True(row.IsPreservedOriginal);
        Assert.False(node.CanTick);
        Assert.Equal("\uf187", node.CurrentStateGlyph);   // archive box: kept, never filed
    }

    [Fact]
    public void A_track_beside_the_kept_album_is_offered_as_normal()
    {
        var (node, row) = Leaf();
        row.Folder = "Sting/The Dream Of The Blue Turtles";

        Assert.False(row.IsPreservedOriginal);
        Assert.True(node.CanTick);
        Assert.NotEqual("\uf187", node.CurrentStateGlyph);
    }

    [Fact]
    public void A_row_that_changes_repaints_its_line()
    {
        var (node, row) = Leaf();
        var raised = Watch(node);

        row.Track = "3";

        Assert.Contains(null, raised);   // a null name means "re-read all of it": every cell comes from the row
        Assert.Equal("3", node.ColTrack);
    }

    [Fact]
    public void A_numbered_track_takes_its_number_into_the_tree()
    {
        var (node, row) = Leaf();

        row.Track = "3";

        Assert.Equal("03  Orinoco Flow", node.Name);
    }

    [Fact]
    public void On_a_set_the_disc_goes_in_front_of_the_number()
    {
        var (node, row) = Leaf(discCount: 2);
        row.Disc = "2";

        row.Track = "3";

        Assert.Equal("2-03  Orinoco Flow", node.Name);
    }

    [Fact]
    public void A_track_nobody_has_named_is_still_its_file()
    {
        var (node, row) = Leaf(title: "");

        row.Track = "3";

        Assert.Equal("track01.flac", node.Name);
    }

    [Fact]
    public void The_store_writing_a_row_back_repaints_it_too()
    {
        // This is the path for an edit made off-screen. Values are loaded onto the row that is already on
        // screen, quietly, so nothing is written back to the store. The line must still read itself again.
        var (node, row) = Leaf();
        var raised = Watch(node);

        row.LoadStoredQuietly(new TrackInfo { FilePath = "/m/track01.flac", Extension = ".flac", Title = "Orinoco Flow", TrackNumber = 3 }, isChecked: true);

        Assert.Contains(null, raised);
        Assert.Equal("3", node.ColTrack);
        Assert.Equal("03  Orinoco Flow", node.Name);
    }
}
