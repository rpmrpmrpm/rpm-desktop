using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// A rip that was never imported is invisible: it sits in <c>.rpm</c>, which the scanner skips,
/// so nothing in the app knows it is there and the disc has to be ripped again. These pin down what
/// counts as pending, and, since finding them means offering to delete them, what the delete is
/// allowed to touch.
/// </summary>
public class PendingRipsTests : IDisposable
{
    private readonly string _root;
    private readonly string _staging;

    public PendingRipsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pendingrips-{Guid.NewGuid():N}");
        _staging = Path.Combine(_root, LibraryDatabase.DataFolderName);
        Directory.CreateDirectory(_staging);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A rip folder with some audio in it, exactly as the ripper leaves one.</summary>
    private string Rip(string suffix, params string[] fileNames)
    {
        var folder = Path.Combine(_staging, PendingRips.FolderPrefix + suffix);
        Directory.CreateDirectory(folder);
        foreach (var name in fileNames)
            File.WriteAllText(Path.Combine(folder, name), "audio");
        return folder;
    }

    /// <summary>Stands in for TagLib: the album is whatever the folder was named after.</summary>
    private static TrackInfo FakeRead(string path) => new()
    {
        FilePath = path,
        Extension = Path.GetExtension(path),
        Album = "Urban Hymns",
        AlbumArtist = "The Verve",
        Title = Path.GetFileNameWithoutExtension(path),
    };

    /// <summary>A vinyl session folder, as the recorder leaves one.</summary>
    private string Vinyl(string suffix, params string[] fileNames)
    {
        var folder = Path.Combine(_staging, PendingRips.VinylFolderPrefix + suffix);
        Directory.CreateDirectory(folder);
        foreach (var name in fileNames)
            File.WriteAllText(Path.Combine(folder, name), "audio");
        return folder;
    }

    [Fact]
    public void An_interrupted_vinyl_recording_is_found_and_owes_a_split()
    {
        // Cancelled, or the app died: two sides recorded, never split. Twenty minutes a side:
        // this is exactly what must not silently vanish.
        Vinyl("aaa", "side1.flac", "side2.flac");

        var pending = PendingRips.Scan(_staging, FakeRead);

        var session = Assert.Single(pending);
        Assert.Equal(PendingKind.Vinyl, session.Kind);
        Assert.Equal(PendingVinylStage.Unsplit, session.Stage);
        Assert.Equal(2, session.SideCount);
        Assert.Equal(0, session.TrackCount);   // sides are not tracks
    }

    [Fact]
    public void A_joined_recording_still_counts_its_sides_not_the_album_file()
    {
        // Prepared for the split: the sides were joined into one album and moved into Originals. The
        // one file left at the top level is that album, a two-side record, not a one-side one.
        var folder = Vinyl("joined", "album.flac");
        var originals = Path.Combine(folder, RPM.Core.Splitting.AlbumFileSplitter.OriginalsFolderName);
        Directory.CreateDirectory(originals);
        File.WriteAllText(Path.Combine(originals, "side1.flac"), "audio");
        File.WriteAllText(Path.Combine(originals, "side2.flac"), "audio");

        var session = Assert.Single(PendingRips.Scan(_staging, FakeRead));

        Assert.Equal(PendingVinylStage.Unsplit, session.Stage);
        Assert.Equal(2, session.SideCount);
        Assert.Equal(0, session.TrackCount);
    }

    [Fact]
    public void A_cut_recording_owes_an_import_and_counts_its_tracks()
    {
        // The split ran: its tracks are in the session folder and the album they were cut from moved
        // into Originals beside the sides. Those tracks are tracks. Counting them as sides would read as
        // "12 sides to split" for a record that has two.
        var folder = Vinyl("cut", "01 - One.flac", "02 - Two.flac", "03 - Three.flac");
        var originals = Path.Combine(folder, RPM.Core.Splitting.AlbumFileSplitter.OriginalsFolderName);
        Directory.CreateDirectory(originals);
        File.WriteAllText(Path.Combine(originals, "album.flac"), "audio");
        File.WriteAllText(Path.Combine(originals, "side1.flac"), "audio");
        File.WriteAllText(Path.Combine(originals, "side2.flac"), "audio");

        var session = Assert.Single(PendingRips.Scan(_staging, FakeRead));

        Assert.Equal(PendingVinylStage.TracksCut, session.Stage);
        Assert.Equal(3, session.TrackCount);
        Assert.Equal(2, session.SideCount);
    }

    [Fact]
    public void A_recording_being_re_split_still_owes_the_split()
    {
        // Re-splitting brings the album back out of Originals, and the previous cut's tracks are
        // still lying beside it. An album at the top level is a split that has not run.
        var folder = Vinyl("resplit", "album.flac", "01 - One.flac", "02 - Two.flac");
        var originals = Path.Combine(folder, RPM.Core.Splitting.AlbumFileSplitter.OriginalsFolderName);
        Directory.CreateDirectory(originals);
        File.WriteAllText(Path.Combine(originals, "side1.flac"), "audio");

        var session = Assert.Single(PendingRips.Scan(_staging, FakeRead));

        Assert.Equal(PendingVinylStage.Unsplit, session.Stage);
        Assert.Equal(1, session.SideCount);
    }

