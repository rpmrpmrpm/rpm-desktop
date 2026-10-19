using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class ImportQueueStoreTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public ImportQueueStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdqueue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Builds a queue row whose wanted side starts equal to current, like a fresh scan.
    private static QueueTrack Row(
        string source, string folder, string fileName,
        string? artist = null, string? title = null, string? album = null,
        MetadataSource src = MetadataSource.Tags, bool @checked = false, int splitHint = 0,
        string? albumArtist = null, uint track = 0, int? bitrate = null, int? bits = null,
        int? sampleRate = null, long size = 0, FileAvailability availability = FileAvailability.Local,
        uint disc = 0, uint discCount = 0)
    {
        var current = new TrackInfo
        {
            FilePath = $"/music/{folder}/{fileName}",
            Extension = Path.GetExtension(fileName),
            DurationSeconds = 200,
            Artist = artist,
            AlbumArtist = albumArtist,
            Title = title,
            Album = album,
            TrackNumber = track,
            DiscNumber = disc,
            DiscCount = discCount,
            BitrateKbps = bitrate,
            BitsPerSample = bits,
            SampleRate = sampleRate,
            Availability = availability,
            Source = src,
        };
        return new QueueTrack
        {
            Source = source,
            Folder = folder,
            FileName = fileName,
            IsChecked = @checked,
            SplitHint = splitHint,
            SizeBytes = size,
            Current = current,
            Wanted = current,
        };
    }

    // A track already on the shelf, for the duplicate pass to find.
    private static LibraryTrack Shelved(
        string fileName, string artist, string title, string? album = null,
        int? bitrate = null, int? bits = null, int? sampleRate = null) => new()
    {
        RelativePath = $"lib/{fileName}",
        Folder = "lib",
        FileName = fileName,
        Extension = Path.GetExtension(fileName),
        Artist = artist,
        Title = title,
        Album = album,
        BitrateKbps = bitrate,
        BitsPerSample = bits,
        SampleRate = sampleRate,
    };

    /// <summary>A track the collection lists but this computer has no file for. The account's
    /// catalogue adds rows like this to the mirror on a machine that has imported nothing yet.</summary>
    private static LibraryTrack CloudRecord(
        string fileName, string artist, string title, string? album = null) => new()
    {
        RelativePath = $"lib/{fileName}",
        Folder = "lib",
        FileName = fileName,
        Extension = Path.GetExtension(fileName),
        Artist = artist,
        Title = title,
        Album = album,
        CloudState = CloudState.CloudOnly,
    };

    [Fact]
    public void Insert_dedupes_by_path()
    {
        var added = _db.QueueInsertMany([
            Row("src", "src/A", "1.mp3", "Muse", "Hysteria"),
            Row("src", "src/A", "2.mp3", "Muse", "Stockholm Syndrome"),
        ]);
        Assert.Equal(2, added);

        // Same path as "1.mp3" (path derives from folder+fileName) → ignored, not duplicated.
        var again = _db.QueueInsertMany([Row("src", "src/A", "1.mp3", "Muse", "Hysteria")]);
        Assert.Equal(0, again);
        Assert.Equal(2, _db.QueueCount());
    }

    [Fact]
    public void Group_skeleton_is_the_metadata_not_the_folders()
    {
        // Two folders, one album: the group comes from the tags, not from the folder.
        _db.QueueInsertMany([
            Row("src", "src/A", "b.mp3", "Muse", "Bliss", "Origin of Symmetry", track: 2),
            Row("src", "src/B", "a.mp3", "Muse", "Plug In Baby", "Origin of Symmetry", track: 1),
            Row("src", "src/A", "c.mp3", "Muse", "Hysteria", "Absolution", track: 4),
        ]);

        Assert.Equal(
            [("Muse", "Absolution", 1), ("Muse", "Origin of Symmetry", 2)],
            _db.WholeQueue.GroupCounts());

        // An album's page is returned in track order, not in file-name order.
        var page = _db.WholeQueue.GroupPage("Muse", "Origin of Symmetry", offset: 0, limit: 10);
        Assert.Equal(["Plug In Baby", "Bliss"], page.Select(r => r.Wanted.Title));
    }

    [Fact]
    public void The_album_artist_decides_the_group_and_missing_tags_get_the_unknown_buckets()
    {
        _db.QueueInsertMany([
            // A compilation track: the ARTIST is the performer, the ALBUM ARTIST belongs to the album.
            Row("src", "src", "1.mp3", "Nina Simone", "Sinnerman", "Cafe del Mar", albumArtist: "Various Artists"),
            Row("src", "src", "2.mp3", "Muse", "Hysteria"),          // no album
            Row("src", "src", "3.mp3", null, "Track 03"),            // no artist either
        ]);

        Assert.Equal(
            [("", "", 1), ("Muse", "", 1), ("Various Artists", "Cafe del Mar", 1)],
            _db.WholeQueue.GroupCounts());

        // The empty keys are real keys: the tree pages them like any other group.
        Assert.Equal("Track 03", _db.WholeQueue.GroupPage("", "", 0, 10).Single().Wanted.Title);
        Assert.Equal("Hysteria", _db.WholeQueue.GroupPage("Muse", "", 0, 10).Single().Wanted.Title);
    }

    [Fact]
    public void A_second_disc_is_not_a_duplicate_of_the_first()
    {
        // A two-disc set whose discs share track titles (live sets, classical, any album with an
        // "Intro" on both discs). Without the disc in the identity, the whole second disc is marked as
        // a worse copy of the first, and the clean-up deletes it in one click.
        _db.QueueInsertMany([
            Row("src", "src/CD1", "1.flac", "X", "Intro", "Live", track: 1, disc: 1, discCount: 2),
            Row("src", "src/CD1", "2.flac", "X", "Encore", "Live", track: 2, disc: 1, discCount: 2),
            Row("src", "src/CD2", "1.flac", "X", "Intro", "Live", track: 1, disc: 2, discCount: 2),
            Row("src", "src/CD2", "2.flac", "X", "Encore", "Live", track: 2, disc: 2, discCount: 2),
        ]);

        _db.QueueRefreshDuplicates();
        Assert.All(_db.WholeQueue.GroupPage("X", "Live", 0, 50),
                   r => Assert.Equal(QueueDuplicate.None, r.Duplicate));
        Assert.Equal(0, _db.QueueRemoveDuplicates());
        Assert.Equal(4, _db.QueueCount());

        // Two copies of the SAME disc are still duplicates: the disc is added to the identity, it does
        // not replace it.
        _db.QueueInsertMany([
            Row("src", "src/copy", "1.flac", "X", "Intro", "Live", track: 1, disc: 2, discCount: 2),
        ]);
        _db.QueueRefreshDuplicates();
        Assert.Equal(1, _db.QueueRemoveDuplicates());
    }

    [Fact]
    public void A_groups_rows_come_back_in_the_order_the_records_play()
    {
        _db.QueueInsertMany([
            Row("src", "src/A", "z.mp3", "Muse", "Bliss", "Origin of Symmetry", track: 2),
            Row("src", "src/A", "a.mp3", "Muse", "Plug In Baby", "Origin of Symmetry", track: 1),
            Row("src", "src/B", "m.mp3", "Muse", "Hysteria", "Absolution", track: 4),
            Row("src", "src/C", "q.mp3", "Radiohead", "Creep", "Pablo Honey", track: 1),
        ]);

        // One album: track order, not file-name order.
        Assert.Equal(
            ["Plug In Baby", "Bliss"],
            _db.WholeQueue.RowsInGroup("Muse", "Origin of Symmetry").Select(r => r.Wanted.Title));

        // A null album means the whole artist: every album under it, each in track order.
        Assert.Equal(
            ["Hysteria", "Plug In Baby", "Bliss"],
            _db.WholeQueue.RowsInGroup("Muse", album: null).Select(r => r.Wanted.Title));
        Assert.Equal(3, _db.WholeQueue.GroupCheckState("Muse", album: null).Total);
    }

    [Fact]
    public void A_groups_placeholders_come_back_by_album_or_by_whole_artist()
    {
        _db.QueueInsertMany([
            Row("src", "src/A", "1.mp3", "Muse", "Bliss", "Origin of Symmetry",
                availability: FileAvailability.Dataless),
            Row("src", "src/A", "2.mp3", "Muse", "Plug In Baby", "Origin of Symmetry"),   // on the disk
            Row("src", "src/B", "3.mp3", "Muse", "Hysteria", "Absolution",
                availability: FileAvailability.Dataless),
            Row("src", "src/C", "4.mp3", "Radiohead", "Creep", "Pablo Honey",
                availability: FileAvailability.Dataless),
        ]);

        // One album: only its own placeholder, not the file that is already on disk.
        var album = _db.WholeQueue.DatalessRowsInGroup("Muse", "Origin of Symmetry");
        Assert.Equal(["1.mp3"], album.Select(r => r.FileName));
        Assert.Equal(1, _db.QueueDatalessCountInGroup("Muse", "Origin of Symmetry"));

        // A null album means the whole artist, all albums, album by album, and only that artist.
        var artist = _db.WholeQueue.DatalessRowsInGroup("Muse", album: null);
        Assert.Equal(["3.mp3", "1.mp3"], artist.Select(r => r.FileName));
        Assert.Equal(2, _db.QueueDatalessCountInGroup("Muse", album: null));

        // A group with no placeholders returns zero, not the count for the whole queue.
        Assert.Equal(0, _db.QueueDatalessCountInGroup("Nobody", album: null));
    }

    [Fact]
    public void Fts_search_matches_word_start_only()
    {
        _db.QueueInsertMany([
            Row("src", "src", "1.mp3", "The Beatles", "Yesterday"),
            Row("src", "src", "2.mp3", "Beach Boys", "Surfin"),
        ]);

        // "bea" is the START of a word in both "Beatles" and "Beach".
        Assert.Equal(2, _db.Queue(new QueueFilter { Search = "bea" }).GroupCounts().Sum(c => c.TrackCount));

        // "beatl" only begins a word in the first row.
        var beatl = _db.Queue(new QueueFilter { Search = "beatl" }).GroupPage("The Beatles", "", 0, 10);
        Assert.Single(beatl);
        Assert.Equal("1.mp3", beatl[0].FileName);

        // "les" is the END of "Beatles", not a word start → no match (that's the point of an indexable search).
        Assert.Empty(_db.Queue(new QueueFilter { Search = "les" }).GroupPage("The Beatles", "", 0, 10));

        // Two words both AND-ed, each as a prefix.
        Assert.Single(_db.Queue(new QueueFilter { Search = "bea yes" }).GroupPage("The Beatles", "", 0, 10));
    }

    [Fact]
    public void Aggregates_count_checked_needs_recognition_and_importable()
    {
        _db.QueueInsertMany([
            Row("src", "src", "1.mp3", "Muse", "Hysteria", src: MetadataSource.Tags, @checked: true),   // recognized + ticked → importable
            Row("src", "src", "2.mp3", null, null, src: MetadataSource.Unknown, @checked: true),          // needs recognition, ticked → NOT importable
            Row("src", "src", "3.mp3", "Muse", "Starlight", src: MetadataSource.FilenameGuess),           // filename guess → needs recognition, unticked
            Row("src", "src", "4.mp3", "Muse", "Bliss", src: MetadataSource.Tags),                        // recognized, unticked
        ]);

        Assert.Equal(4, _db.QueueCount());
        Assert.Equal(2, _db.QueueCheckedCount());
        Assert.Equal(2, _db.QueueNeedsRecognitionCount()); // rows 2 (unknown) and 3 (filename guess)
        Assert.Equal(1, _db.QueueImportableCount());        // only row 1
    }

    /// <summary>
    /// The tick decides what is imported, not whether a release matched the track. A bonus track
    /// that the chosen release does not list keeps its names guessed from the file name and is still
    /// imported. The release names what it can; the rest is imported with guessed names rather than
    /// left behind.
    /// </summary>
    [Fact]
    public void A_ticked_row_the_release_did_not_name_is_still_imported()
    {
        _db.QueueInsertMany([
            Row("src", "src/Album", "01 - Rubber Universe.mp3", "Alan Parsons", "Rubber Universe", @checked: true),
            Row("src", "src/Album", "13 - Dr Evil.mp3", "Alan Parsons", "Dr Evil",
                src: MetadataSource.FilenameGuess, @checked: true),
        ]);

        // It still counts as needing recognition...
        Assert.Equal(1, _db.QueueNeedsRecognitionCount());

        // ...and it is still imported.
        Assert.Equal(2, _db.QueueImportableCount());
        // Sorted here because the test checks WHICH rows. The store returns them in grid order
        // (title, when nothing else separates two rows), not in file-name order.
        Assert.Equal(
            ["01 - Rubber Universe.mp3", "13 - Dr Evil.mp3"],
            _db.WholeQueue.ImportableRows().Select(r => r.FileName).OrderBy(f => f));
    }

    /// <summary>The only metadata reason a ticked row is not imported: there is nothing to build a
    /// path from.</summary>
    [Fact]
    public void A_ticked_row_with_no_artist_or_title_has_nowhere_to_be_filed()
    {
        _db.QueueInsertMany([
            Row("src", "src", "1.mp3", "Muse", null, src: MetadataSource.FilenameGuess, @checked: true),
            Row("src", "src", "2.mp3", null, "Hysteria", src: MetadataSource.FilenameGuess, @checked: true),
            Row("src", "src", "3.mp3", "  ", "  ", src: MetadataSource.FilenameGuess, @checked: true),
        ]);

        Assert.Equal(3, _db.QueueCheckedCount());
        Assert.Equal(0, _db.QueueImportableCount());
    }

    [Fact]
    public void Update_wanted_writes_back_and_reflects_in_aggregates_and_search()
    {
        _db.QueueInsertMany([Row("src", "src", "1.mp3", null, null, src: MetadataSource.Unknown, @checked: true)]);
        Assert.Equal(1, _db.QueueNeedsRecognitionCount());
        Assert.Equal(0, _db.QueueImportableCount());

        var row = _db.WholeQueue.GroupPage("", "", 0, 10).Single();
        var wanted = row.Wanted with { Artist = "Radiohead", Title = "Creep", Source = MetadataSource.Fingerprint };
        _db.QueueUpdateWanted(row.Id, row.IsChecked, wanted);

        // Now recognized → drops out of needs-recognition, becomes importable (still ticked).
        Assert.Equal(0, _db.QueueNeedsRecognitionCount());
        Assert.Equal(1, _db.QueueImportableCount());

        // The write-back was saved, the row moved to its new artist, and the FTS text was updated to
        // the new wanted values.
        var reread = _db.WholeQueue.GroupPage("Radiohead", "", 0, 10).Single();
        Assert.Equal("Radiohead", reread.Wanted.Artist);
        Assert.Equal("Creep", reread.Wanted.Title);
        Assert.Equal(MetadataSource.Fingerprint, reread.Wanted.Source);
        Assert.Single(_db.Queue(new QueueFilter { Search = "radio" }).GroupPage("Radiohead", "", 0, 10));
    }

    [Fact]
    public void Checked_and_importable_row_sets_and_distinct_sources()
    {
        _db.QueueInsertMany([
            Row("/music/rip", "rip/A", "1.mp3", "Muse", "Hysteria", @checked: true),                 // importable
            Row("/music/rip", "rip/A", "2.mp3", null, null, src: MetadataSource.Unknown, @checked: true), // checked, not importable
            Row("/other", "other", "3.mp3", "Muse", "Bliss"),                                          // unticked
        ]);

        Assert.Equal(2, _db.WholeQueue.CheckedRows().Count);
        var importable = _db.WholeQueue.ImportableRows();
        Assert.Single(importable);
        Assert.Equal("1.mp3", importable[0].FileName);

        Assert.Equal(["/music/rip", "/other"], _db.QueueDistinctSources());
    }

    [Fact]
    public void The_album_a_split_preserved_is_listed_but_never_imported()
    {
        _db.QueueInsertMany([
            Row("src", "src/Album", "01 - Track.flac", "Muse", "Hysteria", @checked: true),
            // Fully tagged, ticked, and still not importable: it is the whole album in one file.
            Row("src", "src/Album/Originals", "side a.flac", "Muse", "Absolution", @checked: true),
            Row("src", "src/Album/_originals", "old side.flac", "Muse", "Absolution", @checked: true),
        ]);

        Assert.Equal(3, _db.QueueCount());          // all three are in the tree
        Assert.Equal(3, _db.QueueCheckedCount());

        var importable = _db.WholeQueue.ImportableRows();
        Assert.Single(importable);
        Assert.Equal("01 - Track.flac", importable[0].FileName);
        Assert.Equal(1, _db.QueueImportableCount());
    }

    [Fact]
    public void A_preserved_album_does_not_nag_for_recognition()
    {
        _db.QueueInsertMany([
            Row("src", "src/Album/Originals", "side a.flac", src: MetadataSource.Unknown),
            Row("src", "src/Album", "01 - Track.flac", src: MetadataSource.Unknown),
        ]);

        Assert.Equal(1, _db.QueueNeedsRecognitionCount());
    }

    [Fact]
    public void A_preserved_album_is_not_ticked_by_the_record_it_was_cut_out_of()
    {
        _db.QueueInsertMany([
            Row("src", "src/Album", "01 - Track.flac", "Muse", "Hysteria", "Absolution"),
            Row("src", "src/Album", "02 - Other.flac", "Muse", "Bliss", "Absolution"),
            // In the same album group, because the rip put one track's tags on the whole album file.
            Row("src", "src/Album/Originals", "album.flac", "Muse", "Bliss", "Absolution"),
        ]);

        _db.WholeQueue.SetCheckedInGroup("Muse", "Absolution", true);

        Assert.Equal(2, _db.QueueCheckedCount());
        Assert.Equal(2, _db.QueueImportableCount());
        // The album's checkbox shows FULL, not partial: it counts only rows that can be imported.
        Assert.Equal((2, 2), _db.WholeQueue.GroupCheckState("Muse", "Absolution"));
        Assert.Equal(3, _db.QueueCount());        // and the album file is still listed
    }

    [Fact]
    public void Select_all_leaves_a_preserved_album_alone()
    {
        _db.QueueInsertMany([
            Row("src", "src/Album", "01 - Track.flac", "Muse", "Hysteria", "Absolution"),
            Row("src", "src/Album/Originals", "album.flac", "Muse", "Hysteria", "Absolution"),
            Row("src", "src/Album/_originals", "old album.flac", "Muse", "Hysteria", "Absolution"),
        ]);

        _db.WholeQueue.CheckAll(true);

        Assert.Equal(1, _db.QueueCheckedCount());
        Assert.Equal(1, _db.QueueImportableCount());

        // Ticking the row itself has no effect either.
        var preserved = _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10)
            .Single(r => r.FileName == "album.flac");
        _db.QueueSetChecked(preserved.Id, true);
        Assert.Equal(1, _db.QueueCheckedCount());
    }

    [Fact]
    public void A_preserved_album_is_never_a_duplicate_and_never_makes_one()
    {
        _db.QueueInsertMany([
            // The split track, and the forty-minute file it was split from, with the same tags.
            Row("src", "src/Album", "02 - Bliss.flac", "Muse", "Bliss", "Absolution",
                bits: 16, sampleRate: 44100, size: 25_000_000),
            Row("src", "src/Album/Originals", "album.flac", "Muse", "Bliss", "Absolution",
                bits: 16, sampleRate: 44100, size: 270_000_000),
        ]);

        _db.QueueRefreshDuplicates();

        var byFile = _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10)
            .ToDictionary(r => r.FileName, r => r.Duplicate);
        // The album file is not a copy of the track: the track was split from it.
        Assert.Equal(QueueDuplicate.None, byFile["album.flac"]);
        // The track is not a worse copy of the album file either, though its size suggests it.
        Assert.Equal(QueueDuplicate.None, byFile["02 - Bliss.flac"]);
        Assert.Equal(0, _db.QueueRemoveDuplicates());
    }

    [Fact]
    public void A_preserved_album_survives_the_shelf_holding_the_track_it_names()
    {
        _db.UpsertMany([Shelved("bliss.flac", "Muse", "Bliss", "Absolution", bits: 16, sampleRate: 44100)]);
        _db.QueueInsertMany([
            Row("src", "src/Album/Originals", "album.flac", "Muse", "Bliss", "Absolution",
                bits: 16, sampleRate: 44100, size: 270_000_000),
        ]);

        _db.QueueRefreshDuplicates();

        Assert.Equal(QueueDuplicate.None,
            _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10).Single().Duplicate);
        // A bad split is undone from this file, so a clean-up must not remove it.
        Assert.Equal(0, _db.QueueRemoveDuplicates());
    }

    [Fact]
    public void Ticking_a_group_ticks_that_group_only()
    {
        _db.QueueInsertMany([
            Row("src", "src", "1.mp3", "Muse", "one", "Absolution"),
            Row("src", "src", "2.mp3", "Muse", "two", "Absolution"),
            Row("src", "src", "3.mp3", "Muse", "three", "Black Holes"),
            Row("src", "src", "4.mp3", "Blur", "four", "Parklife"),
        ]);

        // One album.
        _db.WholeQueue.SetCheckedInGroup("Muse", "Absolution", true);
        Assert.Equal(2, _db.QueueCheckedCount());
        Assert.Equal((2, 2), _db.WholeQueue.GroupCheckState("Muse", "Absolution"));
        // The artist above it is only partly ticked: two of its three.
        Assert.Equal((2, 3), _db.WholeQueue.GroupCheckState("Muse", null));
        Assert.Equal((0, 1), _db.WholeQueue.GroupCheckState("Blur", null));

        // A null album is the whole artist.
        _db.WholeQueue.SetCheckedInGroup("Muse", null, true);
        Assert.Equal(3, _db.QueueCheckedCount());

        _db.WholeQueue.SetCheckedInGroup("Muse", null, false);
        Assert.Equal(0, _db.QueueCheckedCount());
    }

    [Fact]
    public void One_artist_spelled_two_ways_is_one_group()
    {
        _db.QueueInsertMany([
            Row("src", "src", "1.mp3", "Muse", "one", "Absolution"),
            Row("src", "src", "2.mp3", "MUSE", "two", "absolution"),
        ]);

        var counts = _db.WholeQueue.GroupCounts();
        Assert.Single(counts);
        Assert.Equal(2, counts[0].TrackCount);
        Assert.Equal(2, _db.WholeQueue.GroupPage(counts[0].Artist, counts[0].Album, 0, 10).Count);
    }

    [Fact]
    public void Remove_and_clear()
    {
        _db.QueueInsertMany([
            Row("src", "src", "1.mp3", "A", "one"),
            Row("src", "src", "2.mp3", "B", "two"),
        ]);
        var rows = _db.WholeQueue.GroupPage("A", "", 0, 10);

        _db.QueueRemove([rows[0].Id]);
        Assert.Equal(1, _db.QueueCount());
        Assert.Empty(_db.Queue(new QueueFilter { Search = "one" }).GroupPage("A", "", 0, 10)); // its FTS entry went too

        _db.QueueClear();
        Assert.Equal(0, _db.QueueCount());
    }

    // ── duplicates: against the shelf, and against the rest of the queue ───────────────────────────

    [Fact]
    public void A_track_the_library_already_holds_is_marked_a_duplicate()
    {
        _db.UpsertMany([Shelved("hysteria.flac", "Muse", "Hysteria", "Absolution", bits: 16, sampleRate: 44100)]);
        _db.QueueInsertMany([Row("src", "src", "1.mp3", "Muse", "Hysteria", "Absolution", bitrate: 320)]);

        _db.QueueRefreshDuplicates();

        // A FLAC on the shelf ranks above any MP3, whatever its bitrate.
        Assert.Equal(QueueDuplicate.InLibrary, _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10).Single().Duplicate);
    }

    [Fact]
    public void The_better_encode_of_a_shelved_track_is_an_upgrade()
    {
        _db.UpsertMany([Shelved("hysteria.mp3", "Muse", "Hysteria", "Absolution", bitrate: 128)]);
        _db.QueueInsertMany([
            Row("src", "src", "better.flac", "Muse", "Hysteria", "Absolution", bits: 16, sampleRate: 44100),
        ]);

        _db.QueueRefreshDuplicates();

        Assert.Equal(QueueDuplicate.Upgrade, _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10).Single().Duplicate);
    }

    [Fact]
    public void Two_copies_of_one_track_in_the_queue_leave_the_best_alone()
    {
        _db.QueueInsertMany([
            Row("src", "a", "hysteria.mp3", "Muse", "Hysteria", "Absolution", bitrate: 192, size: 5_000_000),
            Row("src", "b", "hysteria.flac", "Muse", "Hysteria", "Absolution",
                bits: 16, sampleRate: 44100, size: 30_000_000),
            Row("src", "c", "hysteria-2.mp3", "Muse", "Hysteria", "Absolution", bitrate: 320, size: 9_000_000),
        ]);

        _db.QueueRefreshDuplicates();

        var byFile = _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10)
            .ToDictionary(r => r.FileName, r => r.Duplicate);
        Assert.Equal(QueueDuplicate.None, byFile["hysteria.flac"]);   // the keeper
        Assert.Equal(QueueDuplicate.InQueue, byFile["hysteria-2.mp3"]);
        Assert.Equal(QueueDuplicate.InQueue, byFile["hysteria.mp3"]);
    }

    [Fact]
    public void A_shelved_copy_outranks_being_the_best_in_the_queue()
    {
        // The shelf has it in FLAC; the queue has two MP3s of it. Neither is worth importing, and the
        // better MP3 must not be marked as the one to keep just because it ranks above the other.
        _db.UpsertMany([Shelved("hysteria.flac", "Muse", "Hysteria", "Absolution", bits: 16, sampleRate: 44100)]);
        _db.QueueInsertMany([
            Row("src", "a", "one.mp3", "Muse", "Hysteria", "Absolution", bitrate: 320),
            Row("src", "b", "two.mp3", "Muse", "Hysteria", "Absolution", bitrate: 128),
        ]);

        _db.QueueRefreshDuplicates();

        var byFile = _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10)
            .ToDictionary(r => r.FileName, r => r.Duplicate);
        Assert.Equal(QueueDuplicate.InLibrary, byFile["one.mp3"]);
        Assert.Equal(QueueDuplicate.InLibrary, byFile["two.mp3"]);
    }

    // ── duplicates: a cloud record is not a copy ──────────────────────────────────────────────────

    [Fact]
    public void A_track_the_collection_only_lists_in_the_cloud_is_not_a_duplicate()
    {
        // Signing in fills the mirror from the account's catalogue before anything is imported. Those
        // rows have no file here, so the queued file is not a copy of anything: importing it is the
        // only way this computer gets a file.
        _db.UpsertMany([CloudRecord("hysteria.flac", "Muse", "Hysteria", "Absolution")]);
        _db.QueueInsertMany([Row("src", "src", "1.mp3", "Muse", "Hysteria", "Absolution", bitrate: 320)]);

        _db.QueueRefreshDuplicates();

        Assert.Equal(QueueDuplicate.InCloud,
                     _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10).Single().Duplicate);
    }

    [Fact]
    public void Removing_duplicates_leaves_the_cloud_records_alone()
    {
        _db.UpsertMany([CloudRecord("hysteria.flac", "Muse", "Hysteria", "Absolution")]);
        _db.QueueInsertMany([Row("src", "src", "1.mp3", "Muse", "Hysteria", "Absolution", bitrate: 320)]);

        Assert.Equal(0, _db.QueueRemoveDuplicates());
        Assert.Equal(0, _db.QueueDuplicateCount());
        Assert.Equal(1, _db.QueueCount());
    }

    [Fact]
    public void The_cloud_mark_is_counted_apart_from_the_duplicates()
    {
        _db.UpsertMany([
            CloudRecord("hysteria.flac", "Muse", "Hysteria", "Absolution"),
            Shelved("stockholm.flac", "Muse", "Stockholm Syndrome", "Absolution",
                    bits: 16, sampleRate: 44100),
        ]);
        _db.QueueInsertMany([
            Row("src", "src", "1.mp3", "Muse", "Hysteria", "Absolution", bitrate: 320),
            Row("src", "src", "2.mp3", "Muse", "Stockholm Syndrome", "Absolution", bitrate: 320),
        ]);

        _db.QueueRefreshDuplicates();

        var counts = _db.QueueChipCounts();
        Assert.Equal(1, counts.InCloud);
        Assert.Equal(1, counts.Duplicates);
    }

    [Fact]
    public void A_second_copy_of_a_cloud_record_is_still_the_queue_duplicate_it_is()
    {
        // Two local copies of one track the account has in the cloud. The better one is the file to
        // import; the other is redundant whatever the cloud has, so the clean-up can still remove it.
        _db.UpsertMany([CloudRecord("hysteria.flac", "Muse", "Hysteria", "Absolution")]);
        _db.QueueInsertMany([
            Row("src", "a", "good.mp3", "Muse", "Hysteria", "Absolution", bitrate: 320, size: 9_000_000),
            Row("src", "b", "poor.mp3", "Muse", "Hysteria", "Absolution", bitrate: 128, size: 4_000_000),
        ]);

        _db.QueueRefreshDuplicates();

        var byFile = _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10)
            .ToDictionary(r => r.FileName, r => r.Duplicate);
        Assert.Equal(QueueDuplicate.InCloud, byFile["good.mp3"]);
        Assert.Equal(QueueDuplicate.InQueue, byFile["poor.mp3"]);

        Assert.Equal(1, _db.QueueRemoveDuplicates());
    }

    [Fact]
    public void A_file_on_this_disk_still_outranks_the_same_track_in_the_cloud()
    {
        // Both rows describe the track. The row with a local file makes the queued copy a duplicate,
        // and the cloud row must not change that result.
        _db.UpsertMany([
            CloudRecord("hysteria-cloud.flac", "Muse", "Hysteria", "Absolution"),
            Shelved("hysteria.flac", "Muse", "Hysteria", "Absolution", bits: 16, sampleRate: 44100),
        ]);
        _db.QueueInsertMany([Row("src", "src", "1.mp3", "Muse", "Hysteria", "Absolution", bitrate: 320)]);

        _db.QueueRefreshDuplicates();

        Assert.Equal(QueueDuplicate.InLibrary,
                     _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10).Single().Duplicate);
    }

    [Fact]
    public void Rows_nobody_has_named_are_not_duplicates_of_each_other()
    {
        _db.QueueInsertMany([
            Row("src", "src", "1.mp3", null, null),
            Row("src", "src", "2.mp3", null, null),
            Row("src", "src", "3.mp3", "Muse", null),   // artist but no title: still not an identity
        ]);

        _db.QueueRefreshDuplicates();

        Assert.All(_db.WholeQueue.GroupPage("", "", 0, 10), r => Assert.Equal(QueueDuplicate.None, r.Duplicate));
        Assert.All(_db.WholeQueue.GroupPage("Muse", "", 0, 10), r => Assert.Equal(QueueDuplicate.None, r.Duplicate));
    }

    [Fact]
    public void The_duplicate_pass_answers_to_the_wanted_side()
    {
        _db.UpsertMany([Shelved("creep.flac", "Radiohead", "Creep", "Pablo Honey", bits: 16, sampleRate: 44100)]);
        _db.QueueInsertMany([Row("src", "src", "1.mp3", null, null, src: MetadataSource.Unknown)]);

        _db.QueueRefreshDuplicates();
        Assert.Equal(QueueDuplicate.None, _db.WholeQueue.GroupPage("", "", 0, 10).Single().Duplicate);

        // Recognition names it, and it turns out to be something already on the shelf.
        var row = _db.WholeQueue.GroupPage("", "", 0, 10).Single();
        _db.QueueUpdateWanted(row.Id, row.IsChecked,
            row.Wanted with { Artist = "Radiohead", Title = "Creep", Album = "Pablo Honey" });
        _db.QueueRefreshDuplicates();

        Assert.Equal(QueueDuplicate.InLibrary,
            _db.WholeQueue.GroupPage("Radiohead", "Pablo Honey", 0, 10).Single().Duplicate);
    }

    // ── files that cannot be imported: in the cloud only, or local and broken ─────────────────────

    [Fact]
    public void A_cloud_placeholder_is_listed_but_never_imported()
    {
        _db.QueueInsertMany([
            Row("src", "src", "1.mp3", "Muse", "Hysteria", "Absolution", @checked: true),
            // Fully tagged and ticked, but its bytes are not on this disk. Importing it would either
            // add an empty placeholder to the shelf or start a download nobody asked for.
            Row("src", "src", "2.mp3", "Muse", "Bliss", "Absolution", @checked: true,
                availability: FileAvailability.Dataless),
        ]);

        Assert.Equal(2, _db.QueueCount());       // both are listed in the tree
        Assert.Equal(2, _db.QueueCheckedCount());

        var importable = _db.WholeQueue.ImportableRows();
        Assert.Single(importable);
        Assert.Equal("1.mp3", importable[0].FileName);
        Assert.Equal(1, _db.QueueImportableCount());
    }

    [Fact]
    public void An_unreadable_file_is_listed_but_never_imported()
    {
        _db.QueueInsertMany([
            Row("src", "src", "good.mp3", "Muse", "Hysteria", "Absolution", @checked: true),
            Row("src", "src", "broken.mp3", "Muse", "Bliss", "Absolution", @checked: true,
                availability: FileAvailability.Unreadable),
        ]);

        var importable = _db.WholeQueue.ImportableRows();
        Assert.Single(importable);
        Assert.Equal("good.mp3", importable[0].FileName);
    }

    [Fact]
    public void A_file_nobody_can_read_does_not_nag_for_recognition()
    {
        // Recognition needs the audio, so it cannot work on a file that is not on disk or is broken.
        // Such files are not counted as needing recognition.
        _db.QueueInsertMany([
            Row("src", "src", "away.mp3", src: MetadataSource.Unknown, availability: FileAvailability.Dataless),
            Row("src", "src", "broken.mp3", src: MetadataSource.Unknown, availability: FileAvailability.Unreadable),
            Row("src", "src", "here.mp3", src: MetadataSource.Unknown),
        ]);

        Assert.Equal(1, _db.QueueNeedsRecognitionCount());
    }

    [Fact]
    public void Availability_round_trips_through_the_store()
    {
        _db.QueueInsertMany([
            Row("src", "src", "away.mp3", "Muse", "Hysteria", availability: FileAvailability.Dataless),
        ]);

        var row = _db.WholeQueue.GroupPage("Muse", "", 0, 10).Single();
        Assert.Equal(FileAvailability.Dataless, row.Current.Availability);
    }

    // ── the same FILE twice, which the metadata key cannot detect ─────────────────────────────────

    [Fact]
    public void The_same_untagged_file_in_two_folders_is_a_duplicate()
    {
        // A collection imported into a music app long ago, then imported again from the original
        // copy. Neither file has an artist or a title, so neither has a metadata identity. But they are
        // the same file, and the name, the size and the length all match.
        _db.QueueInsertMany([
            Row("src", "Music", "01 All Around The World.mp3", size: 9_431_552),
            Row("src", "Music/Media.localized/Music/Unknown Artist/Unknown Album",
                "01 All Around The World.mp3", size: 9_431_552),
        ]);

        _db.QueueRefreshDuplicates();

        var states = _db.WholeQueue.GroupPage("", "", 0, 10).Select(r => r.Duplicate).ToList();
        Assert.Equal(2, states.Count);
        Assert.Single(states, QueueDuplicate.None);       // one keeper
        Assert.Single(states, QueueDuplicate.InQueue);    // one duplicate of it
    }

    [Fact]
    public void Two_different_files_that_share_a_name_are_not_duplicates()
    {
        // "01 Intro.mp3" is a very common file name. A shared name means something only together with
        // the same byte count and length.
        _db.QueueInsertMany([
            Row("src", "A", "01 Intro.mp3", size: 1_000_000),
            Row("src", "B", "01 Intro.mp3", size: 2_500_000),
        ]);

        _db.QueueRefreshDuplicates();

        Assert.All(_db.WholeQueue.GroupPage("", "", 0, 10), r => Assert.Equal(QueueDuplicate.None, r.Duplicate));
    }

    [Fact]
    public void A_file_of_unknown_size_is_never_matched_on_its_name_alone()
    {
        _db.QueueInsertMany([
            Row("src", "A", "01 Intro.mp3"),
            Row("src", "B", "01 Intro.mp3"),
        ]);

        _db.QueueRefreshDuplicates();

        Assert.All(_db.WholeQueue.GroupPage("", "", 0, 10), r => Assert.Equal(QueueDuplicate.None, r.Duplicate));
    }

    [Fact]
    public void A_title_and_the_record_it_is_on_identify_a_track_without_the_artist_field()
    {
        // Two files of one track, with different names and sizes, so only the tags can match them.
        // The artist tag is empty on both, so title and album alone must be enough.
        _db.QueueInsertMany([
            Row("src", "A", "track01.mp3", title: "Hysteria", album: "Absolution", size: 5_000_000),
            Row("src", "B", "01 hysteria.flac", title: "Hysteria", album: "Absolution",
                bits: 16, sampleRate: 44100, size: 30_000_000),
        ]);

        _db.QueueRefreshDuplicates();

        var byFile = _db.WholeQueue.GroupPage("", "Absolution", 0, 10)
            .ToDictionary(r => r.FileName, r => r.Duplicate);
        Assert.Equal(QueueDuplicate.None, byFile["01 hysteria.flac"]);   // the FLAC is kept
        Assert.Equal(QueueDuplicate.InQueue, byFile["track01.mp3"]);
    }

    [Fact]
    public void A_bare_title_with_no_record_behind_it_still_identifies_nothing()
    {
        // "Intro" alone is not an identity. Different files, different sizes: nothing shows a match.
        _db.QueueInsertMany([
            Row("src", "A", "a.mp3", title: "Intro", size: 1_000_000),
            Row("src", "B", "b.mp3", title: "Intro", size: 2_000_000),
        ]);

        _db.QueueRefreshDuplicates();

        Assert.All(_db.WholeQueue.GroupPage("", "", 0, 10), r => Assert.Equal(QueueDuplicate.None, r.Duplicate));
    }

    // ── the batch clean-ups offered in the chin ───────────────────────────────────────────────────

    [Fact]
    public void Removing_duplicates_keeps_the_best_copy_of_each_track()
    {
        _db.QueueInsertMany([
            Row("src", "a", "hysteria.mp3", "Muse", "Hysteria", "Absolution", bitrate: 192, size: 5_000_000),
            Row("src", "b", "hysteria.flac", "Muse", "Hysteria", "Absolution",
                bits: 16, sampleRate: 44100, size: 30_000_000),
            Row("src", "c", "hysteria-2.mp3", "Muse", "Hysteria", "Absolution", bitrate: 320, size: 9_000_000),
            Row("src", "d", "bliss.mp3", "Muse", "Bliss", "Origin", bitrate: 320, size: 9_000_000),
        ]);

        _db.QueueRefreshDuplicates();               // the marks the grid would show
        Assert.Equal(2, _db.QueueDuplicateCount());
        Assert.Equal(2, _db.QueueRemoveDuplicates());

        var left = _db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10);
        Assert.Equal("hysteria.flac", Assert.Single(left).FileName);   // the best one, and only it
        Assert.Single(_db.WholeQueue.GroupPage("Muse", "Origin", 0, 10));    // kept: not a duplicate
    }

    [Fact]
    public void Removing_duplicates_drops_what_the_library_already_has_but_keeps_the_upgrades()
    {
        _db.UpsertMany([
            Shelved("hysteria.flac", "Muse", "Hysteria", "Absolution", bits: 16, sampleRate: 44100),
            Shelved("bliss.mp3", "Muse", "Bliss", "Origin", bitrate: 128),
        ]);
        _db.QueueInsertMany([
            // The shelf has a FLAC of this: no gain from importing.
            Row("src", "a", "hysteria.mp3", "Muse", "Hysteria", "Absolution", bitrate: 320),
            // The shelf has a 128k MP3 of this: an upgrade, so it is kept.
            Row("src", "b", "bliss.flac", "Muse", "Bliss", "Origin", bits: 16, sampleRate: 44100),
        ]);

        Assert.Equal(1, _db.QueueRemoveDuplicates());

        var left = _db.WholeQueue.GroupPage("Muse", "Origin", 0, 10);
        Assert.Equal("bliss.flac", Assert.Single(left).FileName);
        Assert.Empty(_db.WholeQueue.GroupPage("Muse", "Absolution", 0, 10));
    }

    [Fact]
    public void Removing_duplicates_takes_their_search_entries_with_them()
    {
        _db.QueueInsertMany([
            Row("src", "a", "hysteria.mp3", "Muse", "Hysteria", "Absolution", bitrate: 192, size: 5_000_000),
            Row("src", "b", "hysteria.flac", "Muse", "Hysteria", "Absolution",
                bits: 16, sampleRate: 44100, size: 30_000_000),
        ]);

        _db.QueueRemoveDuplicates();

        // One row left, so one hit. A stale FTS entry would match a row that was removed.
        Assert.Single(_db.Queue(new QueueFilter { Search = "hysteria" }).GroupPage("Muse", "Absolution", 0, 10));
    }

    [Fact]
    public void Removing_unreadable_files_leaves_the_cloud_ones_alone()
    {
        _db.QueueInsertMany([
            Row("src", "src", "good.mp3", "Muse", "Hysteria"),
            Row("src", "src", "broken.mp3", "Muse", "Bliss", availability: FileAvailability.Unreadable),
            // Not on disk, but not broken. There is a separate action to download it, so this
            // clean-up must not remove it.
            Row("src", "src", "away.mp3", "Muse", "Starlight", availability: FileAvailability.Dataless),
        ]);

        Assert.Equal(1, _db.QueueUnreadableCount());
        Assert.Equal(1, _db.QueueRemoveUnreadable());

        Assert.Equal(
            ["away.mp3", "good.mp3"],
            _db.WholeQueue.GroupPage("Muse", "", 0, 10).Select(r => r.FileName).OrderBy(f => f));
    }

    // ── what a write-back says about itself ──────────────────────────────────────────────────────
    //
    // QueueUpdateWanted returns whether the row MOVED: a different group, or a different answer to
    // "is this the same recording as that one". The import pane uses that result to choose between
    // re-reading the pages on screen and rebuilding the whole tree. A rebuild loses the user's scroll
    // position, their open folders and the selected row. A false negative leaves a row shown under
    // an artist it does not belong to any more.

    private long SeedOne(string artist, string album, string title, string file = "a.flac", uint disc = 1)
    {
        _db.QueueInsertMany([Row("src", "f", file, artist: artist, title: title, album: album, disc: disc)]);
        return _db.WholeQueue.RowsInGroup(artist, album).Single(r => r.FileName == file).Id;
    }

    private static TrackInfo Wanted(
        string artist, string album, string title, uint disc = 1,
        string? genre = null, uint year = 0, uint track = 0, string? albumArtist = null) => new()
    {
        FilePath = "/music/f/a.flac",
        Extension = ".flac",
        Artist = artist,
        AlbumArtist = albumArtist,
        Album = album,
        Title = title,
        DiscNumber = disc,
        Genre = genre,
        Year = year,
        TrackNumber = track,
    };

    [Fact]
    public void A_write_that_changes_nothing_reports_no_move()
    {
        var id = SeedOne("Enya", "Watermark", "Orinoco Flow");

        Assert.False(_db.QueueUpdateWanted(id, isChecked: true, Wanted("Enya", "Watermark", "Orinoco Flow")));
    }

    [Theory]
    [InlineData("Genre")]
    [InlineData("Year")]
    [InlineData("Track")]
    public void A_field_that_moves_nothing_reports_no_move(string field)
    {
        var id = SeedOne("Enya", "Watermark", "Orinoco Flow");

        var wanted = field switch
        {
            "Genre" => Wanted("Enya", "Watermark", "Orinoco Flow", genre: "Celtic"),
            "Year" => Wanted("Enya", "Watermark", "Orinoco Flow", year: 1988),
            _ => Wanted("Enya", "Watermark", "Orinoco Flow", track: 2),
        };

        Assert.False(_db.QueueUpdateWanted(id, isChecked: false, wanted));
    }

    [Fact]
    public void A_new_artist_moves_the_row()
    {
        var id = SeedOne("Enya", "Watermark", "Orinoco Flow");

        Assert.True(_db.QueueUpdateWanted(id, isChecked: false, Wanted("Clannad", "Watermark", "Orinoco Flow")));
    }

    [Fact]
    public void An_album_artist_moves_the_row_even_when_the_artist_stands_still()
    {
        // The group key is the album artist when there is one, so setting it moves the row.
        var id = SeedOne("Enya", "Watermark", "Orinoco Flow");

        Assert.True(_db.QueueUpdateWanted(
            id, isChecked: false, Wanted("Enya", "Watermark", "Orinoco Flow", albumArtist: "Enya & Friends")));
    }

    [Fact]
    public void A_new_album_moves_the_row()
    {
        var id = SeedOne("Enya", "Watermark", "Orinoco Flow");

        Assert.True(_db.QueueUpdateWanted(id, isChecked: false, Wanted("Enya", "Shepherd Moons", "Orinoco Flow")));
    }

    [Fact]
    public void A_new_title_or_disc_moves_the_row_because_it_changes_what_it_is_a_copy_of()
    {
        var id = SeedOne("Enya", "Watermark", "Orinoco Flow");
        Assert.True(_db.QueueUpdateWanted(id, isChecked: false, Wanted("Enya", "Watermark", "Cursum Perficio")));

        var other = SeedOne("Enya", "Watermark", "Orinoco Flow", file: "b.flac");
        Assert.True(_db.QueueUpdateWanted(other, isChecked: false, Wanted("Enya", "Watermark", "Orinoco Flow", disc: 2)));
    }

    [Fact]
    public void The_ticked_facts_count_the_ticks_and_the_cloud_among_them()
    {
        // The toolbar buttons are enabled from these counts. Download is offered only when a ticked
        // row is not on disk, so "how many ticked rows are not on this disk" must be its own count,
        // separate from "are any rows ticked".
        _db.QueueInsertMany([
            Row("src", "src/A", "1.mp3", "Muse", "Hysteria", @checked: true),
            Row("src", "src/A", "2.mp3", "Muse", "Sing for Absolution", @checked: true,
                availability: FileAvailability.Dataless),
            Row("src", "src/A", "3.mp3", "Muse", "Endlessly", availability: FileAvailability.Dataless),
        ]);

        Assert.Equal((2, 1), _db.QueueTickedFacts());
    }

    [Fact]
    public void Nothing_ticked_is_nothing_to_act_on()
    {
        _db.QueueInsertMany([Row("src", "src/A", "1.mp3", "Muse", "Hysteria")]);

        Assert.Equal((0, 0), _db.QueueTickedFacts());
    }

    [Theory]
    // The album artist is the key when there is one…
    [InlineData("Various Artists", "Enya", "Watermark", "Various Artists", "Watermark")]
    // …and the track artist is used when there is not. This keeps a compilation of untagged rips
    // from splitting into one node per track.
    [InlineData("", "Enya", "Watermark", "Enya", "Watermark")]
    [InlineData(null, "Enya", "Watermark", "Enya", "Watermark")]
    // Whitespace is not a group, and a missing album is a real key: the Unknown bucket.
    [InlineData("  Enya  ", "Enya", "  Watermark ", "Enya", "Watermark")]
    [InlineData(null, null, null, "", "")]
    public void The_group_a_row_files_under_is_the_album_artist_then_the_artist(
        string? albumArtist, string? artist, string? album, string expectedArtist, string expectedAlbum)
    {
        // The toolbar computes this from the rows it has rather than asking the store, so it must use
        // the same rule as the store. A different rule would apply "Number discs" to a group that the
        // row is not in.
        Assert.Equal(
            (expectedArtist, expectedAlbum),
            LibraryDatabase.QueueGroupKeys(albumArtist, artist, album));
    }

    [Fact]
    public void Case_alone_is_not_a_move()
    {
        // The group columns collate NOCASE, so "enya" and "Enya" are one node. Reporting a move here
        // would rebuild the tree for a change of letter case only.
        var id = SeedOne("Enya", "Watermark", "Orinoco Flow");

        Assert.False(_db.QueueUpdateWanted(id, isChecked: false, Wanted("enya", "watermark", "orinoco flow")));
    }

    // ── the revision: what a slow reader checks before it writes ──────────────────────────────────

    /// <summary>
    /// A page is read on one thread and used on another a moment later. If a rewrite happened in
    /// between, the rows are old and would overwrite the edit on screen. So the store counts
    /// rewrites: the reader records the count before reading and checks it again before using the
    /// rows.
    /// </summary>
    [Fact]
    public void A_rewrite_moves_the_revision()
    {
        _db.QueueInsertMany([Row("src", "f", "a.mp3", artist: "A", title: "T", album: "Al")]);
        var id = IdOfFirst();

        var before = _db.QueueRevision;
        _db.QueueUpdateWanted(id, isChecked: true, new TrackInfo
        {
            FilePath = "/music/f/a.mp3", Extension = ".mp3",
            Artist = "A", Title = "T", Album = "Al", TrackNumber = 3,
        });

        Assert.True(_db.QueueRevision > before);
    }

    [Fact]
    public void A_tick_moves_it_too_and_reading_never_does()
    {
        _db.QueueInsertMany([Row("src", "f", "a.mp3", artist: "A", title: "T", album: "Al")]);

        var afterTick = Moved(() => _db.WholeQueue.CheckAll(true));
        Assert.True(afterTick);

        // Every read the grid makes while it scrolls. If reads changed the counter, every page would
        // be read twice.
        Assert.False(Moved(() => _db.WholeQueue.GroupPage("A", "Al", 0, 50)));
        Assert.False(Moved(() => _db.WholeQueue.Groups()));
        Assert.False(Moved(() => _db.QueueChipCounts()));
        Assert.False(Moved(() => _db.WholeQueue.CheckedRows()));
    }

    [Fact]
    public void A_scan_adding_rows_leaves_it_alone()
    {
        // New rows do not change a row that was already read, and a scan adds thousands of them. If
        // inserts changed the counter, every page being read would be read again several times.
        _db.QueueInsertMany([Row("src", "f", "a.mp3", artist: "A", title: "T", album: "Al")]);

        Assert.False(Moved(() => _db.QueueInsertMany(
            [Row("src", "f", "b.mp3", artist: "A", title: "U", album: "Al")])));
    }

    private bool Moved(Action act)
    {
        var before = _db.QueueRevision;
        act();
        return _db.QueueRevision != before;
    }

    private long IdOfFirst() => _db.WholeQueue.GroupPage("A", "Al", 0, 1)[0].Id;
}
