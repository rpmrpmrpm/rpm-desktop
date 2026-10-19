using System.ComponentModel;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// What happens to a queue row that is read out of the store a second time.
///
/// <para>The import pane reads its pages again for almost anything: a field edited, a record arranged,
/// another batch scanned. The row that comes back is the same file. In this pane the ticks are the grid's
/// selection, so it has to come back as the same objects, carrying the new values, without telling the
/// store anything it already knows.</para>
/// </summary>
public class QueueNodeReuseTests
{
    private static TrackInfo File(string title) => new()
    {
        FilePath = $"/m/{title}.flac",
        Extension = ".flac",
        Artist = "Enya",
        Album = "Watermark",
        Title = title,
    };

    private static TrackRow Row(string title) => TrackRow.FromStored(File(title), File(title), false);

    private static ImportNode Album(string name, int depth = 1) => new()
    {
        Name = name,
        Depth = depth,
        GroupArtist = "Enya",
        GroupAlbum = name,
        VirtualTrackCount = 2,
    };

    private static ImportNode Leaf(TrackRow row, ImportNode album, int offset = 0) => new()
    {
        Name = row.Title,
        Depth = album.Depth + 1,
        Row = row,
        Parent = album,
        QueueId = 7,
        LeafOffset = offset,
    };

    // ── the quiet load ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_quiet_load_takes_the_stored_values()
    {
        var row = Row("Orinoco Flow");
        var stored = File("Orinoco Flow") with { Year = 1988, Genre = "New Age", TrackNumber = 2 };

        row.LoadStoredQuietly(stored, isChecked: true);

        Assert.Equal("1988", row.Year);
        Assert.Equal("New Age", row.Genre);
        Assert.Equal("2", row.Track);
        Assert.True(row.IsChecked);
    }

    /// <summary>The trap this method exists for: through the ordinary setters, every page fetch would
    /// write every row it read straight back to the store.</summary>
    [Fact]
    public void A_quiet_load_asks_for_no_write_back()
    {
        var row = Row("Cursum Perficio");
        var wanted = 0;
        var ticked = 0;
        row.WantedChanged += () => wanted++;
        row.CheckedChanged += () => ticked++;

        row.LoadStoredQuietly(File("Cursum Perficio") with { Year = 1988 }, isChecked: true);

        Assert.Equal(0, wanted);
        Assert.Equal(0, ticked);
    }

    [Fact]
    public void A_quiet_load_says_so_once()
    {
        var row = Row("On Your Shore");
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        row.LoadStoredQuietly(File("On Your Shore") with { Year = 1988, Genre = "New Age" }, isChecked: false);

        var name = Assert.Single(changes);
        Assert.True(string.IsNullOrEmpty(name), "an unnamed change means re-read all of it");
    }

    /// <summary>A quietly loaded row is still a live row: the next edit the user makes reaches the
    /// store like any other.</summary>
    [Fact]
    public void A_quiet_load_leaves_the_row_live()
    {
        var row = Row("Storms in Africa");
        var wanted = 0;
        row.WantedChanged += () => wanted++;

        row.LoadStoredQuietly(File("Storms in Africa") with { Year = 1988 }, isChecked: false);
        row.Genre = "New Age";

        Assert.Equal(1, wanted);
    }

    // ── the re-seat ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_reseated_leaf_keeps_its_row_and_takes_its_new_place()
    {
        var row = Row("Evening Falls");
        var was = Album("Watermark");
        var now = Album("Shepherd Moons");
        var leaf = Leaf(row, was, offset: 4);

        leaf.Reseat("03  Evening Falls", now, leafOffset: 2);

        Assert.Same(row, leaf.Row);
        Assert.Equal("03  Evening Falls", leaf.Name);
        Assert.Same(now, leaf.Parent);
        Assert.Equal(now.Depth + 1, leaf.Depth);
        Assert.Equal(2, leaf.LeafOffset);
    }

    [Fact]
    public void A_reseated_leaf_says_to_re_read_all_of_it()
    {
        var leaf = Leaf(Row("Exile"), Album("Watermark"));
        var changes = new List<string?>();
        leaf.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        leaf.Reseat("05  Exile", Album("Watermark"), leafOffset: 5);

        var name = Assert.Single(changes);
        Assert.True(string.IsNullOrEmpty(name), "an unnamed change means re-read all of it");
    }

    /// <summary>The two halves together: the mark a row wears is worked out from the fields, so a row
    /// whose missing field has just been filled in has to stop asking for it.</summary>
    [Fact]
    public void A_re_read_row_wears_the_new_mark()
    {
        var bare = new TrackInfo { FilePath = "/m/07.flac", Extension = ".flac" };
        var row = TrackRow.FromStored(bare, bare, isChecked: false);
        var album = Album("Watermark");
        var leaf = Leaf(row, album);

        Assert.Equal("\uf059", leaf.CurrentStateGlyph);   // question circle — fields still empty

        row.LoadStoredQuietly(
            bare with
            {
                Artist = "Enya", AlbumArtist = "Enya", Album = "Watermark", Title = "Exile",
                TrackNumber = 7, Year = 1988, Genre = "New Age",
            },
            isChecked: false);
        leaf.Reseat("07  Exile", album, leafOffset: 6);

        Assert.Equal("\uf00c", leaf.CurrentStateGlyph);   // check — everything the shelf needs
    }

    // ── the name ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_new_title_reaches_the_tree_column()
    {
        var leaf = Leaf(Row("Untitled"), Album("Watermark"));
        var changes = new List<string?>();
        leaf.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        leaf.Name = "01  Watermark";

        Assert.Equal("01  Watermark", leaf.Name);
        Assert.Equal(new[] { nameof(ImportNode.Name) }, changes);
    }

    [Fact]
    public void The_same_title_says_nothing()
    {
        var leaf = Leaf(Row("Watermark"), Album("Watermark"));
        var changes = 0;
        leaf.PropertyChanged += (_, _) => changes++;

        leaf.Name = leaf.Name;

        Assert.Equal(0, changes);
    }
}
