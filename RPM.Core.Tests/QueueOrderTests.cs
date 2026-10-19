using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class QueueOrderTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public QueueOrderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdorder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static QueueTrack Row(string fileName, string title, uint track)
    {
        var current = new TrackInfo
        {
            FilePath = $"/music/in/{fileName}",
            Extension = ".flac",
            DurationSeconds = 200,
            Artist = "Enya",
            AlbumArtist = "Enya",
            Album = "Watermark",
            Title = title,
            TrackNumber = track,
            Source = MetadataSource.Tags,
        };
        return new QueueTrack
        {
            Source = "scan",
            Folder = "in",
            FileName = fileName,
            Current = current,
            Wanted = current,
        };
    }

    private void Seed() => _db.QueueInsertMany(
    [
        Row("a.flac", "One", 1),
        Row("b.flac", "Two", 2),
        Row("c.flac", "Three", 3),
    ]);

    private List<string> Titles() =>
        _db.WholeQueue.GroupPage("Enya", "Watermark", 0, 50).Select(t => t.Wanted.Title!).ToList();

    private long IdOf(string title) =>
        _db.WholeQueue.GroupPage("Enya", "Watermark", 0, 50).First(t => t.Wanted.Title == title).Id;

    [Fact]
    public void An_untouched_queue_reads_in_track_order()
    {
        Seed();
        Assert.Equal(["One", "Two", "Three"], Titles());
    }

    [Fact]
    public void The_ticked_snapshot_reads_in_the_same_order_the_grid_does()
    {
        // This is what "number these 1 to n from the top" depends on. An action receives the ticked
        // rows as a snapshot from the store. If that snapshot came back in file-name order, it would
        // undo the arrangement the user has just dragged into place.
        Seed();
        _db.WholeQueue.CheckAll(true);
        _db.QueueReorder("Enya", "Watermark", [IdOf("Three")], afterId: null, beforeId: IdOf("One"));

        Assert.Equal(
            ["Three", "One", "Two"],
            _db.WholeQueue.CheckedRows().Select(t => t.Wanted.Title!));
    }

    [Fact]
    public void A_row_moved_to_the_front_leads()
    {
        Seed();
        _db.QueueReorder("Enya", "Watermark", [IdOf("Three")], afterId: null, beforeId: IdOf("One"));

        Assert.Equal(["Three", "One", "Two"], Titles());
    }

    [Fact]
    public void A_row_moved_to_the_end_trails()
    {
        Seed();
        _db.QueueReorder("Enya", "Watermark", [IdOf("One")], afterId: IdOf("Three"), beforeId: null);

        Assert.Equal(["Two", "Three", "One"], Titles());
    }

    [Fact]
    public void A_row_lands_between_the_two_it_was_dropped_between()
    {
        Seed();
        _db.QueueReorder("Enya", "Watermark", [IdOf("Three")],
                         afterId: IdOf("One"), beforeId: IdOf("Two"));

        Assert.Equal(["One", "Three", "Two"], Titles());
    }

    [Fact]
    public void Several_rows_keep_the_order_they_were_given_in()
    {
        Seed();
        _db.QueueReorder("Enya", "Watermark", [IdOf("Three"), IdOf("Two")],
                         afterId: null, beforeId: IdOf("One"));

        Assert.Equal(["Three", "Two", "One"], Titles());
    }

    [Fact]
    public void Reordering_does_not_touch_the_track_numbers()
    {
        Seed();
        _db.QueueReorder("Enya", "Watermark", [IdOf("Three")], afterId: null, beforeId: IdOf("One"));

        var page = _db.WholeQueue.GroupPage("Enya", "Watermark", 0, 50);
        Assert.Equal(3u, page[0].Wanted.TrackNumber);
        Assert.Equal(1u, page[1].Wanted.TrackNumber);
    }

    [Fact]
    public void Arranging_a_record_gives_every_row_in_it_an_order()
    {
        Seed();
        Assert.Equal(0, _db.QueueOrderOf(IdOf("One")));

        _db.QueueReorder("Enya", "Watermark", [IdOf("Two")], afterId: null, beforeId: IdOf("One"));

        Assert.True(_db.QueueOrderOf(IdOf("One")) > 0);
        Assert.True(_db.QueueOrderOf(IdOf("Three")) > 0);
    }

    [Fact]
    public void An_exhausted_gap_renormalises_and_the_order_survives()
    {
        Seed();
        // Squeeze the last row into the same gap again and again, until the midpoint has nowhere to go.
        for (var i = 0; i < 14; i++)
        {
            var titles = Titles();
            _db.QueueReorder("Enya", "Watermark", [IdOf(titles[^1])],
                             afterId: IdOf(titles[0]), beforeId: IdOf(titles[1]));
        }

        var final = Titles();
        Assert.Equal(3, final.Count);
        Assert.True(_db.QueueOrderOf(IdOf(final[0])) < _db.QueueOrderOf(IdOf(final[1])));
        Assert.True(_db.QueueOrderOf(IdOf(final[1])) < _db.QueueOrderOf(IdOf(final[2])));
    }

    [Fact]
    public void A_reordered_group_still_answers_where_a_row_is()
    {
        Seed();
        _db.QueueReorder("Enya", "Watermark", [IdOf("Three")], afterId: null, beforeId: IdOf("One"));

        Assert.Equal(0, _db.QueueRowOffsetInGroup(IdOf("Three")));
        Assert.Equal(2, _db.QueueRowOffsetInGroup(IdOf("Two")));
    }
}
