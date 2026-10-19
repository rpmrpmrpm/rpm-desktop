using RPM.Core.Models;
using RPM.Desktop.Presentation.Import;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// Writing an arrangement down as track numbers.
///
/// <para>Arranging a record and numbering it are two acts in the workbench: a drag says what comes
/// after what and touches nothing else, and this is where somebody says to make that the numbering.
/// So these tests are about ORDER GIVEN, never about what the rows already claimed.</para>
/// </summary>
public class QueueNumberingTests
{
    private static TrackRow Row(
        string title, string disc = "", string track = "", string album = "", string artist = "") =>
        new(new TrackInfo { FilePath = $"/m/{title}.flac", Extension = ".flac" })
        { Title = title, Disc = disc, Track = track, Album = album, Artist = artist };

    [Fact]
    public void Numbers_in_display_order_from_one()
    {
        var rows = new[] { Row("c"), Row("a"), Row("b") };

        var changed = QueueNumbering.Renumber(rows);

        Assert.Equal(3, changed);
        Assert.Equal(["1", "2", "3"], rows.Select(r => r.Track));
    }

    [Fact]
    public void Numbers_each_disc_from_one()
    {
        var rows = new[]
        {
            Row("a", disc: "1"), Row("b", disc: "1"),
            Row("c", disc: "2"), Row("d", disc: "2"), Row("e", disc: "2"),
        };

        QueueNumbering.Renumber(rows);

        Assert.Equal(["1", "2", "1", "2", "3"], rows.Select(r => r.Track));
    }

    [Fact]
    public void Writes_each_discs_own_track_count()
    {
        var rows = new[]
        {
            Row("a", disc: "1"), Row("b", disc: "1"),
            Row("c", disc: "2"), Row("d", disc: "2"), Row("e", disc: "2"),
        };

        QueueNumbering.Renumber(rows);

        Assert.Equal(["2", "2", "3", "3", "3"], rows.Select(r => r.TrackCount));
    }

    [Fact]
    public void Rows_with_no_disc_are_one_disc_together()
    {
        var rows = new[] { Row("a"), Row("b", disc: "0"), Row("c") };

        QueueNumbering.Renumber(rows);

        Assert.Equal(["1", "2", "3"], rows.Select(r => r.Track));
    }

    [Fact]
    public void Every_record_starts_again_at_one()
    {
        // A selection across three albums is three numberings. One running count of ninety would be
        // the numbering of a record nobody has.
        var rows = new[]
        {
            Row("a", album: "Watermark", artist: "Enya"),
            Row("b", album: "Watermark", artist: "Enya"),
            Row("c", album: "Shepherd Moons", artist: "Enya"),
            Row("d", album: "Shepherd Moons", artist: "Enya"),
            Row("e", album: "Shepherd Moons", artist: "Enya"),
        };

        QueueNumbering.Renumber(rows);

        Assert.Equal(["1", "2", "1", "2", "3"], rows.Select(r => r.Track));
        Assert.Equal(["2", "2", "3", "3", "3"], rows.Select(r => r.TrackCount));
    }

    [Fact]
    public void Two_artists_with_the_same_album_name_are_two_records()
    {
        var rows = new[]
        {
            Row("a", album: "Greatest Hits", artist: "Queen"),
            Row("b", album: "Greatest Hits", artist: "Abba"),
        };

        QueueNumbering.Renumber(rows);

        Assert.Equal(["1", "1"], rows.Select(r => r.Track));
    }

    [Fact]
    public void A_records_discs_each_start_at_one()
    {
        var rows = new[]
        {
            Row("a", disc: "1", album: "The Wall", artist: "Pink Floyd"),
            Row("b", disc: "1", album: "The Wall", artist: "Pink Floyd"),
            Row("c", disc: "2", album: "The Wall", artist: "Pink Floyd"),
        };

        QueueNumbering.Renumber(rows);

        Assert.Equal(["1", "2", "1"], rows.Select(r => r.Track));
    }

    [Fact]
    public void Reports_only_what_actually_moved()
    {
        var rows = new[] { Row("a", track: "1"), Row("b", track: "2") };

        Assert.Equal(0, QueueNumbering.Renumber(rows));
    }
}
