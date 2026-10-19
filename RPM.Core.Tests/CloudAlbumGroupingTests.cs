using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What one album is, over cloud rows. The phone draws its wall from this. Grouping by track artist
/// plus album name would make a compilation into one album per guest artist, and a two-disc set into
/// two records. These are the three cases the desktop's folder rule gets right and a tag pair
/// cannot.
/// </summary>
public class CloudAlbumGroupingTests
{
    private static CloudTrack T(
        string path, string? title = null, string? artist = null, string? album = null,
        string? albumArtist = null, int disc = 0, int track = 0)
        => new(
            RpmId: path,
            Sha256: "sha-" + path,
            RelativePath: path,
            OriginalSize: 1,
            Title: title ?? Path.GetFileNameWithoutExtension(path),
            Artist: artist,
            Album: album,
            AddedAt: 0,
            Tags: new CloudTrackTags(
                TrackNumber: track, DiscNumber: disc, AlbumArtist: albumArtist));

    /// <summary>A compilation as it is actually tagged: one album artist, a different artist on every
    /// track. Grouping by the track's artist would make twelve of these into twelve albums.</summary>
    private static CloudTrack[] Compilation() =>
    [
        T("Various Artists/Now 22/01 Rhythm Is a Dancer.flac", artist: "Snap!",
          album: "Now 22", albumArtist: "Various Artists", track: 1),
        T("Various Artists/Now 22/02 Ebeneezer Goode.flac", artist: "The Shamen",
          album: "Now 22", albumArtist: "Various Artists", track: 2),
        T("Various Artists/Now 22/03 Sleeping Satellite.flac", artist: "Tasmin Archer",
          album: "Now 22", albumArtist: "Various Artists", track: 3),
    ];

    private static IEnumerable<IGrouping<string, CloudTrack>> Group(IEnumerable<CloudTrack> tracks)
        => tracks.GroupBy(CloudAlbumGrouping.KeyFor, StringComparer.Ordinal);

    [Fact]
    public void A_compilation_is_one_album_however_many_artists_play_on_it()
    {
        var albums = Group(Compilation()).ToList();

        Assert.Single(albums);

        var rows = albums[0].ToList();
        Assert.Equal("Various Artists", CloudAlbumGrouping.AlbumArtistFor(rows, "Various Artists/Now 22"));
        Assert.Equal("Now 22", CloudAlbumGrouping.AlbumNameFor(rows, "Various Artists/Now 22"));
    }

    [Fact]
    public void A_merged_multi_disc_set_is_one_album()
    {
        // MultiDiscMode.MergedAlbum, which is the default: every disc in the one folder, per-disc track
        // numbers, one album name.
        CloudTrack[] tracks =
        [
            T("The Clash/Sandinista!/01 The Magnificent Seven.flac", artist: "The Clash",
              album: "Sandinista!", albumArtist: "The Clash", disc: 1, track: 1),
            T("The Clash/Sandinista!/02 Hitsville U.K..flac", artist: "The Clash",
              album: "Sandinista!", albumArtist: "The Clash", disc: 1, track: 2),
            T("The Clash/Sandinista!/03 Lose This Skin.flac", artist: "The Clash",
              album: "Sandinista!", albumArtist: "The Clash", disc: 2, track: 1),
        ];

        Assert.Single(Group(tracks));
    }

    [Fact]
    public void Discs_filed_as_separate_albums_stay_separate()
    {
        // MultiDiscMode.SeparateAlbums decorates the name and gives each disc its own folder. Those are
        // meant to read as two records, so nothing here may merge them.
        CloudTrack[] tracks =
        [
            T("Erasure/Erasure (Disc 1)/01 Intro.flac", artist: "Erasure",
              album: "Erasure (Disc 1)", albumArtist: "Erasure", track: 1),
            T("Erasure/Erasure (Disc 2)/01 Rock Me Gently.flac", artist: "Erasure",
              album: "Erasure (Disc 2)", albumArtist: "Erasure", track: 1),
        ];

        Assert.Equal(2, Group(tracks).Count());
    }

    [Fact]
    public void Two_artists_with_an_identically_named_album_are_two_albums()
    {
        CloudTrack[] tracks =
        [
            T("Queen/Greatest Hits/01 Bohemian Rhapsody.flac", artist: "Queen",
              album: "Greatest Hits", albumArtist: "Queen", track: 1),
            T("Weezer/Greatest Hits/01 Buddy Holly.flac", artist: "Weezer",
              album: "Greatest Hits", albumArtist: "Weezer", track: 1),
        ];

        Assert.Equal(2, Group(tracks).Count());
    }

    [Fact]
    public void The_folder_decides_whatever_the_tags_disagree_about()
    {
        // Same folder, and one file whose album tag was never written. The desktop shows one album here
        // because the folder is the album; a tag pair would show two.
        CloudTrack[] tracks =
        [
            T("Manu Chao/Clandestino/01 Clandestino.flac", artist: "Manu Chao", album: "Clandestino"),
            T("Manu Chao/Clandestino/02 Desaparecido.flac", artist: "Manu Chao"),
        ];

        Assert.Single(Group(tracks));
    }

