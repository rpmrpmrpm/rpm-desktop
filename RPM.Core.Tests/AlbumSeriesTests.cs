using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The series a record is one volume of: said in the album editor or in the import queue, kept in the
/// library, carried through a re-file and between the listener's own computers.
/// </summary>
public class AlbumSeriesTests : IDisposable
{
    private const string Folder = "Mozart/Die Zauberflöte";
    private static readonly AlbumSeries Mozart = new("The Complete Mozart Edition", "43");

    private readonly string _root;
    private readonly LibraryDatabase _db;

    public AlbumSeriesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmseries-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── the answer itself ──

    [Fact]
    public void A_series_with_no_name_is_no_series()
    {
        Assert.Null(AlbumSeries.Of("  ", "43"));
        Assert.Null(AlbumSeries.Of(null, null));
        Assert.Equal(new AlbumSeries("Edition", null), AlbumSeries.Of(" Edition ", " "));
    }

    [Fact]
    public void Volumes_sort_by_their_number_and_unnumbered_ones_last()
    {
        string?[] volumes = ["43", "IV", "9", "Vol. 10", null, "3a"];
        var sorted = volumes.Select(v => new AlbumSeries("S", v))
            .OrderBy(s => s.Order.Number).ThenBy(s => s.Order.Text)
            .Select(s => s.Volume);

        Assert.Equal(["3a", "9", "Vol. 10", "43", null, "IV"], sorted);
    }

    // ── the library ──

    [Fact]
    public void A_record_untouched_stands_alone()
        => Assert.Null(_db.GetAlbumMeta(Folder).InSeries);

    [Fact]
    public void A_series_said_is_read_back_and_can_be_taken_away()
    {
        _db.SetAlbumSeries(Folder, Mozart);
        Assert.Equal(Mozart, _db.GetAlbumMeta(Folder).InSeries);
        Assert.Equal(Mozart, _db.GetAlbumMetaMany([Folder])[Folder].InSeries);

        _db.SetAlbumSeries(Folder, null);
        Assert.Null(_db.GetAlbumMeta(Folder).InSeries);
    }

    /// <summary>The name is said once per record, and the editor offers the ones already used so
    /// the forty-fourth volume does not start a second series spelled differently.</summary>
    [Fact]
    public void The_library_offers_each_series_once_and_lists_its_volumes_in_order()
    {
        _db.SetAlbumSeries("Mozart/Requiem", new AlbumSeries("The Complete Mozart Edition", "9"));
        _db.SetAlbumSeries(Folder, Mozart);
        _db.SetAlbumSeries("Bach/Cantatas 1", new AlbumSeries("Bach 2000", "1"));
        _db.SetAlbumSeries("Mozart/Symphonies", new AlbumSeries("the complete mozart edition", "1"));

        Assert.Equal(2, _db.AlbumSeriesNames().Count);
        Assert.Equal(
            ["Mozart/Symphonies", "Mozart/Requiem", Folder],
            _db.AlbumsInSeries("The Complete Mozart Edition").Select(a => a.Folder));
    }

    [Fact]
    public void A_re_filed_record_keeps_its_series_and_leaves_none_behind()
    {
        _db.SetAlbumSeries(Folder, Mozart);

        _db.MoveAlbumMeta(Folder, "Wolfgang Amadeus Mozart/Die Zauberflöte");

        Assert.Equal(Mozart, _db.GetAlbumMeta("Wolfgang Amadeus Mozart/Die Zauberflöte").InSeries);
        Assert.Null(_db.GetAlbumMeta(Folder).InSeries);
        Assert.Equal(["Wolfgang Amadeus Mozart/Die Zauberflöte"],
            _db.AlbumsInSeries(Mozart.Name).Select(a => a.Folder));
    }

    // ── between the listener's own computers ──