    [Fact]
    public void A_sessions_size_includes_the_sides_it_keeps()
    {
        // What the row promises back on delete is the whole folder, sides included (they are the
        // biggest thing in it).
        var folder = Vinyl("size", "album.flac");
        var originals = Path.Combine(folder, RPM.Core.Splitting.AlbumFileSplitter.OriginalsFolderName);
        Directory.CreateDirectory(originals);
        File.WriteAllText(Path.Combine(originals, "side1.flac"), new string('x', 500));

        var session = Assert.Single(PendingRips.Scan(_staging, FakeRead));

        Assert.True(session.SizeBytes > 500);
    }

    [Fact]
    public void A_split_vinyl_recording_stays_listed_for_its_kept_album()
    {
        // Split and imported: the tracks moved out, and the album it was cut from is kept in
        // _originals, which the scanner skips, so only looking there keeps it from going invisible.
        var folder = Vinyl("bbb");
        var originals = Path.Combine(folder, RPM.Core.Splitting.AlbumFileSplitter.OriginalsFolderName);
        Directory.CreateDirectory(originals);
        File.WriteAllText(Path.Combine(originals, "album.flac"), "audio");

        var session = Assert.Single(PendingRips.Scan(_staging, FakeRead));

        Assert.Equal(PendingKind.Vinyl, session.Kind);
        Assert.Equal(PendingVinylStage.Kept, session.Stage);
        Assert.True(session.SplitDone);
    }

    [Fact]
    public void An_empty_vinyl_folder_is_not_pending()
    {
        Directory.CreateDirectory(Path.Combine(_staging, PendingRips.VinylFolderPrefix + "empty"));
        Assert.Empty(PendingRips.Scan(_staging, FakeRead));
    }

    [Fact]
    public void Rips_and_vinyl_sessions_are_listed_together_as_two_kinds()
    {
        Rip("disc", "01 track.flac");
        Vinyl("record", "side1.flac");

        var pending = PendingRips.Scan(_staging, FakeRead);

        Assert.Equal(2, pending.Count);
        Assert.Single(pending, p => p.Kind == PendingKind.Rip);
        Assert.Single(pending, p => p.Kind == PendingKind.Vinyl);
    }

    [Fact]
    public void Delete_accepts_a_vinyl_session()
    {
        var folder = Vinyl("gone", "side1.flac");
        PendingRips.Delete(_staging, folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void A_ripped_but_unimported_disc_is_found()
    {
        Rip("aaa", "01 track.flac", "02 track.flac", "03 track.flac");

        var pending = PendingRips.Scan(_staging, FakeRead);

        var rip = Assert.Single(pending);
        Assert.Equal("Urban Hymns", rip.Name);
        Assert.Equal("The Verve", rip.Artist);
        Assert.Equal(3, rip.TrackCount);
        Assert.True(rip.SizeBytes > 0);
    }

    [Fact]
    public void An_imported_rip_is_not_pending()
    {
        // The import moved the files out and left the folder behind. Nothing is waiting.
        Directory.CreateDirectory(Path.Combine(_staging, PendingRips.FolderPrefix + "done"));

        Assert.Empty(PendingRips.Scan(_staging, FakeRead));
    }

    [Fact]
    public void The_library_database_is_not_a_rip()
    {
        // library.db lives in the same folder. Scanning must not take an interest in it.
        File.WriteAllText(Path.Combine(_staging, LibraryDatabase.DataFileName), "sqlite");
        Directory.CreateDirectory(Path.Combine(_staging, "artwork"));

        Assert.Empty(PendingRips.Scan(_staging, FakeRead));
    }

    [Fact]
    public void A_rip_whose_tags_cannot_be_read_still_shows_up()
    {
        // A rip that died mid-track leaves a file TagLib will not open. It is still pending, and
        // still the thing the user does not want to rip again.
        var folder = Rip("broken", "01 half.flac");

        var pending = PendingRips.Scan(_staging, _ => throw new IOException("corrupt"));

        var rip = Assert.Single(pending);
        Assert.Null(rip.Name);   // nothing named it: the guid folder is not an answer, the caller says what it is
        Assert.Equal(1, rip.TrackCount);
        Assert.Equal(folder, rip.Folder);
    }

    [Fact]
    public void A_rips_manifest_names_it_when_its_files_cannot()
    {
        // The rip died before it tagged anything, so the audio says nothing. What the disc lookup knew
        // was written down before the first sector was read, and that is the name.
        var folder = Rip("named", "01 half.flac");
        new RipManifest(RipSource.Cd, "Urban Hymns", "The Verve", TrackCount: 13,
            CreatedUtc: new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc), Device: "MATSHITA UJ-8E8")
            .WriteTo(folder);

        var rip = Assert.Single(PendingRips.Scan(_staging, _ => throw new IOException("corrupt")));

        Assert.Equal("Urban Hymns", rip.Name);
        Assert.Equal("The Verve", rip.Artist);
        Assert.Equal(RipSource.Cd, rip.Source);
        Assert.Equal(13, rip.PlannedTrackCount);   // one file landed of thirteen: it stopped short
        Assert.Equal(1, rip.TrackCount);
        Assert.Equal(new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc), rip.RippedUtc);
    }

