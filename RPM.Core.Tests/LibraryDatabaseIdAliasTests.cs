using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A song whose rpm id changes keeps its place in every playlist, and an id it had before still finds it.
/// See LibraryDatabase.IdAliases.
/// </summary>
public class LibraryDatabaseIdAliasTests : IDisposable
{
    private const string OldId = "11111111-1111-4111-8111-111111111111";
    private const string NewId = "22222222-2222-4222-8222-222222222222";
    private const string NewerId = "33333333-3333-4333-8333-333333333333";
    private const string OtherId = "44444444-4444-4444-8444-444444444444";

    private readonly string _root;
    private LibraryDatabase _db;

    public LibraryDatabaseIdAliasTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdalias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LibraryTrack Track(string relativePath, string rpmId)
    {
        var slash = relativePath.LastIndexOf('/');
        return new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
            RpmId = rpmId,
        };
    }

    private LibraryTrack Add(string relativePath, string rpmId)
    {
        _db.Upsert(Track(relativePath, rpmId));
        return _db.FindByRpmId(rpmId) ?? throw new InvalidOperationException("The track was not written.");
    }

    private long PlaylistWith(params (string RpmId, string RelPath)[] songs)
    {
        var playlist = _db.CreatePlaylist(null, "Mix");
        _db.AddSongsToPlaylist(playlist, songs.Select(s => ((string?)s.RpmId, s.RelPath)), AlreadyInPlaylist.AddAnother);
        return playlist;
    }

    private void Reopen()
    {
        _db.Dispose();
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    [Fact]
    public void A_song_that_takes_over_another_id_keeps_its_place_in_its_playlists()
    {
        var song = Add("ABBA/Arrival/02 - Dancing Queen.flac", OldId);
        var playlist = PlaylistWith((OldId, song.RelativePath));

        _db.SetRpmId(song.Id, NewId);

        Assert.Equal(NewId, Assert.Single(_db.GetPlaylistItems(playlist)).RpmId);
        Assert.Equal(NewId, _db.CurrentRpmId(OldId));
    }

    [Fact]
    public void Opening_the_library_repairs_an_entry_that_names_an_id_no_song_has()
    {
        // The entry an older build left: the song at its path has a new id, the entry still the old one.
        var song = Add("10cc/Bloody Tourists/01 - Dreadlock Holiday.flac", NewId);
        var playlist = PlaylistWith((OldId, song.RelativePath));

        Reopen();

        Assert.Equal(NewId, Assert.Single(_db.GetPlaylistItems(playlist)).RpmId);
        Assert.Equal(NewId, _db.CurrentRpmId(OldId));
    }

    [Fact]
    public void A_repaired_cloud_playlist_is_sent_up_once_more_and_keeps_what_it_last_sent()
    {
        const string cloudId = "55555555-5555-4555-8555-555555555555";
        var song = Add("Simply Red/Stars/01 - Something Got Me Started.flac", NewId);
        var playlist = PlaylistWith((OldId, song.RelativePath));
        _db.SetPlaylistInCloud(playlist, cloudId, 1);
        _db.NotePlaylistPublished(playlist, 1000, [NewId], PlaylistCloudState.MetaOf("Mix", []));

        Assert.Equal(1, _db.RepairPlaylistIds());

        var state = _db.PlaylistCloudStamps()[cloudId];
        // No record of the name sent: the push sees a change and writes the list again.
        Assert.Null(state.SentMeta);
        // The ids last sent are kept: a merge is measured against them.
        Assert.True(state.Published.Matches([NewId]));
        Assert.Equal(1000, state.Stamp);
    }

    [Fact]
    public void The_repair_leaves_an_entry_alone_when_its_id_names_a_song()
    {
        // Two copies of a song in the library. The entry names one of them and must keep naming it, even
        // though its path is the other one's.
        Add("Chaka Khan/Chaka/01 - I'm Every Woman.m4a", OtherId);
        var other = Add("Chaka Khan/Hits/01 - I'm Every Woman.flac", NewId);
        var playlist = PlaylistWith((OtherId, other.RelativePath));

        Assert.Equal(0, _db.RepairPlaylistIds());

        Assert.Equal(OtherId, Assert.Single(_db.GetPlaylistItems(playlist)).RpmId);
        Assert.Equal(OtherId, _db.CurrentRpmId(OtherId));
    }

    [Fact]
    public void An_id_that_changed_twice_finds_the_song_by_its_first_id()
    {
        var song = Add("Cher/Forever Fan/01 - Believe.m4a", OldId);
        _db.SetRpmId(song.Id, NewId);
        _db.SetRpmId(song.Id, NewerId);

        Assert.Equal(NewerId, _db.CurrentRpmId(OldId));
        Assert.Equal([NewerId, OtherId], _db.CurrentRpmIds([OldId, OtherId]));
    }

    [Fact]
    public void An_id_with_no_alias_is_its_own_answer()
    {
        Assert.Equal(OtherId, _db.CurrentRpmId(OtherId));
    }

    [Fact]
    public void A_published_list_read_in_new_ids_merges_a_renamed_song_as_the_same_song()
    {
        // Published with the old id, held here with the new one, and the cloud unchanged since: the merge
        // must not read that as one song taken out and another put in.
        var published = PlaylistBase.Published([OldId, OtherId]);
        string Now(string id) => id == OldId ? NewId : id;

        var merged = PlaylistMerge.Songs(published.Translated(Now), [NewId, OtherId], [.. new[] { OldId, OtherId }.Select(Now)]);

        Assert.Equal([NewId, OtherId], merged);
        Assert.False(PlaylistBase.Unknown.Translated(Now).IsKnown);
    }
}