    private static AlbumMetaEdit Arriving(AlbumSeries? series, long at, bool olderBuild = false)
    {
        var sent = PeerCatalogBuilder.From(
            [new AlbumMetaEdit(AlbumMeta.Default(Folder) with { Series = series?.Name, SeriesVolume = series?.Volume }, at)],
            PeerCatalogBuilder.AlbumAudience.TheOwner).Single();

        // A build from before series sends no such field, which a receiver reads as null.
        if (olderBuild)
            sent = sent with { Series = null, SeriesVolume = null };
        return PeerCatalogBuilder.To([sent]).Single();
    }

    [Fact]
    public void A_series_said_on_another_computer_arrives()
    {
        _db.ApplyAlbumMetaEdits([Arriving(Mozart, at: DateTime.UtcNow.Ticks)], ArrivingAlbumMarks.Whole);

        Assert.Equal(Mozart, _db.GetAlbumMeta(Folder).InSeries);
    }

    [Fact]
    public void A_series_taken_away_on_another_computer_is_taken_away_here()
    {
        _db.SetAlbumSeries(Folder, Mozart);

        _db.ApplyAlbumMetaEdits([Arriving(null, at: DateTime.UtcNow.Ticks + 1)], ArrivingAlbumMarks.Whole);

        Assert.Null(_db.GetAlbumMeta(Folder).InSeries);
    }

    /// <summary>A newer edit from a build that has never heard of series says nothing about one, and
    /// must not erase the one this library holds.</summary>
    [Fact]
    public void A_build_that_knows_no_series_does_not_erase_one()
    {
        _db.SetAlbumSeries(Folder, Mozart);

        _db.ApplyAlbumMetaEdits([Arriving(null, at: DateTime.UtcNow.Ticks + 1, olderBuild: true)], ArrivingAlbumMarks.Whole);

        Assert.Equal(Mozart, _db.GetAlbumMeta(Folder).InSeries);
    }

    // ── the import queue ──

    private static QueueTrack Row(string file)
    {
        var info = new TrackInfo
        {
            FilePath = $"/music/src/{file}",
            Extension = ".flac",
            Artist = "Mozart",
            AlbumArtist = "Mozart",
            Album = "Die Zauberflöte",
            Title = Path.GetFileNameWithoutExtension(file),
        };
        return new QueueTrack { Source = "/src", Folder = "src", FileName = file, Current = info, Wanted = info };
    }

    [Fact]
    public void A_series_said_in_the_queue_is_held_and_filed_with_the_record()
    {
        _db.QueueInsertMany([Row("01.flac"), Row("02.flac")]);
        _db.QueueSetAlbumIntent("Mozart", "Die Zauberflöte", new QueueAlbumIntent(SeriesSet: true, Series: Mozart));

        // A second visit that changes something else keeps the series from the first.
        _db.QueueSetAlbumIntent("Mozart", "Die Zauberflöte", new QueueAlbumIntent(ClassicalSet: true, Classical: true));
        var intent = _db.QueueAlbumIntentFor("Mozart", "Die Zauberflöte")!;
        Assert.True(intent.SeriesSet);
        Assert.Equal(Mozart, intent.Series);

        AlbumIntentApplier.Apply(_db, _root, Folder, intent);
        Assert.Equal(Mozart, _db.GetAlbumMeta(Folder).InSeries);
    }

    /// <summary>Saying "stands alone" in the queue is an answer, and it takes a record joining one that
    /// is already on the shelf out of the series. Saying nothing leaves the shelf's answer alone.</summary>
    [Fact]
    public void Only_a_series_that_was_set_is_written_at_import()
    {
        _db.SetAlbumSeries(Folder, Mozart);

        AlbumIntentApplier.Apply(_db, _root, Folder, new QueueAlbumIntent(ClassicalSet: true, Classical: true));
        Assert.Equal(Mozart, _db.GetAlbumMeta(Folder).InSeries);

        AlbumIntentApplier.Apply(_db, _root, Folder, new QueueAlbumIntent(SeriesSet: true, Series: null));
        Assert.Null(_db.GetAlbumMeta(Folder).InSeries);
    }
}
