using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// What the user says about a RECORD in the import queue, and how it reaches the folder its files
/// are filed into. The answers belong to the album: classical, levelled, the printed disc label.
/// The album folder does not exist until the import. The queue's tree already knows which record a
/// track is on, so the intent is held against that group and follows the ROWS through every
/// rename.
/// </summary>
public class QueueAlbumIntentTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public QueueAlbumIntentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"queueintent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose() { _db.Dispose(); try { Directory.Delete(_root, true); } catch { } }

    private static QueueTrack Row(string file, string artist = "Karajan", string album = "Symphony No. 5")
    {
        var info = new TrackInfo
        {
            FilePath = $"/music/src/{file}",
            Extension = ".flac",
            Artist = artist,
            AlbumArtist = artist,
            Album = album,
            Title = Path.GetFileNameWithoutExtension(file),
        };
        return new QueueTrack { Source = "/src", Folder = "src", FileName = file, Current = info, Wanted = info };
    }

    [Fact]
    public void An_answer_is_held_against_the_record_and_read_back()
    {
        _db.QueueInsertMany([Row("01.flac"), Row("02.flac")]);
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5",
            new QueueAlbumIntent(ClassicalSet: true, Classical: true));

        var intent = _db.QueueAlbumIntentFor("Karajan", "Symphony No. 5");
        Assert.NotNull(intent);
        Assert.True(intent!.ClassicalSet);
        Assert.True(intent.Classical);
        Assert.False(intent.NormalizeSet);

        // Every row of the record carries it, which is what makes it survive a rename.
        Assert.All(_db.WholeQueue.RowsInGroup("Karajan", "Symphony No. 5"),
                   r => Assert.Equal(intent.Id, r.AlbumIntentId));
    }

    /// <summary>
    /// What a window asks before SHOWING one of these answers: the rows' record, when all the rows
    /// are of one record. A selection spanning two records has no single answer to show, and showing
    /// the newest would put one record's disc label in the other's field.
    /// </summary>
    [Fact]
    public void A_selection_spanning_two_records_shares_no_answer()
    {
        _db.QueueInsertMany([Row("01.flac"), Row("02.flac"), Row("03.flac", album: "Symphony No. 9")]);
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5", new QueueAlbumIntent(DiscArt: [1, 2, 3]));
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 9", new QueueAlbumIntent(DiscArt: [9, 9, 9]));

        var five = _db.WholeQueue.RowsInGroup("Karajan", "Symphony No. 5").Select(r => r.Id).ToList();
        var nine = _db.WholeQueue.RowsInGroup("Karajan", "Symphony No. 9").Select(r => r.Id).ToList();

        // One record's rows: its own answer, picture and all.
        Assert.Equal<byte[]?>([1, 2, 3], _db.QueueAlbumIntentSharedBy(five)?.DiscArt);

        // Both records' rows: no one answer, rather than whichever was said last.
        Assert.Null(_db.QueueAlbumIntentSharedBy([.. five, .. nine]));
    }

    /// <summary>A record where only half of the rows have been told something has not been told it.
    /// The answer is stamped on every row of the group, so a row without it means the selection is
    /// not that record's.</summary>
    [Fact]
    public void Rows_that_carry_no_answer_share_none()
    {
        _db.QueueInsertMany([Row("01.flac"), Row("02.flac")]);
        var rows = _db.WholeQueue.RowsInGroup("Karajan", "Symphony No. 5").Select(r => r.Id).ToList();

        Assert.Null(_db.QueueAlbumIntentSharedBy(rows));
        Assert.Null(_db.QueueAlbumIntentSharedBy([]));
    }

    [Fact]
    public void A_second_visit_adds_to_what_was_said_rather_than_replacing_it()
    {
        _db.QueueInsertMany([Row("01.flac")]);
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5",
            new QueueAlbumIntent(ClassicalSet: true, Classical: true));
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5",
            new QueueAlbumIntent(NormalizeSet: true, Normalize: false));

        var intent = _db.QueueAlbumIntentFor("Karajan", "Symphony No. 5")!;
        Assert.True(intent.Classical);          // still there
        Assert.True(intent.NormalizeSet);
        Assert.False(intent.Normalize);
    }

    [Fact]
    public void Null_is_a_real_answer_and_is_kept_apart_from_unsaid()
    {
        // "Decide from the tracks" is a decision; it must not read back as "nobody said".
        _db.QueueInsertMany([Row("01.flac")]);
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5",
            new QueueAlbumIntent(ClassicalSet: true, Classical: null));

        var intent = _db.QueueAlbumIntentFor("Karajan", "Symphony No. 5")!;
        Assert.True(intent.ClassicalSet);
        Assert.Null(intent.Classical);
    }

    [Fact]
    public void The_answer_follows_the_music_when_the_record_is_renamed()
    {
        _db.QueueInsertMany([Row("01.flac"), Row("02.flac")]);
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5",
            new QueueAlbumIntent(ClassicalSet: true, Classical: true));

        foreach (var row in _db.WholeQueue.RowsInGroup("Karajan", "Symphony No. 5"))
            _db.QueueUpdateWanted(row.Id, row.IsChecked,
                row.Wanted with { Album = "Symphony No. 5 in C minor" });

        Assert.Null(_db.QueueAlbumIntentFor("Karajan", "Symphony No. 5"));
        var moved = _db.QueueAlbumIntentFor("Karajan", "Symphony No. 5 in C minor");
        Assert.True(moved?.Classical);
    }

    [Fact]
    public void A_disc_scanned_later_joins_the_answer_the_record_already_carries()
    {
        _db.QueueInsertMany([Row("cd1-01.flac")]);
        var id = _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5",
            new QueueAlbumIntent(ClassicalSet: true, Classical: true));

        _db.QueueInsertMany([Row("cd2-01.flac")]);
        _db.QueueAdoptAlbumIntents();

        Assert.All(_db.WholeQueue.RowsInGroup("Karajan", "Symphony No. 5"),
                   r => Assert.Equal(id, r.AlbumIntentId));
    }

    [Fact]
    public void A_visit_that_changed_nothing_leaves_what_was_said_alone()
    {
        _db.QueueInsertMany([Row("01.flac")]);
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5",
            new QueueAlbumIntent(ClassicalSet: true, Classical: true));
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5", new QueueAlbumIntent());

        // An empty result is "I touched nothing", not "forget what I said last time".
        Assert.True(_db.QueueAlbumIntentFor("Karajan", "Symphony No. 5")?.Classical);
        Assert.Single(_db.QueueAlbumIntents());
    }

    [Fact]
    public void The_answer_dies_with_the_last_row_of_its_record()
    {
        _db.QueueInsertMany([Row("01.flac"), Row("02.flac")]);
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5",
            new QueueAlbumIntent(ClassicalSet: true, Classical: true));

        var rows = _db.WholeQueue.RowsInGroup("Karajan", "Symphony No. 5");
        _db.QueueRemove([rows[0].Id]);
        Assert.Single(_db.QueueAlbumIntents());     // one row left, still meaningful

        _db.QueueRemove([rows[1].Id]);
        Assert.Empty(_db.QueueAlbumIntents());      // nothing left for it to be about
    }

    [Fact]
    public void An_answer_lands_on_the_rows_even_when_the_lookup_has_just_renamed_the_record()
    {
        // This matters for the disc label: the album editor collects the answers AND renames the
        // record from the release it looked up, so by the time the answers come back, the group they
        // were given for is gone. Keyed by row, the answers still arrive.
        _db.QueueInsertMany([Row("01.flac"), Row("02.flac")]);
        var ids = _db.WholeQueue.RowsInGroup("Karajan", "Symphony No. 5").Select(r => r.Id).ToList();

        foreach (var row in _db.WholeQueue.RowsInGroup("Karajan", "Symphony No. 5"))
            _db.QueueUpdateWanted(row.Id, row.IsChecked,
                row.Wanted with { Album = "Symphony No. 5 (Remastered)", AlbumArtist = "Berliner Philharmoniker" });

        _db.QueueSetAlbumIntentForRows(ids, new QueueAlbumIntent(
            DiscArt: [1, 2, 3], ReleaseProvider: "musicbrainz", ReleaseId: "rel-9"));

        var intent = _db.QueueAlbumIntentForRows(ids);
        Assert.Equal([1, 2, 3], intent?.DiscArt);
        Assert.Equal("rel-9", intent?.ReleaseId);
        Assert.All(_db.WholeQueue.RowsInGroup("Berliner Philharmoniker", "Symphony No. 5 (Remastered)"),
                   r => Assert.Equal(intent!.Id, r.AlbumIntentId));
    }

    [Fact]
    public void The_pressing_a_record_was_settled_against_is_remembered()
    {
        _db.QueueInsertMany([Row("01.flac")]);
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5",
            new QueueAlbumIntent(ReleaseProvider: "musicbrainz", ReleaseId: "rel-1"));

        // A later answer about something else leaves the pressing alone.
        _db.QueueSetAlbumIntent("Karajan", "Symphony No. 5",
            new QueueAlbumIntent(ClassicalSet: true, Classical: true));

        var intent = _db.QueueAlbumIntentFor("Karajan", "Symphony No. 5")!;
        Assert.Equal("musicbrainz", intent.ReleaseProvider);
        Assert.Equal("rel-1", intent.ReleaseId);
        Assert.True(intent.Classical);
    }

    // ── the run's scope ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_folders_this_run_filed_into_are_touched()
    {
        var folders = AlbumIntentApplier.FoldersPerIntent(
        [
            (7, Path.Combine(_root, "Karajan", "Symphony No. 5", "01.flac")),
            (7, Path.Combine(_root, "Karajan", "Symphony No. 5", "02.flac")),
            (7, Path.Combine(_root, "Karajan", "Symphony No. 5 (Disc 2)", "01.flac")),
            (0, Path.Combine(_root, "Someone Else", "Other", "01.flac")),   // no intent, no folder
        ], _root);

        var mine = Assert.Single(folders);
        Assert.Equal(7, mine.Key);
        Assert.Equal(["Karajan/Symphony No. 5", "Karajan/Symphony No. 5 (Disc 2)"], mine.Value);
    }

    [Fact]
    public void A_file_that_landed_outside_the_library_is_not_a_folder_of_the_library()
    {
        Assert.Empty(AlbumIntentApplier.FoldersPerIntent(
            [(7, Path.Combine(Path.GetTempPath(), "elsewhere", "01.flac"))], _root));
    }

    [Fact]
    public void Applying_writes_the_records_answers_and_its_disc_label()
    {
        var relative = "Karajan/Symphony No. 5";
        Directory.CreateDirectory(Path.Combine(_root, "Karajan", "Symphony No. 5"));

        // A one-pixel PNG is enough: this is about where the bytes go, not what they are.
        byte[] disc = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10];
        AlbumIntentApplier.Apply(_db, _root, relative,
            new QueueAlbumIntent(ClassicalSet: true, Classical: true, DiscArt: disc));

        var meta = _db.GetAlbumMeta(relative);
        Assert.True(meta.Classical);
        Assert.Null(meta.Normalize);   // nobody said, so nothing was written
        Assert.NotNull(FolderArtWriter.FindDiscImage(Path.Combine(_root, "Karajan", "Symphony No. 5")));
    }

    /// <summary>The record settled on in the queue becomes the album's link once the album is on the
    /// shelf. It is what named the tracks, so it must reach the album and not stop at the tags.
    /// Otherwise an album identified by hand would be linked to nothing.</summary>
    [Fact]
    public void Applying_links_the_album_to_the_pressing_the_queue_settled_on()
    {
        var relative = "Karajan/Symphony No. 5";
        Directory.CreateDirectory(Path.Combine(_root, "Karajan", "Symphony No. 5"));

        AlbumIntentApplier.Apply(_db, _root, relative,
            new QueueAlbumIntent(ReleaseProvider: "discogs", ReleaseId: "430456"));

        var meta = _db.GetAlbumMeta(relative);
        Assert.True(meta.IsLinkedToPressing);
        Assert.Equal("430456", meta.DiscogsReleaseId);
    }

    /// <summary>An import that settled on nothing must not unlink a record that is already linked.
    /// The second disc of a set, arriving later, says nothing about the pressing.</summary>
    [Fact]
    public void An_intent_with_no_pressing_leaves_the_link_alone()
    {
        var relative = "Karajan/Symphony No. 5";
        Directory.CreateDirectory(Path.Combine(_root, "Karajan", "Symphony No. 5"));
        _db.SetAlbumPressing(relative, "discogs", "430456");

        AlbumIntentApplier.Apply(_db, _root, relative,
            new QueueAlbumIntent(ClassicalSet: true, Classical: true));

        Assert.Equal("430456", _db.GetAlbumMeta(relative).DiscogsReleaseId);
    }

    [Fact]
    public void What_nobody_said_in_the_queue_is_left_alone_on_the_shelf()
    {
        // The second disc of a record that is already rated: importing it must not clear the rating.
        var relative = "Karajan/Symphony No. 5";
        Directory.CreateDirectory(Path.Combine(_root, "Karajan", "Symphony No. 5"));
        _db.SetAlbumReview(relative, 5, "the one to own", 123);
        _db.SetAlbumNormalize(relative, true);

        AlbumIntentApplier.Apply(_db, _root, relative,
            new QueueAlbumIntent(ClassicalSet: true, Classical: true));

        var meta = _db.GetAlbumMeta(relative);
        Assert.True(meta.Classical);
        Assert.True(meta.Normalize);
        Assert.Equal(5, meta.Rating);
        Assert.Equal("the one to own", meta.ReviewMarkdown);
    }
}
