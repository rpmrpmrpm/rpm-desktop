using RPM.Core.Library;
using RPM.Core.Import;

namespace RPM.Core.Tests;

/// <summary>
/// Re-filing a whole library with a naming template: the files move, the mirror is updated without
/// losing the rows' identity, and what an album has besides its tracks (the folder cover, the row in
/// album_meta, a playlist entry that refers to it by path) moves too.
/// </summary>
public class LibraryRefilerTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryRefilerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"refile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Writes a file at a library-relative path and adds it to the mirror, with the tags a
    /// template uses. The file is real, because a re-file moves real files.</summary>
    private LibraryTrack Add(string relativePath, string albumArtist, string album, string title,
        uint track = 1, string? rpmId = null)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "audio");

        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            SizeBytes = 5,
            ModifiedUtcTicks = 1,
            AlbumArtist = albumArtist,
            Artist = albumArtist,
            Album = album,
            Title = title,
            TrackNumber = track,
            RpmId = rpmId,
        });
        return _db.Query(title)[0];
    }

    private static NamingTemplate Numbered => NamingTemplate.ArtistAlbumNumberedTitle;

    private bool Exists(string relativePath) =>
        File.Exists(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // ── the move itself ──

    [Fact]
    public void Files_the_template_would_put_elsewhere_are_moved_and_the_mirror_follows()
    {
        Add("junk/whatever.mp3", "Radiohead", "OK Computer", "Airbag", track: 1);

        var outcome = LibraryRefiler.Refile(_db, _root, Numbered);

        Assert.Equal(1, outcome.Moved);
        Assert.Equal(0, outcome.Refused);
        Assert.False(Exists("junk/whatever.mp3"));
        Assert.True(Exists("Radiohead/OK Computer/01 - Airbag.mp3"));

        var row = Assert.Single(_db.Query());
        Assert.Equal("Radiohead/OK Computer/01 - Airbag.mp3", row.RelativePath);
        Assert.Equal("Radiohead/OK Computer", row.Folder);
        Assert.Equal("01 - Airbag.mp3", row.FileName);
    }

    [Fact]
    public void A_moved_row_keeps_its_id_so_what_hangs_off_it_survives()
    {
        var before = Add("junk/whatever.mp3", "Radiohead", "OK Computer", "Airbag");

        LibraryRefiler.Refile(_db, _root, Numbered);

        var after = Assert.Single(_db.Query());
        Assert.Equal(before.Id, after.Id);
    }

    [Fact]
    public void A_track_the_template_already_agrees_with_is_counted_not_moved()
    {
        Add("Radiohead/OK Computer/01 - Airbag.mp3", "Radiohead", "OK Computer", "Airbag");

        var outcome = LibraryRefiler.Refile(_db, _root, Numbered);

        Assert.Equal(0, outcome.Moved);
        Assert.Equal(1, outcome.AlreadyInPlace);
        Assert.True(Exists("Radiohead/OK Computer/01 - Airbag.mp3"));
    }

    [Fact]
    public void An_empty_library_is_a_no_op()
    {
        var outcome = LibraryRefiler.Refile(_db, _root, Numbered);

        Assert.Equal(0, outcome.Moved);
        Assert.Equal(0, outcome.AlreadyInPlace);
        Assert.Empty(outcome.Refusals);
    }

    [Fact]
    public void The_folder_a_record_left_is_cleaned_up()
    {
        Add("junk/a.mp3", "Radiohead", "OK Computer", "Airbag", track: 1);
        Add("junk/b.mp3", "Radiohead", "OK Computer", "Karma Police", track: 2);

        LibraryRefiler.Refile(_db, _root, Numbered);

        Assert.False(Directory.Exists(Path.Combine(_root, "junk")));
    }

    // ── what an album has besides its tracks ──

    [Fact]
    public void The_folders_cover_moves_with_the_record()
    {
        Add("junk/a.mp3", "Radiohead", "OK Computer", "Airbag", track: 1);
        File.WriteAllText(Path.Combine(_root, "junk", "cover.jpg"), "art");

        LibraryRefiler.Refile(_db, _root, Numbered);

        Assert.True(Exists("Radiohead/OK Computer/cover.jpg"));
        Assert.False(Directory.Exists(Path.Combine(_root, "junk")));
    }

    [Fact]
    public void The_rating_and_review_follow_the_folder()
    {
        Add("junk/a.mp3", "Radiohead", "OK Computer", "Airbag", track: 1);
        _db.SetAlbumReview("junk", 5, "A masterpiece.", 123);
        _db.SetAlbumFavorite("junk", true);

        LibraryRefiler.Refile(_db, _root, Numbered);

        var meta = _db.GetAlbumMeta("Radiohead/OK Computer");
        Assert.Equal(5, meta.Rating);
        Assert.Equal("A masterpiece.", meta.ReviewMarkdown);
        Assert.True(meta.IsFavorite);
        Assert.Equal(0, _db.GetAlbumMeta("junk").Rating);
    }

    [Fact]
    public void A_record_split_across_two_new_folders_keeps_its_sidecars_where_they_are()
    {
        // Two tracks that the template puts under different album artists: the old folder was not one
        // album's folder, so the cover stays, rather than guessing which half it belongs to.
        Add("mixed/a.mp3", "Radiohead", "OK Computer", "Airbag", track: 1);
        Add("mixed/b.mp3", "Portishead", "Dummy", "Roads", track: 1);
        File.WriteAllText(Path.Combine(_root, "mixed", "cover.jpg"), "art");

        LibraryRefiler.Refile(_db, _root, Numbered);

        Assert.True(Exists("Radiohead/OK Computer/01 - Airbag.mp3"));
        Assert.True(Exists("Portishead/Dummy/01 - Roads.mp3"));
        Assert.True(Exists("mixed/cover.jpg"));
    }

    [Fact]
    public void A_folder_still_holding_music_keeps_its_sidecars()
    {
        // One track moves, and another audio file stays: the folder is still in use.
        Add("junk/a.mp3", "Radiohead", "OK Computer", "Airbag", track: 1);
        File.WriteAllText(Path.Combine(_root, "junk", "rip.log"), "log");
        File.WriteAllText(Path.Combine(_root, "junk", "leftover.mp3"), "audio not in the library");

        LibraryRefiler.Refile(_db, _root, Numbered);

        Assert.True(Exists("junk/rip.log"));
        Assert.True(Exists("junk/leftover.mp3"));
    }

    [Fact]
    public void A_playlist_entry_that_names_a_file_by_path_is_repointed()
    {
        var track = Add("junk/a.mp3", "Radiohead", "OK Computer", "Airbag", track: 1);
        var playlist = _db.CreatePlaylist(null, "Evening");
        _db.AddSongsToPlaylist(playlist, [(track.RpmId, track.RelativePath)], AlreadyInPlaylist.AddAnother);

        LibraryRefiler.Refile(_db, _root, Numbered);

        var songs = _db.FlattenPlaylistTracks(playlist);
        Assert.Equal("Radiohead/OK Computer/01 - Airbag.mp3", Assert.Single(songs).RelativePath);
    }

    // ── progress and cancelling ──

    [Fact]
    public void Progress_is_reported_per_track_and_finishes_on_the_total()
    {
        Add("junk/a.mp3", "Radiohead", "OK Computer", "Airbag", track: 1);
        Add("junk/b.mp3", "Radiohead", "OK Computer", "Karma Police", track: 2);

        var seen = new List<RefileProgress>();
        LibraryRefiler.Refile(_db, _root, Numbered, new SynchronousProgress<RefileProgress>(seen.Add));

        Assert.Equal(3, seen.Count);            // one before each file, one at the end
        Assert.All(seen, p => Assert.Equal(2, p.Total));
        Assert.Equal(2, seen[^1].Done);
    }

    [Fact]
    public void Cancelling_leaves_what_already_moved_moved_and_the_mirror_agreeing_with_it()
    {
        Add("junk/a.mp3", "Radiohead", "OK Computer", "Airbag", track: 1);
        Add("junk/b.mp3", "Radiohead", "OK Computer", "Karma Police", track: 2);

        using var cts = new CancellationTokenSource();
        // Cancel once the first file has been handled.
        var progress = new SynchronousProgress<RefileProgress>(p => { if (p.Done == 1) cts.Cancel(); });

        Assert.Throws<OperationCanceledException>(
            () => LibraryRefiler.Refile(_db, _root, Numbered, progress, cts.Token));

        var moved = Assert.Single(_db.Query(), t => t.Folder == "Radiohead/OK Computer");
        Assert.True(Exists(moved.RelativePath));
        // The track that was not processed is unchanged, both row and file.
        var stayed = Assert.Single(_db.Query(), t => t.Folder == "junk");
        Assert.True(Exists(stayed.RelativePath));
    }

    /// <summary>Reports on the calling thread. <see cref="Progress{T}"/> posts to a synchronization
    /// context, and a test has none, so its callbacks would run after the assertions.</summary>
    private sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    // ── the album following its tracks, called directly ───────────────────
    //
    // FollowFolders is public because the bulk pass is not the only code that moves an album: the
    // album editor re-files one album at a time, through MassEdit. That path must also call it, or the
    // album_meta row stays on a folder with no music in it.

    /// <summary>An album re-filed from the album editor keeps everything the mirror had about it.</summary>
    [Fact]
    public void An_album_that_moves_takes_its_row_with_it()
    {
        Add("Old/Record/01 One.mp3", "The Band", "The Record", "One");
        _db.SetAlbumReview("Old/Record", 4, null, 1);
        _db.SetAlbumPressing("Old/Record", "discogs", "12345");

        // The tracks have moved; FollowFolders moves the rest.
        var moved = Path.Combine(_root, "The Band", "The Record");
        Directory.CreateDirectory(moved);
        File.Move(Path.Combine(_root, "Old", "Record", "01 One.mp3"),
                  Path.Combine(moved, "01 One.mp3"));

        LibraryRefiler.FollowFolders(_db, _root, new(StringComparer.OrdinalIgnoreCase)
        {
            ["Old/Record"] = ["The Band/The Record"],
        });

        var landed = _db.GetAlbumMeta("The Band/The Record");
        Assert.Equal(4, landed.Rating);
        Assert.Equal("12345", landed.ReleaseId);
        Assert.Equal("discogs", landed.ReleaseProvider);
        // Nothing is left under the old folder.
        Assert.Equal(0, _db.GetAlbumMeta("Old/Record").Rating);
    }

    /// <summary>The folder's own files move too: every view shows the album with the cover, and a cover
    /// left in the old folder would make the album lose its picture on a rename.</summary>
    [Fact]
    public void The_folders_cover_comes_across_with_it()
    {
        Add("Old/Record/01 One.mp3", "The Band", "The Record", "One");
        File.WriteAllText(Path.Combine(_root, "Old", "Record", "cover.jpg"), "picture");

        var moved = Path.Combine(_root, "The Band", "The Record");
        Directory.CreateDirectory(moved);
        File.Move(Path.Combine(_root, "Old", "Record", "01 One.mp3"),
                  Path.Combine(moved, "01 One.mp3"));

        LibraryRefiler.FollowFolders(_db, _root, new(StringComparer.OrdinalIgnoreCase)
        {
            ["Old/Record"] = ["The Band/The Record"],
        });

        Assert.True(Exists("The Band/The Record/cover.jpg"));
    }

    /// <summary>Music still in the old folder means the folder did not belong to this album only.
    /// Nothing moves, and the row stays, because it may describe the music that is still there.</summary>
    [Fact]
    public void A_folder_with_other_music_still_in_it_is_left_alone()
    {
        Add("Mixed/01 One.mp3", "The Band", "The Record", "One");
        Add("Mixed/02 Other.mp3", "Someone Else", "Another Record", "Other");
        _db.SetAlbumReview("Mixed", 5, null, 1);

        var moved = Path.Combine(_root, "The Band", "The Record");
        Directory.CreateDirectory(moved);
        File.Move(Path.Combine(_root, "Mixed", "01 One.mp3"), Path.Combine(moved, "01 One.mp3"));

        LibraryRefiler.FollowFolders(_db, _root, new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mixed"] = ["The Band/The Record"],
        });

        Assert.Equal(5, _db.GetAlbumMeta("Mixed").Rating);
        Assert.Equal(0, _db.GetAlbumMeta("The Band/The Record").Rating);
    }

    /// <summary>An album whose tracks moved to more than one folder has no single folder to follow, so
    /// nothing is guessed.</summary>
    [Fact]
    public void A_record_that_scattered_is_not_followed()
    {
        Add("Old/Record/01 One.mp3", "The Band", "The Record", "One");
        _db.SetAlbumReview("Old/Record", 3, null, 1);

        LibraryRefiler.FollowFolders(_db, _root, new(StringComparer.OrdinalIgnoreCase)
        {
            ["Old/Record"] = ["A/One", "B/Two"],
        });

        Assert.Equal(3, _db.GetAlbumMeta("Old/Record").Rating);
    }

    /// <summary>An album whose bytes are all in the cloud has no folder on this disk, and it is
    /// re-filed in the mirror the same way as a local one. Its row must still follow, or the rating and
    /// the release are left on a folder that has none of its tracks.</summary>
    [Fact]
    public void A_record_with_no_folder_on_this_disk_still_follows_in_the_mirror()
    {
        _db.SetAlbumReview("Cloud/Record", 5, null, 1);
        _db.SetAlbumPressing("Cloud/Record", "musicbrainz", "abc");

        LibraryRefiler.FollowFolders(_db, _root, new(StringComparer.OrdinalIgnoreCase)
        {
            ["Cloud/Record"] = ["The Band/The Record"],
        });

        var landed = _db.GetAlbumMeta("The Band/The Record");
        Assert.Equal(5, landed.Rating);
        Assert.Equal("abc", landed.ReleaseId);
    }
}
