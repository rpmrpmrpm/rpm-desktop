using RPM.Core.Analysis;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class AlbumResolverTests
{
    private static TrackInfo Track(string path, string? artist, string? album, string? title = "T",
        uint track = 0, uint trackCount = 0, string? albumArtist = null, string? grouping = null)
        => new()
        {
            FilePath = path,
            Extension = ".mp3",
            Artist = artist,
            Album = album,
            Title = title,
            TrackNumber = track,
            TrackCount = trackCount,
            AlbumArtist = albumArtist,
            Grouping = grouping,
        };

    [Fact]
    public void Compilation_folder_many_artists_shared_album()
    {
        var tracks = Enumerable.Range(1, 8).Select(i =>
            Track($"/m/Now 64/{i:00}.mp3", $"Artist {i}", "Now That's What I Call Music! 64", track: (uint)i, trackCount: 8)).ToList();

        var group = Assert.Single(AlbumResolver.Resolve(tracks));
        Assert.Equal(GroupKind.Compilation, group.Kind);
        Assert.Equal("Now That's What I Call Music! 64", group.AlbumName);
        Assert.Equal(AlbumResolver.VariousArtists, group.AlbumArtist);
        Assert.Equal(1.0, group.Completeness, 2);
    }

    [Fact]
    public void Disc_suffixed_album_tags_cluster_as_one_album()
    {
        // Real-world Now-collection rips: "Album - CD 1" / "Album - CD 2" per disc folder.
        var tracks = new List<TrackInfo>();
        for (var d = 1; d <= 2; d++)
            for (var i = 1; i <= 4; i++)
                tracks.Add(Track($"/m/Now 64/CD {d}/{i:00}.mp3", $"Artist {d}{i}",
                    $"Now That's What I Call Music! 64 - CD {d}", albumArtist: "Various Artists"));

        var group = Assert.Single(AlbumResolver.Resolve(tracks));
        Assert.Equal(GroupKind.Compilation, group.Kind);
        Assert.Equal("Now That's What I Call Music! 64", group.AlbumName);
    }

    [Theory]
    [InlineData("Album - CD 1", "Album")]
    [InlineData("Album (Disc 2)", "Album")]
    [InlineData("Album [disk 3]", "Album")]
    [InlineData("Album CD2", "Album")]
    [InlineData("CD 1", "CD 1")]                 // nothing left → keep original
    [InlineData("Plain Album", "Plain Album")]
    public void NormalizeAlbum_strips_disc_suffixes(string input, string expected)
        => Assert.Equal(expected, AlbumResolver.NormalizeAlbum(input));

    [Fact]
    public void Disc_subfolders_collapse_into_one_group()
    {
        var tracks = new List<TrackInfo>();
        for (var d = 1; d <= 2; d++)
            for (var i = 1; i <= 4; i++)
                tracks.Add(Track($"/m/Now 64/CD {d}/{i:00}.mp3", $"A{d}{i}", "Now 64"));

        var group = Assert.Single(AlbumResolver.Resolve(tracks));
        Assert.Equal(8, group.Tracks.Count);
        Assert.Equal(GroupKind.Compilation, group.Kind);
    }

    [Fact]
    public void Single_artist_folder_is_an_original_album()
    {
        var tracks = Enumerable.Range(1, 10).Select(i =>
            Track($"/m/Absolution/{i:00}.mp3", "Muse", "Absolution", track: (uint)i, trackCount: 14)).ToList();

        var group = Assert.Single(AlbumResolver.Resolve(tracks));
        Assert.Equal(GroupKind.OriginalAlbum, group.Kind);
        Assert.Equal("Muse", group.AlbumArtist);
        Assert.Equal(10 / 14.0, group.Completeness, 2);
    }

    [Fact]
    public void Mixed_folder_without_album_cohesion_is_loose()
    {
        var tracks = new List<TrackInfo>
        {
            Track("/m/dl/a.mp3", "Muse", "Absolution"),
            Track("/m/dl/b.mp3", "Keane", "Hopes and Fears"),
            Track("/m/dl/c.mp3", "Coldplay", null),
            Track("/m/dl/d.mp3", "Oasis", "Definitely Maybe"),
        };

        var group = Assert.Single(AlbumResolver.Resolve(tracks));
        Assert.Equal(GroupKind.LooseSongs, group.Kind);
        Assert.Null(group.AlbumName);
    }

    [Fact]
    public void Single_file_is_loose_even_with_album_tag()
    {
        var tracks = new List<TrackInfo> { Track("/m/one/a.mp3", "Muse", "Absolution") };
        var group = Assert.Single(AlbumResolver.Resolve(tracks));
        Assert.Equal(GroupKind.LooseSongs, group.Kind);
    }

    [Fact]
    public void Untagged_folder_falls_back_to_folder_name_album()
    {
        var tracks = Enumerable.Range(1, 5).Select(i =>
            Track($"/m/Greatest Hits (1998)/{i:00}.mp3", $"Artist {i}", album: null)).ToList();

        var group = Assert.Single(AlbumResolver.Resolve(tracks));
        Assert.Equal("Greatest Hits", group.AlbumName);
        Assert.Equal(GroupKind.Compilation, group.Kind);
    }

    [Fact]
    public void Default_treatment_follows_goal()
    {
        var comp = Assert.Single(AlbumResolver.Resolve(
            Enumerable.Range(1, 6).Select(i => Track($"/m/c/{i}.mp3", $"A{i}", "Comp", track: (uint)i, trackCount: 20))));
        Assert.Equal(GroupTreatment.KeepAlbum, comp.DefaultTreatment(OrganizeGoal.Albums));
        Assert.Equal(GroupTreatment.LooseSongs, comp.DefaultTreatment(OrganizeGoal.Songs)); // 6/20 = thin

        var full = Assert.Single(AlbumResolver.Resolve(
            Enumerable.Range(1, 12).Select(i => Track($"/m/f/{i}.mp3", "Muse", "Absolution", track: (uint)i, trackCount: 14))));
        Assert.Equal(GroupTreatment.KeepAlbum, full.DefaultTreatment(OrganizeGoal.Songs)); // 12/14 = substantial
    }

    [Fact]
    public void KeepAlbum_moves_original_album_into_grouping()
    {
        var tracks = Enumerable.Range(1, 6).Select(i =>
            Track($"/m/Now 64/{i:00}.mp3", $"Artist {i}", "Now 64")).ToList();
        var group = Assert.Single(AlbumResolver.Resolve(tracks));

        // Recognition renamed this track's album to its original studio album.
        var recognized = tracks[0] with { Album = "Under the Iron Sea" };
        var effective = AlbumResolver.ApplyTreatment(recognized, group, GroupTreatment.KeepAlbum);

        Assert.Equal("Now 64", effective.Album);
        Assert.Equal(AlbumResolver.VariousArtists, effective.AlbumArtist);
        Assert.Equal("Under the Iron Sea", effective.Grouping);
    }

    [Fact]
    public void KeepAlbum_never_clobbers_existing_grouping()
    {
        var tracks = Enumerable.Range(1, 6).Select(i =>
            Track($"/m/Now 64/{i:00}.mp3", $"Artist {i}", "Now 64")).ToList();
        var group = Assert.Single(AlbumResolver.Resolve(tracks));

        var t = tracks[0] with { Album = "Original", Grouping = "My Grouping" };
        var effective = AlbumResolver.ApplyTreatment(t, group, GroupTreatment.KeepAlbum);
        Assert.Equal("My Grouping", effective.Grouping);
    }

    [Fact]
    public void LooseSongs_treatment_drops_the_album()
    {
        var tracks = Enumerable.Range(1, 6).Select(i =>
            Track($"/m/c/{i}.mp3", $"A{i}", "Comp")).ToList();
        var group = Assert.Single(AlbumResolver.Resolve(tracks));

        var effective = AlbumResolver.ApplyTreatment(tracks[0], group, GroupTreatment.LooseSongs);
        Assert.Null(effective.Album);
        Assert.Equal("A1", effective.AlbumArtist);
    }

    [Fact]
    public void SplitToOriginal_keeps_per_track_album()
    {
        var tracks = Enumerable.Range(1, 6).Select(i =>
            Track($"/m/c/{i}.mp3", $"A{i}", "Comp")).ToList();
        var group = Assert.Single(AlbumResolver.Resolve(tracks));

        var recognized = tracks[0] with { Album = "Original Album" };
        var effective = AlbumResolver.ApplyTreatment(recognized, group, GroupTreatment.SplitToOriginal);
        Assert.Equal("Original Album", effective.Album);
        Assert.Equal("A1", effective.AlbumArtist);
    }

    [Fact]
    public void Various_artists_tag_forces_compilation_even_with_few_artists()
    {
        var tracks = new List<TrackInfo>
        {
            Track("/m/va/1.mp3", "A", "Mix", albumArtist: "Various Artists"),
            Track("/m/va/2.mp3", "B", "Mix", albumArtist: "Various Artists"),
        };
        var group = Assert.Single(AlbumResolver.Resolve(tracks));
        Assert.Equal(GroupKind.Compilation, group.Kind);
    }
}