    [Fact]
    public void Tags_beat_the_manifest()
    {
        // The manifest is what the disc lookup guessed before the rip; the tags are what the user has
        // since corrected. Whatever is on the files wins.
        var folder = Rip("tagged", "01 track.flac");
        new RipManifest(RipSource.Cd, "Wrong Album", "Wrong Artist").WriteTo(folder);

        var rip = Assert.Single(PendingRips.Scan(_staging, FakeRead));

        Assert.Equal("Urban Hymns", rip.Name);
        Assert.Equal("The Verve", rip.Artist);
    }

    [Fact]
    public void A_recordings_manifest_says_it_is_vinyl()
    {
        // Nothing has named the record (that happens at the split), so all the row has to go on is
        // that this was a record, played on this input, on this day.
        var folder = Vinyl("manifest", "side1.flac");
        new RipManifest(RipSource.Vinyl, CreatedUtc: DateTime.UtcNow, Device: "Line In").WriteTo(folder);

        var session = Assert.Single(PendingRips.Scan(_staging, _ => throw new IOException("no tags")));

        Assert.Null(session.Name);
        Assert.Equal(RipSource.Vinyl, session.Source);
        Assert.Equal(1, session.SideCount);
    }

    [Fact]
    public void A_manifest_survives_a_round_trip_and_a_missing_one_is_not_a_crash()
    {
        var folder = Rip("roundtrip", "01 track.flac");
        var written = new RipManifest(RipSource.Dvd, "Live at Wembley", "Queen", 24,
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "HL-DT-ST BD-RE", DiscId: "abc123");
        written.WriteTo(folder);

        Assert.Equal(written, RipManifest.Read(folder));
        Assert.Null(RipManifest.Read(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public void The_manifest_is_not_audio_and_does_not_make_an_empty_folder_pending()
    {
        // An imported rip leaves its manifest behind. Nothing is waiting: a name with no files under
        // it is not work.
        var folder = Path.Combine(_staging, PendingRips.FolderPrefix + "imported");
        Directory.CreateDirectory(folder);
        new RipManifest(RipSource.Cd, "Urban Hymns").WriteTo(folder);

        Assert.Empty(PendingRips.Scan(_staging, FakeRead));
    }

    [Fact]
    public void Newest_first()
    {
        var older = Rip("older", "a.flac");
        Directory.SetCreationTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        var newer = Rip("newer", "b.flac");
        Directory.SetCreationTimeUtc(newer, DateTime.UtcNow);

        var pending = PendingRips.Scan(_staging, FakeRead);

        Assert.Equal(2, pending.Count);
        Assert.Equal(newer, pending[0].Folder);
    }

    [Fact]
    public void A_missing_staging_folder_is_no_rips_not_a_crash()
        => Assert.Empty(PendingRips.Scan(Path.Combine(_root, "nope"), FakeRead));

    // ── what the delete may touch ──

    [Fact]
    public void Deleting_a_rip_removes_it()
    {
        var folder = Rip("gone", "a.flac");

        PendingRips.Delete(_staging, folder);

        Assert.False(Directory.Exists(folder));
        Assert.Empty(PendingRips.Scan(_staging, FakeRead));
    }

    [Fact]
    public void The_delete_refuses_anything_that_is_not_a_rip_folder()
    {
        // The staging folder holds library.db. A delete that could be talked into taking the whole
        // folder would take the user's library mirror with it.
        var artwork = Path.Combine(_staging, "artwork");
        Directory.CreateDirectory(artwork);

        Assert.Throws<ArgumentException>(() => PendingRips.Delete(_staging, _staging));
        Assert.Throws<ArgumentException>(() => PendingRips.Delete(_staging, artwork));
        Assert.Throws<ArgumentException>(() => PendingRips.Delete(_staging, _root));

        Assert.True(Directory.Exists(_staging));
        Assert.True(Directory.Exists(artwork));
    }

    [Fact]
    public void The_delete_refuses_a_rip_folder_somewhere_else()
    {
        // Right name, wrong place: a "rip-" folder outside staging is not ours to remove.
        var elsewhere = Path.Combine(_root, PendingRips.FolderPrefix + "elsewhere");
        Directory.CreateDirectory(elsewhere);

        Assert.Throws<ArgumentException>(() => PendingRips.Delete(_staging, elsewhere));
        Assert.True(Directory.Exists(elsewhere));
    }

    [Fact]
    public void The_delete_refuses_to_climb_out_with_a_traversal()
    {
        var escape = Path.Combine(_staging, PendingRips.FolderPrefix + "x", "..", "..");

        Assert.Throws<ArgumentException>(() => PendingRips.Delete(_staging, escape));
        Assert.True(Directory.Exists(_root));
    }
}