    [Fact]
    public void Case_and_separators_do_not_split_a_folder()
    {
        CloudTrack[] tracks =
        [
            T("Manu Chao/Clandestino/01 Clandestino.flac", artist: "Manu Chao", album: "Clandestino"),
            T(@"manu chao\clandestino\02 Desaparecido.flac", artist: "Manu Chao", album: "Clandestino"),
        ];

        Assert.Single(Group(tracks));
    }

    [Fact]
    public void Tracks_loose_at_the_library_root_are_one_album()
    {
        CloudTrack[] tracks =
        [
            T("stray one.flac", artist: "Somebody"),
            T("stray two.flac", artist: "Somebody Else"),
        ];

        Assert.Single(Group(tracks));
        Assert.Equal("", CloudAlbumGrouping.FolderOf("stray one.flac"));
    }

    /// <summary>Nothing currently writes a pathless row, but <c>relativePath</c> is optional on the
    /// wire, so the tags have to answer, and there the disc decorations come off, which is the rule
    /// the desktop matches an incoming disc to an existing album with.</summary>
    [Fact]
    public void Without_a_path_the_tags_answer_and_disc_decorations_come_off()
    {
        var discOne = CloudAlbumGrouping.KeyFor(null, "Erasure", "Erasure", "Erasure (Disc 1)");
        var discTwo = CloudAlbumGrouping.KeyFor(null, "Erasure", "Erasure", "Erasure - CD 2");

        Assert.Equal(discOne, discTwo);

        // And the album still belongs to its album artist, not to the track's.
        Assert.Equal(
            CloudAlbumGrouping.KeyFor(null, "Various Artists", "Snap!", "Now 22"),
            CloudAlbumGrouping.KeyFor(null, "Various Artists", "The Shamen", "Now 22"));
    }

    /// <summary>A tag that is present but blank is not an album artist: the same rule
    /// <see cref="AlbumArt.KeyForTrack"/> states, and the reason neither uses <c>??</c>.</summary>
    [Fact]
    public void A_blank_album_artist_falls_back_to_the_track_artist()
        => Assert.Equal(
            CloudAlbumGrouping.KeyFor(null, null, "Manu Chao", "Clandestino"),
            CloudAlbumGrouping.KeyFor(null, "   ", "Manu Chao", "Clandestino"));

    [Fact]
    public void The_folder_names_the_album_when_no_file_carries_a_tag()
    {
        CloudTrack[] tracks = [T("Manu Chao/Clandestino/01 untitled.flac")];
        var folder = CloudAlbumGrouping.FolderOf(tracks[0].RelativePath);

        // Artist/Album/track: the last segment is the album and the one above it the artist, which is
        // what the desktop's card reads before a tag has been opened.
        Assert.Equal("Clandestino", CloudAlbumGrouping.AlbumNameFor(tracks, folder));
        Assert.Equal("Manu Chao", CloudAlbumGrouping.AlbumArtistFor(tracks, folder));
    }

    [Fact]
    public void The_album_artist_outranks_the_track_artist_and_both_outrank_the_folder()
    {
        CloudTrack[] tagged = [T("V/Now 22/01 x.flac", artist: "Snap!", albumArtist: "Various Artists")];
        CloudTrack[] trackOnly = [T("V/Now 22/01 x.flac", artist: "Snap!")];

        Assert.Equal("Various Artists", CloudAlbumGrouping.AlbumArtistFor(tagged, "V/Now 22"));
        Assert.Equal("Snap!", CloudAlbumGrouping.AlbumArtistFor(trackOnly, "V/Now 22"));
    }

    /// <summary>An untagged disc reads as disc 1 and an untagged track sorts last inside its disc: the
    /// desktop's rule, which is why the phone asks <see cref="AlbumTracklist"/> rather than sorting
    /// unknown positions to the very end of the record.</summary>
    [Fact]
    public void Discs_come_apart_in_reading_order()
    {
        CloudTrack[] tracks =
        [
            T("a/b/d2t1.flac", disc: 2, track: 1),
            T("a/b/d1t2.flac", disc: 1, track: 2),
            T("a/b/nothing.flac"),
            T("a/b/d1t1.flac", disc: 1, track: 1),
        ];

        var discs = AlbumTracklist.ByDisc(tracks, CloudAlbumGrouping.DiscOf, CloudAlbumGrouping.TrackOf);

        Assert.Equal(2, discs.Count);
        Assert.Equal(1u, discs[0].DiscNumber);
        Assert.Equal(["d1t1", "d1t2", "nothing"], discs[0].Tracks.Select(t => t.Title));
        Assert.Equal(["d2t1"], discs[1].Tracks.Select(t => t.Title));
    }

    [Fact]
    public void A_single_disc_album_is_one_section_so_no_divider_is_drawn()
    {
        CloudTrack[] tracks = [T("a/b/one.flac", track: 1), T("a/b/two.flac", track: 2)];

        Assert.Single(AlbumTracklist.ByDisc(tracks, CloudAlbumGrouping.DiscOf, CloudAlbumGrouping.TrackOf));
    }
}
