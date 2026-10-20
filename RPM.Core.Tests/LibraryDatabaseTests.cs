using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using Microsoft.Data.Sqlite;

namespace RPM.Core.Tests;

public class LibraryDatabaseTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryDatabaseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdlib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LibraryTrack Track(string relativePath, string? artist = null, string? title = null, string? album = null)
    {
        var slash = relativePath.LastIndexOf('/');
        return new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
            SizeBytes = 100,
            ModifiedUtcTicks = 1,
            Artist = artist,
            Title = title,
            Album = album,
        };
    }

    /// <summary>A counted play at a given time. The listen log is the only way to change the play
    /// totals, so a test that needs a play writes a listen (see <see cref="ListenLogTests"/>).</summary>
    private void Played(string relativePath, long atUtcTicks)
    {
        var when = new DateTimeOffset(new DateTime(atUtcTicks, DateTimeKind.Utc));
        _db.NoteListen(ListenEntry.At(when, relativePath, heardSeconds: 200, lengthSeconds: 200)!);
    }

    /// <summary>
    /// A database written by a newer build is refused, not opened and marked older. Marking it older
    /// would make the newer build run its steps again, and some of them rewrite data.
    /// </summary>
    [Fact]
    public void A_database_from_a_newer_build_is_refused_and_left_as_it_was()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdlib-newer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            using (new LibraryDatabase(MirrorLocation.InFolder(root))) { }

            long current;
            using (var raw = new SqliteConnection($"Data Source={MirrorLocation.InFolder(root).DatabasePath};Pooling=False"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                current = Convert.ToInt64(cmd.ExecuteScalar());
                cmd.CommandText = $"PRAGMA user_version = {current + 1}";
                cmd.ExecuteNonQuery();
            }

            var refused = Assert.Throws<LibraryTooNewException>(() => new LibraryDatabase(MirrorLocation.InFolder(root)));
            Assert.Equal(current + 1, refused.Found);
            Assert.Equal(current, refused.Supported);

            using (var raw = new SqliteConnection($"Data Source={MirrorLocation.InFolder(root).DatabasePath};Pooling=False"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                Assert.Equal(current + 1, Convert.ToInt64(cmd.ExecuteScalar()));
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Creates_db_inside_hidden_data_folder()
    {
        Assert.True(File.Exists(Path.Combine(_root, ".rpm", "library.db")));
        Assert.True(LibraryDatabase.ExistsUnder(_root));
    }

    [Fact]
    public void Upsert_inserts_then_replaces_by_relative_path()
    {
        _db.Upsert(Track("Daft Punk/Discovery/01 - One More Time.mp3", "Daft Punk", "One More Time"));
        _db.Upsert(Track("Daft Punk/Discovery/01 - One More Time.mp3", "Daft Punk", "One More Time (edit)"));

        var all = _db.Query();
        Assert.Single(all);
        Assert.Equal("One More Time (edit)", all[0].Title);
        Assert.Equal(1, _db.Count);
    }

    [Fact]
    public void FindAlbumFolder_returns_the_folder_an_album_already_lives_in()
    {
        _db.UpsertMany(new[]
        {
            Track("Pink Floyd/The Wall/1-01 - In the Flesh.flac", "Pink Floyd", album: "The Wall") with { AlbumArtist = "Pink Floyd" },
            Track("Pink Floyd/The Wall/1-02 - The Thin Ice.flac", "Pink Floyd", album: "The Wall") with { AlbumArtist = "Pink Floyd" },
        });

        Assert.Equal("Pink Floyd/The Wall", _db.FindAlbumFolder("The Wall", "Pink Floyd"));
    }

    [Fact]
    public void FindAlbumFolder_matches_across_disc_decorations()
    {
        // Disc 1 is in the library under an album with a "(Disc 1)" suffix; disc 2 arrives as plain
        // "The Wall" and must still find it.
        _db.Upsert(Track("Pink Floyd/The Wall (Disc 1)/01 - In the Flesh.flac", "Pink Floyd", album: "The Wall (Disc 1)")
            with { AlbumArtist = "Pink Floyd" });

        Assert.Equal("Pink Floyd/The Wall (Disc 1)", _db.FindAlbumFolder("The Wall", "Pink Floyd"));
    }

    [Fact]
    public void FindAlbumFolder_is_null_when_the_album_is_not_in_the_library()
        => Assert.Null(_db.FindAlbumFolder("Nowhere", "Nobody"));

    [Fact]
    public void FindAlbumFolder_rules_out_a_different_album_artist()
    {
        _db.Upsert(Track("Queen/Greatest Hits/01 - x.flac", "Queen", album: "Greatest Hits") with { AlbumArtist = "Queen" });
        Assert.Null(_db.FindAlbumFolder("Greatest Hits", "ABBA"));
    }

    [Fact]
    public void Query_orders_by_relative_path()
    {
        _db.UpsertMany(new[]
        {
            Track("Zz/b.mp3"),
            Track("Aa/a.mp3"),
            Track("loose.mp3"),
        });

        var all = _db.Query();
        Assert.Equal(new[] { "Aa/a.mp3", "loose.mp3", "Zz/b.mp3" }, all.Select(t => t.RelativePath));
    }

    [Fact]
    public void Search_matches_name_artist_title_album_case_insensitive()
    {
        _db.UpsertMany(new[]
        {
            Track("a/one.mp3", artist: "Daft Punk", title: "Aerodynamic", album: "Discovery"),
            Track("b/two.mp3", artist: "Justice", title: "Genesis", album: "Cross"),
            Track("c/aerosmith_file.mp3"),
        });

        Assert.Equal(2, _db.Query("aero").Count);          // title + file name
        Assert.Single(_db.Query("JUSTICE"));               // artist, case-insensitive
        Assert.Single(_db.Query("discovery"));             // album
        Assert.Empty(_db.Query("nothing-matches"));
    }

    [Fact]
    public void Search_escapes_like_wildcards()
    {
        _db.Upsert(Track("a/100%.mp3", title: "100%"));
        _db.Upsert(Track("a/other.mp3", title: "other"));

        Assert.Single(_db.Query("100%"));
        Assert.Empty(_db.Query("%o%")); // literal, not wildcard
    }

    [Fact]
    public void FolderTrackCounts_groups_direct_tracks_per_folder()
    {
        _db.UpsertMany(new[]
        {
            Track("Daft Punk/Discovery/01.mp3"),
            Track("Daft Punk/Discovery/02.mp3"),
            Track("Daft Punk/loose.mp3"),
            Track("root-track.mp3"),
        });

        var counts = _db.QueryFolderTrackCounts();
        Assert.Equal(new[] { ("", 1), ("Daft Punk", 1), ("Daft Punk/Discovery", 2) }, counts);
    }

    [Fact]
    public void FolderTrackCounts_honours_search()
    {
        _db.UpsertMany(new[]
        {
            Track("a/one.mp3", artist: "Daft Punk"),
            Track("a/two.mp3", artist: "Justice"),
            Track("b/three.mp3", artist: "Justice"),
        });

        var counts = _db.QueryFolderTrackCounts("justice");
        Assert.Equal(new[] { ("a", 1), ("b", 1) }, counts);
    }

    /// <summary>The total running time under the library's heading. Tracks that have never been read
    /// have a zero duration and must add nothing, not break the sum.</summary>
    [Fact]
    public void TotalDuration_sums_the_collection_and_honours_search()
    {
        _db.UpsertMany(new[]
        {
            Duration(Track("a/one.mp3", artist: "Daft Punk"), 120),
            Duration(Track("a/two.mp3", artist: "Justice"), 200.5),
            Duration(Track("b/three.mp3", artist: "Justice"), 0),
        });

        Assert.Equal(320.5, _db.QueryTotalDurationSeconds(), 3);
        Assert.Equal(200.5, _db.QueryTotalDurationSeconds("justice"), 3);
    }

    [Fact]
    public void TotalDuration_of_an_empty_library_is_zero()
        => Assert.Equal(0, _db.QueryTotalDurationSeconds());

    private static LibraryTrack Duration(LibraryTrack track, double seconds)
        => track with { DurationSeconds = seconds };

    /// <summary>The wall's headings come from the tags, not from the path. A band whose name is not a
    /// valid file name is stored under a sanitised spelling, and the interface must not show that
    /// spelling.</summary>
    [Fact]
    public void AlbumFacts_name_each_folder_from_its_tags()
    {
        _db.UpsertMany(new[]
        {
            Track("AC_DC/Back in Black/01.mp3", artist: "AC/DC", album: "Back in Black"),
            Track("AC_DC/Back in Black/02.mp3", artist: "AC/DC", album: "Back in Black"),
        });

        var facts = Assert.Single(_db.QueryAlbumFacts());
        Assert.Equal("AC_DC/Back in Black", facts.Folder);
        Assert.Equal("Back in Black", facts.Album);
        Assert.Equal("AC/DC", facts.AlbumArtist);
    }

    [Fact]
    public void AlbumFacts_fall_back_to_null_so_the_caller_can_use_the_folder()
    {
        _db.UpsertMany(new[] { Track("Some Folder/01.mp3") });

        var facts = Assert.Single(_db.QueryAlbumFacts());
        Assert.Null(facts.Album);
        Assert.Null(facts.AlbumArtist);
    }

    /// <summary>Album artist falls back to the track artist, the same rule the naming template uses when
    /// it decides the folder these rows are grouped by.</summary>
    [Fact]
    public void AlbumFacts_use_the_track_artist_when_there_is_no_album_artist()
    {
        _db.UpsertMany(new[] { Track("x/01.mp3", artist: "Justice", album: "Cross") });

        var facts = Assert.Single(_db.QueryAlbumFacts());
        Assert.Equal("Cross", facts.Album);
        Assert.Equal("Justice", facts.AlbumArtist);
    }

    [Fact]
    public void AlbumFacts_honour_search()
    {
        _db.UpsertMany(new[]
        {
            Track("a/one.mp3", artist: "Daft Punk", album: "Discovery"),
            Track("b/two.mp3", artist: "Justice", album: "Cross"),
        });

        var facts = Assert.Single(_db.QueryAlbumFacts("justice"));
        Assert.Equal("b", facts.Folder);
    }

    /// <summary>An album's plays are the sum of its tracks' plays, and its added date is the NEWEST of
    /// them, so an album that is still being imported does not sort below one finished
    /// yesterday.</summary>
    [Fact]
    public void AlbumFacts_add_up_plays_and_take_the_newest_arrival()
    {
        _db.UpsertMany(new[] { Track("a/one.mp3"), Track("a/two.mp3") });
        Played("a/one.mp3", 100);
        Played("a/one.mp3", 200);
        Played("a/two.mp3", 300);

        var facts = Assert.Single(_db.QueryAlbumFacts());
        Assert.Equal(3, facts.Plays);
        Assert.True(facts.AddedAt > 0);
    }

    /// <summary>The year is a count per year, so the "most of its tracks agree" rule can be applied by
    /// AlbumSort, the same rule the phone's wall uses.</summary>
    [Fact]
    public void AlbumYearCounts_group_years_per_folder_and_skip_the_untagged()
    {
        _db.UpsertMany(new[]
        {
            Year(Track("a/one.mp3"), 1979),
            Year(Track("a/two.mp3"), 1979),
            Year(Track("a/three.mp3"), 2003),
            Track("a/four.mp3"),   // untagged: no year
        });

        var counts = _db.QueryAlbumYearCounts().OrderBy(c => c.Year).ToList();
        Assert.Equal(new[] { ("a", 1979, 2), ("a", 2003, 1) }, counts);
    }

    private static LibraryTrack Year(LibraryTrack track, uint year) => track with { Year = year };

    /// <summary>The genre is a count per genre for the same reason as the year: an album's genre is the
    /// one most of its tracks have, and a compilation may have no single genre. Tracks with no genre
    /// tag are skipped rather than counted as a blank genre.</summary>
    [Fact]
    public void AlbumGenreCounts_group_genres_per_folder_and_skip_the_untagged()
    {
        _db.UpsertMany(new[]
        {
            Genre(Track("a/one.mp3"), "Jazz"),
            Genre(Track("a/two.mp3"), "Jazz"),
            Genre(Track("a/three.mp3"), "Blues"),
            Genre(Track("a/four.mp3"), ""),   // tagged with nothing at all
            Track("a/five.mp3"),              // never tagged
            Genre(Track("b/one.mp3"), "Punk"),
        });

        var counts = _db.QueryAlbumGenreCounts()
            .OrderBy(c => c.Folder, StringComparer.Ordinal)
            .ThenBy(c => c.Genre, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { ("a", "Blues", 1), ("a", "Jazz", 2), ("b", "Punk", 1) }, counts);
    }

    private static LibraryTrack Genre(LibraryTrack track, string genre) => track with { Genre = genre };

    /// <summary>The lines in a track list that are not tracks, counted before any track is read. The
    /// album list uses this to reserve space. Discs are counted whatever their number: whether one
    /// disc needs a header is decided by the caller.</summary>
    [Fact]
    public void AlbumHeaderLines_count_the_discs_a_record_is_spread_over()
    {
        _db.UpsertMany(new[]
        {
            Disc(Track("set/1-01.mp3"), 1),
            Disc(Track("set/1-02.mp3"), 1),
            Disc(Track("set/2-01.mp3"), 2),
            Track("single/01.mp3"),   // no disc tagged at all
        });

        var rows = _db.QueryAlbumHeaderLines().OrderBy(r => r.Folder, StringComparer.Ordinal).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("set", 2, 0), (rows[0].Folder, rows[0].Discs, rows[0].Works));
        Assert.Equal(("single", 0, 0), (rows[1].Folder, rows[1].Discs, rows[1].Works));
    }

    /// <summary>Works are counted only on albums marked as classical, because only there does the
    /// track list show a header for each work.</summary>
    [Fact]
    public void AlbumHeaderLines_count_works_on_a_classical_record_and_nowhere_else()
    {
        _db.UpsertMany(new[]
        {
            Work(Track("mozart/01.mp3"), "Sonata No. 1"),
            Work(Track("mozart/02.mp3"), "Sonata No. 1"),
            Work(Track("mozart/03.mp3"), "Sonata No. 2"),
            Work(Track("rock/01.mp3"), "Some Suite"),   // tagged, but not marked classical
        });
        _db.SetAlbumClassical("mozart", true);

        var rows = _db.QueryAlbumHeaderLines().ToDictionary(r => r.Folder, r => r.Works);
        Assert.Equal(2, rows["mozart"]);
        Assert.Equal(0, rows["rock"]);
    }

    /// <summary>It follows the search, like every other query over the mirror: the album list
    /// reserves space for what is on screen.</summary>
    [Fact]
    public void AlbumHeaderLines_follow_the_search()
    {
        _db.UpsertMany(new[]
        {
            Disc(Track("set/1-01.mp3", album: "Gold"), 1),
            Disc(Track("set/2-01.mp3", album: "Gold"), 2),
            Track("other/01.mp3", album: "Silver"),
        });

        var row = Assert.Single(_db.QueryAlbumHeaderLines("gold"));
        Assert.Equal(("set", 2), (row.Folder, row.Discs));
    }

    private static LibraryTrack Disc(LibraryTrack track, uint disc) => track with { DiscNumber = disc };

    private static LibraryTrack Work(LibraryTrack track, string work) => track with { Work = work };

    /// <summary>A retag is not a new track. The values the row has that the file does not (when it was
    /// added, how often it was played) must be kept, or every correction resets the library's
    /// history.</summary>
    [Fact]
    public void Moving_a_row_keeps_its_plays_and_the_day_it_arrived()
    {
        _db.Upsert(Track("Old/01.mp3", artist: "Justice", album: "Cros"));
        Played("Old/01.mp3", 42);
        var before = Assert.Single(_db.Query()).AddedUtcTicks;

        _db.MoveMany(new[]
        {
            ("Old/01.mp3", Track("New/01.mp3", artist: "Justice", album: "Cross")),
        });

        var after = Assert.Single(_db.Query());
        Assert.Equal("New/01.mp3", after.RelativePath);
        Assert.Equal("Cross", after.Album);
        Assert.Equal(1, after.Plays);
        Assert.Equal(before, after.AddedUtcTicks);
    }

    /// <summary>The album editor's release type reaches the row through a move, as every edit does, so a
    /// move writes it: set, changed and cleared.</summary>
    [Fact]
    public void Moving_a_row_writes_its_release_type()
    {
        _db.Upsert(Track("a/01.mp3", album: "Cross"));

        _db.MoveMany([("a/01.mp3", Track("a/01.mp3", album: "Cross") with { ReleaseType = ReleaseType.Ep })]);
        Assert.Equal(ReleaseType.Ep, Assert.Single(_db.Query()).ReleaseType);

        _db.MoveMany([("a/01.mp3", Track("a/01.mp3", album: "Cross") with { ReleaseType = ReleaseType.Live })]);
        Assert.Equal(ReleaseType.Live, Assert.Single(_db.Query()).ReleaseType);

        _db.MoveMany([("a/01.mp3", Track("a/01.mp3", album: "Cross") with { ReleaseType = null })]);
        Assert.Null(Assert.Single(_db.Query()).ReleaseType);
    }

    /// <summary>An upsert (what import and sync do with a file they have just read) must not overwrite
    /// them either: a caller that has only read the file does not know these values.</summary>
    [Fact]
    public void Upserting_over_a_row_leaves_its_plays_and_arrival_alone()
    {
        _db.Upsert(Track("a/01.mp3", album: "Cros"));
        Played("a/01.mp3", 42);
        var before = Assert.Single(_db.Query()).AddedUtcTicks;

        _db.Upsert(Track("a/01.mp3", album: "Cross"));

        var after = Assert.Single(_db.Query());
        Assert.Equal("Cross", after.Album);
        Assert.Equal(1, after.Plays);
        Assert.Equal(before, after.AddedUtcTicks);
    }

    [Fact]
    public void A_new_row_is_stamped_with_the_day_it_arrived()
    {
        _db.Upsert(Track("a/01.mp3"));

        var row = Assert.Single(_db.Query());
        Assert.InRange(row.AddedUtcTicks, DateTime.UtcNow.AddMinutes(-1).Ticks, DateTime.UtcNow.Ticks);
        Assert.Equal(0, row.Plays);
    }

    [Fact]
    public void FolderPage_windows_in_file_name_order()
    {
        _db.UpsertMany(new[]
        {
            Track("Album/03.mp3"),
            Track("Album/01.mp3"),
            Track("Album/02.mp3"),
            Track("Album/04.mp3"),
            Track("Other/x.mp3"),
        });

        var page = _db.QueryFolderPage("Album", offset: 1, limit: 2);
        Assert.Equal(new[] { "02.mp3", "03.mp3" }, page.Select(t => t.FileName));
    }

    [Fact]
    public void FolderPage_root_folder_and_search()
    {
        _db.UpsertMany(new[]
        {
            Track("rootsong.mp3", title: "Aerodynamic"),
            Track("other.mp3", title: "Genesis"),
            Track("a/nested.mp3", title: "Aerodynamic"),
        });

        var all = _db.QueryFolderPage("", 0, 10);
        Assert.Equal(2, all.Count);

        var searched = _db.QueryFolderPage("", 0, 10, "aero");
        Assert.Single(searched);
        Assert.Equal("rootsong.mp3", searched[0].FileName);
    }

    [Fact]
    public void Remove_drops_the_row()
    {
        _db.Upsert(Track("a/x.mp3"));
        _db.Remove("a/x.mp3");
        Assert.Empty(_db.Query());
    }

    [Fact]
    public void Snapshot_returns_size_mtime_and_id_by_path()
    {
        _db.Upsert(Track("a/x.mp3") with { SizeBytes = 42, ModifiedUtcTicks = 7, RpmId = "gid-1" });
        var snap = _db.Snapshot()["a/x.mp3"];
        Assert.Equal(42L, snap.SizeBytes);
        Assert.Equal(7L, snap.ModifiedUtcTicks);
        Assert.Equal("gid-1", snap.RpmId);
        Assert.Equal(CloudState.Local, snap.CloudState);
    }

    [Fact]
    public void Move_repoints_the_row_and_keeps_its_identity()
    {
        _db.Upsert(Track("a/x.mp3") with { RpmId = "gid-1" });
        long before = _db.Query()[0].Id;

        _db.Move("a/x.mp3", Track("b/renamed.mp3") with { RpmId = "gid-1" });

        var row = Assert.Single(_db.Query());
        Assert.Equal(before, row.Id); // same row: history and statistics are keyed by this id
        Assert.Equal("b/renamed.mp3", row.RelativePath);
        Assert.Equal("b", row.Folder);
        Assert.Equal("renamed.mp3", row.FileName);
    }

    [Fact]
    public void DuplicateRpmIds_finds_ids_held_by_more_than_one_row()
    {
        _db.Upsert(Track("a/x.mp3") with { RpmId = "shared" });
        _db.Upsert(Track("a/copy.mp3") with { RpmId = "shared" });
        _db.Upsert(Track("a/own.mp3") with { RpmId = "unique" });
        _db.Upsert(Track("a/none.mp3"));

        var dupes = _db.DuplicateRpmIds();

        Assert.Equal(new[] { "shared" }, dupes);
    }

    [Fact]
    public void A_mirror_that_still_says_grooves_id_is_renamed_in_place()
    {
        // A v2 mirror, which names the id column grooves_id. The later columns are added by the same
        // migration. The id column holds the ids written into the user's own files, so it is
        // renamed, never rebuilt.
        var root = Path.Combine(Path.GetTempPath(), $"mdren-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, LibraryDatabase.DataFolderName));
        using (var legacy = new SqliteConnection($"Data Source={MirrorLocation.InFolder(root).DatabasePath}"))
        {
            legacy.Open();
            using var cmd = legacy.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE tracks (
                    id INTEGER PRIMARY KEY,
                    relative_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    folder TEXT NOT NULL COLLATE NOCASE,
                    file_name TEXT NOT NULL COLLATE NOCASE,
                    extension TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    modified_utc_ticks INTEGER NOT NULL,
                    duration_seconds REAL NOT NULL DEFAULT 0,
                    title TEXT COLLATE NOCASE,
                    artist TEXT COLLATE NOCASE,
                    album_artist TEXT COLLATE NOCASE,
                    album TEXT COLLATE NOCASE,
                    genre TEXT COLLATE NOCASE,
                    track_number INTEGER NOT NULL DEFAULT 0,
                    disc_number INTEGER NOT NULL DEFAULT 0,
                    year INTEGER NOT NULL DEFAULT 0,
                    grooves_id TEXT
                );
                CREATE INDEX idx_tracks_grooves_id ON tracks(grooves_id);
                CREATE TABLE pl_node (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    parent_id INTEGER,
                    kind TEXT NOT NULL,
                    name TEXT NOT NULL,
                    sort INTEGER NOT NULL DEFAULT 0,
                    is_now_playing INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE pl_item (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    playlist_id INTEGER NOT NULL,
                    position INTEGER NOT NULL DEFAULT 0,
                    item_kind TEXT NOT NULL,
                    grooves_id TEXT,
                    rel_path TEXT,
                    ref_playlist_id INTEGER
                );
                INSERT INTO tracks (relative_path, folder, file_name, extension, size_bytes,
                                    modified_utc_ticks, title, grooves_id)
                VALUES ('a/x.mp3', 'a', 'x.mp3', '.mp3', 10, 20, 'Kept', 'gid-kept');
                INSERT INTO pl_node (kind, name) VALUES ('playlist', 'Mine');
                INSERT INTO pl_item (playlist_id, position, item_kind, grooves_id, rel_path)
                VALUES (1, 0, 'song', 'gid-kept', 'a/x.mp3');
                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();
        }

        try
        {
            using var db = new LibraryDatabase(MirrorLocation.InFolder(root));

            var row = Assert.Single(db.Query());
            Assert.Equal("Kept", row.Title);
            Assert.Equal("gid-kept", row.RpmId);    // the same id, under the new column name

            // The playlist entry still points at the same track.
            var playlist = db.GetPlaylistNodes().Single(n => n.Name == "Mine");
            Assert.Equal(["a/x.mp3"], db.FlattenPlaylistTracks(playlist.Id).Select(t => t.RelativePath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_library_folder_still_called_dot_grooves_is_carried_over()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdfold-{Guid.NewGuid():N}");
        var legacyDir = Path.Combine(root, ".grooves");
        Directory.CreateDirectory(legacyDir);
        // A rip staged inside it. The folder's contents must move with it, or the pending work is
        // left in a place the scanner never looks.
        Directory.CreateDirectory(Path.Combine(legacyDir, "rip-1"));
        using (var seed = new SqliteConnection($"Data Source={Path.Combine(legacyDir, "library.db")}"))
        {
            seed.Open();
            using var cmd = seed.CreateCommand();
            cmd.CommandText = "CREATE TABLE marker (x INTEGER)";
            cmd.ExecuteNonQuery();
        }

        // Disposing the connection above returns it to Microsoft.Data.Sqlite's pool, which keeps the
        // file open. On Windows that stops Directory.Move, and the carry-over then takes its
        // documented "a locked file leaves the old folder where it is" path. Without clearing the
        // pools, the test would check that fallback on Windows instead of the carry-over.
        SqliteConnection.ClearAllPools();

        try
        {
            Assert.True(LibraryDatabase.ExistsUnder(root));  // it is a library before it is opened

            using (var db = new LibraryDatabase(MirrorLocation.InFolder(root))) { }

            Assert.False(Directory.Exists(legacyDir));
            Assert.True(File.Exists(MirrorLocation.InFolder(root).DatabasePath));
            Assert.True(Directory.Exists(Path.Combine(root, ".rpm", "rip-1")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_mirror_from_before_the_id_column_is_migrated_in_place()
    {
        // A v1 mirror: the table with no id column, and a row in it.
        var root = Path.Combine(Path.GetTempPath(), $"mdmig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, LibraryDatabase.DataFolderName));
        using (var legacy = new SqliteConnection($"Data Source={MirrorLocation.InFolder(root).DatabasePath}"))
        {
            legacy.Open();
            using var cmd = legacy.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE tracks (
                    id INTEGER PRIMARY KEY,
                    relative_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    folder TEXT NOT NULL COLLATE NOCASE,
                    file_name TEXT NOT NULL COLLATE NOCASE,
                    extension TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    modified_utc_ticks INTEGER NOT NULL,
                    duration_seconds REAL NOT NULL DEFAULT 0,
                    title TEXT COLLATE NOCASE,
                    artist TEXT COLLATE NOCASE,
                    album_artist TEXT COLLATE NOCASE,
                    album TEXT COLLATE NOCASE,
                    genre TEXT COLLATE NOCASE,
                    track_number INTEGER NOT NULL DEFAULT 0,
                    disc_number INTEGER NOT NULL DEFAULT 0,
                    year INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO tracks (relative_path, folder, file_name, extension, size_bytes,
                                    modified_utc_ticks, title)
                VALUES ('a/x.mp3', 'a', 'x.mp3', '.mp3', 10, 20, 'Old Row');
                """;
            cmd.ExecuteNonQuery();
        }

        try
        {
            using var db = new LibraryDatabase(MirrorLocation.InFolder(root)); // opening it runs the migration

            var row = Assert.Single(db.Query());
            Assert.Equal("Old Row", row.Title); // the row is kept: the mirror is never rebuilt
            Assert.Null(row.RpmId);         // …and sync will write an id for it
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A track with no length in a mirror older than v19 was read with TagLib only, and TagLib throws
    /// on some container layouts. Its stream values were stored anyway, and stored values are never
    /// read again. The v19 migration clears those rows so the reader reads them again, and leaves
    /// every row that has a length unchanged.
    /// </summary>
    [Fact]
    public void A_row_with_no_length_is_offered_to_the_reader_again()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdmig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        using (var before = new LibraryDatabase(MirrorLocation.InFolder(root)))
        {
            before.Upsert(Track("a/dolby.m4a") with { Channels = 2, SampleRate = 44100 });
            before.Upsert(Track("a/plain.mp3") with { DurationSeconds = 212, Channels = 2, SampleRate = 44100 });
        }

        // Set the mirror back to v18.
        using (var raw = new SqliteConnection($"Data Source={MirrorLocation.InFolder(root).DatabasePath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 18";
            cmd.ExecuteNonQuery();
        }

        try
        {
            using var db = new LibraryDatabase(MirrorLocation.InFolder(root));   // opening it runs the migration

            var rows = db.Query().ToDictionary(r => r.FileName);
            Assert.Null(rows["dolby.m4a"].Channels);      // null = never read, so sync reads it again
            Assert.Equal(2, rows["plain.mp3"].Channels);  // already read, so left unchanged
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FromTrackInfo_builds_relative_row()
    {
        var file = Path.Combine(_root, "Artist", "Album", "01 - Song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "xx");

        var info = new TrackInfo { FilePath = file, Extension = ".mp3", Artist = "Artist", Title = "Song" };
        var track = LibraryDatabase.FromTrackInfo(info, _root);

        Assert.Equal("Artist/Album/01 - Song.mp3", track.RelativePath);
        Assert.Equal("Artist/Album", track.Folder);
        Assert.Equal("01 - Song.mp3", track.FileName);
        Assert.Equal(2, track.SizeBytes);
        Assert.True(track.ModifiedUtcTicks > 0);
    }

    [Fact]
    public void ToTrackInfo_rebuilds_absolute_path()
    {
        var info = Track("Artist/Album/01 - Song.mp3", "Artist", "Song").ToTrackInfo(_root);
        Assert.Equal(Path.Combine(_root, "Artist", "Album", "01 - Song.mp3"), info.FilePath);
        Assert.Equal(MetadataSource.Tags, info.Source);
    }

    // ── whether an album is classical (v26) ──

    /// <summary>An album with no answer reads back as null, which is shown as a normal album. The
    /// user's answer, yes or no, is stored.</summary>
    [Fact]
    public void An_album_can_be_told_it_is_classical_or_told_it_is_not()
    {
        Assert.Null(_db.GetAlbumMeta("Mozart/Keyboard Works").Classical);

        _db.SetAlbumClassical("Mozart/Keyboard Works", true);
        Assert.True(_db.GetAlbumMeta("Mozart/Keyboard Works").Classical);

        _db.SetAlbumClassical("Mozart/Keyboard Works", false);
        Assert.False(_db.GetAlbumMeta("Mozart/Keyboard Works").Classical);

        // Back to no answer, which is a normal album.
        _db.SetAlbumClassical("Mozart/Keyboard Works", null);
        Assert.Null(_db.GetAlbumMeta("Mozart/Keyboard Works").Classical);
    }

    /// <summary>Setting the flag keeps the rest of the row: marking an album as classical does not
    /// remove its review.</summary>
    [Fact]
    public void The_classical_flag_sits_beside_the_rest_of_the_album()
    {
        _db.SetAlbumReview("Bach/Cantatas", 5, "Wonderful.", 99);
        _db.SetAlbumClassical("Bach/Cantatas", true);

        var meta = _db.GetAlbumMeta("Bach/Cantatas");
        Assert.True(meta.Classical);
        Assert.Equal(5, meta.Rating);
        Assert.Equal("Wonderful.", meta.ReviewMarkdown);

        Assert.True(_db.GetAlbumMetaMany(["Bach/Cantatas"])["Bach/Cantatas"].Classical);
    }

    /// <summary>The app does not decide by itself that an album is classical. When a mirror older than
    /// v34 is opened, every album in it is set to not classical, whatever it had. The user's own "yes"
    /// marks are reset too, because they cannot be told apart from marks set by the automatic rule.</summary>
    [Fact]
    public void An_older_mirror_comes_back_with_every_album_not_classical()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdclassical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            using (var db = new LibraryDatabase(MirrorLocation.InFolder(root)))
            {
                db.SetAlbumClassical("Mozart/Sonatas", true);
                db.SetAlbumClassical("Nirvana/Nevermind", false);
                db.SetAlbumReview("Bach/Cantatas", 5, "Wonderful.", 99);   // no answer: null
            }

            // Set the file back to the version before the backfill, so opening it runs the step.
            using (var seed = new SqliteConnection($"Data Source={MirrorLocation.InFolder(root).DatabasePath}"))
            {
                seed.Open();
                using var cmd = seed.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 33";
                cmd.ExecuteNonQuery();
            }

            using (var db = new LibraryDatabase(MirrorLocation.InFolder(root)))
            {
                Assert.False(db.GetAlbumMeta("Mozart/Sonatas").Classical);
                Assert.False(db.GetAlbumMeta("Nirvana/Nevermind").Classical);
                Assert.False(db.GetAlbumMeta("Bach/Cantatas").Classical);

                // The rest of the row is unchanged.
                Assert.Equal("Wonderful.", db.GetAlbumMeta("Bach/Cantatas").ReviewMarkdown);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── which composition a track is part of (v25) ──

    /// <summary>The work, the movement and the composer are kept through the mirror. They are columns
    /// so that an album can be grouped by its works, and a shelf sorted by composer, without opening
    /// any file.</summary>
    [Fact]
    public void The_work_a_track_belongs_to_is_kept()
    {
        _db.Upsert(Track("m/01.flac") with
        {
            Work = "Sonata In C Major, KV. 330",
            MovementName = "Allegretto",
            MovementNumber = 3,
            Composer = "Wolfgang Amadeus Mozart",
        });

        var row = Assert.Single(_db.Query());
        Assert.Equal("Sonata In C Major, KV. 330", row.Work);
        Assert.Equal("Allegretto", row.MovementName);
        Assert.Equal(3, row.MovementNumber);
        Assert.Equal("Wolfgang Amadeus Mozart", row.Composer);

        var info = row.ToTrackInfo(_root);
        Assert.Equal("Sonata In C Major, KV. 330", info.Work);
        Assert.Equal("Allegretto", info.MovementName);
        Assert.Equal(3u, info.MovementNumber);
        Assert.Equal("Wolfgang Amadeus Mozart", info.Composers);
    }

    /// <summary>A row built from a file has a movement number even when the file is part of no work:
    /// zero means "read, no work", and null means "never read". Without this, every track that is not
    /// classical would be read again on every sync.</summary>
    [Fact]
    public void A_track_in_no_work_still_settles_its_movement_number()
    {
        var file = Path.Combine(_root, "Artist", "Album", "01 - Song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "xx");

        var track = LibraryDatabase.FromTrackInfo(
            new TrackInfo { FilePath = file, Extension = ".mp3", Title = "Song" }, _root);

        Assert.Equal(0, track.MovementNumber);
        Assert.Null(track.Work);

        _db.Upsert(track);
        Assert.Equal(0, _db.Snapshot()["Artist/Album/01 - Song.mp3"].MovementNumber);
    }

    /// <summary>A mirror older than the work columns reads them back as null ("never read"), so the tag
    /// reader reads its rows once, and only once.</summary>
    [Fact]
    public void A_mirror_from_before_the_work_columns_asks_to_be_read_once()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdwork-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".rpm"));

        using (var seed = new SqliteConnection($"Data Source={Path.Combine(root, ".rpm", "library.db")}"))
        {
            seed.Open();
            using var cmd = seed.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE tracks (
                    id INTEGER PRIMARY KEY,
                    relative_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    folder TEXT NOT NULL COLLATE NOCASE,
                    file_name TEXT NOT NULL COLLATE NOCASE,
                    extension TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    modified_utc_ticks INTEGER NOT NULL,
                    duration_seconds REAL NOT NULL DEFAULT 0,
                    title TEXT COLLATE NOCASE,
                    artist TEXT COLLATE NOCASE,
                    album_artist TEXT COLLATE NOCASE,
                    album TEXT COLLATE NOCASE,
                    genre TEXT COLLATE NOCASE,
                    advisory INTEGER,
                    track_number INTEGER NOT NULL DEFAULT 0,
                    disc_number INTEGER NOT NULL DEFAULT 0,
                    disc_count INTEGER NOT NULL DEFAULT 0,
                    disc_subtitle TEXT,
                    year INTEGER NOT NULL DEFAULT 0,
                    channels INTEGER,
                    sample_rate INTEGER,
                    bits_per_sample INTEGER,
                    bitrate_kbps INTEGER,
                    codec_description TEXT,
                    rpm_id TEXT,
                    content_hash TEXT,
                    cloud_state INTEGER NOT NULL DEFAULT 0,
                    cloud_selected INTEGER NOT NULL DEFAULT 0,
                    cloud_pinned INTEGER NOT NULL DEFAULT 0,
                    canonical_size_bytes INTEGER NOT NULL DEFAULT 0,
                    cloud_synced_utc_ticks INTEGER NOT NULL DEFAULT 0,
                    cloud_error TEXT,
                    cloud_attempts INTEGER NOT NULL DEFAULT 0,
                    cloud_next_attempt_utc_ticks INTEGER NOT NULL DEFAULT 0,
                    added_utc_ticks INTEGER NOT NULL DEFAULT 0,
                    plays INTEGER NOT NULL DEFAULT 0,
                    last_played_utc_ticks INTEGER NOT NULL DEFAULT 0,
                    renditions_utc_ticks INTEGER NOT NULL DEFAULT 0,
                    cloud_adopted INTEGER NOT NULL DEFAULT 0,
                    metadata_edited_utc_ticks INTEGER NOT NULL DEFAULT 0,
                    cloud_updated_at INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO tracks (relative_path, folder, file_name, extension, size_bytes,
                                    modified_utc_ticks, title, channels, advisory)
                VALUES ('a/x.mp3', 'a', 'x.mp3', '.mp3', 10, 20, 'Kept', 2, 0);
                PRAGMA user_version = 24;
                """;
            cmd.ExecuteNonQuery();
        }

        try
        {
            using var db = new LibraryDatabase(MirrorLocation.InFolder(root));

            var row = Assert.Single(db.Query());
            Assert.Equal("Kept", row.Title);        // the row itself is unchanged
            Assert.Null(row.Work);
            Assert.Null(row.MovementNumber);        // never read, so sync will read it

            Assert.Null(db.Snapshot()["a/x.mp3"].MovementNumber);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── ordering playlists by hand ──

    /// <summary>The order set by a drag is the order that is read back, even with the read's
    /// alphabetical tiebreak. If the positions were 1, 1, 1, the list would sort itself again the next
    /// time a playlist in it was renamed.</summary>
    [Fact]
    public void Reordering_nodes_writes_the_order_that_was_asked_for()
    {
        _db.CreatePlaylist(null, "Zebra");
        _db.CreatePlaylist(null, "Apple");
        _db.CreatePlaylist(null, "Mango");

        var byName = _db.GetPlaylistNodes().Where(n => !n.IsNowPlaying)
                        .ToDictionary(n => n.Name, n => n.Id);

        _db.ReorderPlaylistNodes(null, [byName["Mango"], byName["Apple"], byName["Zebra"]]);

        Assert.Equal(
            new[] { "Mango", "Apple", "Zebra" },
            _db.GetPlaylistNodes().Where(n => !n.IsNowPlaying).Select(n => n.Name).ToArray());
    }

    /// <summary>Now playing is not a user playlist and cannot be moved. Whatever order is set at the
    /// root, it stays first.</summary>
    [Fact]
    public void Now_playing_keeps_the_head_of_the_roots()
    {
        _db.CreatePlaylist(null, "Mine");

        var nodes = _db.GetPlaylistNodes();
        var mine = nodes.Single(n => n.Name == "Mine").Id;
        var queue = nodes.Single(n => n.IsNowPlaying).Id;

        _db.ReorderPlaylistNodes(null, [mine, queue]);

        Assert.True(_db.GetPlaylistNodes().First().IsNowPlaying);
    }

    /// <summary>A reorder applies only to one parent's children. A row in a folder is not renumbered by
    /// a drag among the roots, and is not moved out of its folder because it was not listed.</summary>
    [Fact]
    public void Reordering_one_parent_leaves_another_parents_children_alone()
    {
        _db.CreatePlaylistFolder(null, "Box");
        var box = _db.GetPlaylistNodes().Single(n => n.Name == "Box").Id;

        _db.CreatePlaylist(box, "Inner");
        _db.CreatePlaylist(null, "Outer");

        var outer = _db.GetPlaylistNodes().Single(n => n.Name == "Outer").Id;

        _db.ReorderPlaylistNodes(null, [outer, box]);

        var inner = _db.GetPlaylistNodes().Single(n => n.Name == "Inner");
        Assert.Equal(box, inner.ParentId);
    }

    /// <summary>Ids the caller did not list keep their order, after the listed ones. An out-of-date
    /// list must not reorder the rows it does not include.</summary>
    [Fact]
    public void Rows_the_reorder_did_not_mention_keep_their_order()
    {
        _db.CreatePlaylist(null, "One");
        _db.CreatePlaylist(null, "Two");
        _db.CreatePlaylist(null, "Three");

        var byName = _db.GetPlaylistNodes().Where(n => !n.IsNowPlaying)
                        .ToDictionary(n => n.Name, n => n.Id);

        _db.ReorderPlaylistNodes(null, [byName["Three"]]);

        Assert.Equal(
            new[] { "Three", "One", "Two" },
            _db.GetPlaylistNodes().Where(n => !n.IsNowPlaying).Select(n => n.Name).ToArray());
    }

    // ── counting: everything the collection lists, or only what is on this disk ───────────────────

    [Fact]
    public void Counted_anywhere_the_cloud_records_are_part_of_the_collection()
    {
        _db.UpsertMany([
            Track("Muse/Absolution/01.mp3", "Muse", "Apocalypse Please", "Absolution"),
            CloudOnly("Pixies/Doolittle/01.mp3", "Pixies", "Debaser", "Doolittle"),
            CloudOnly("Pixies/Doolittle/02.mp3", "Pixies", "Tame", "Doolittle"),
        ]);

        Assert.Equal(3, _db.CountTracks(TrackScope.Anywhere));
        Assert.Equal(2, _db.CountAlbumFolders(TrackScope.Anywhere));
    }

    [Fact]
    public void Counted_on_this_computer_only_the_tracks_with_a_file_here_are_counted()
    {
        _db.UpsertMany([
            Track("Muse/Absolution/01.mp3", "Muse", "Apocalypse Please", "Absolution"),
            CloudOnly("Pixies/Doolittle/01.mp3", "Pixies", "Debaser", "Doolittle"),
            CloudOnly("Pixies/Doolittle/02.mp3", "Pixies", "Tame", "Doolittle"),
        ]);

        Assert.Equal(1, _db.CountTracks(TrackScope.OnThisComputer));
        Assert.Equal(1, _db.CountAlbumFolders(TrackScope.OnThisComputer));
    }

    /// <summary>A machine after signing in and before importing anything: the account's catalogue is
    /// the whole mirror. The setup wizard must not count this as an import.</summary>
    [Fact]
    public void A_collection_of_nothing_but_cloud_records_holds_nothing_on_this_computer()
    {
        _db.UpsertMany([
            CloudOnly("Pixies/Doolittle/01.mp3", "Pixies", "Debaser", "Doolittle"),
            CloudOnly("Muse/Absolution/01.mp3", "Muse", "Apocalypse Please", "Absolution"),
        ]);

        Assert.Equal(2, _db.CountTracks(TrackScope.Anywhere));
        Assert.Equal(0, _db.CountTracks(TrackScope.OnThisComputer));
        Assert.Equal(0, _db.CountAlbumFolders(TrackScope.OnThisComputer));
    }

    private static LibraryTrack CloudOnly(
        string relativePath, string? artist = null, string? title = null, string? album = null)
        => Track(relativePath, artist, title, album) with
        {
            CloudState = RPM.Core.Cloud.CloudState.CloudOnly,
        };
}
