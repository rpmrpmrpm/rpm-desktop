using RPM.Core.Models;
using RPM.Core.Import;

namespace RPM.Core.Tests;

public class NamingTemplateTests
{
    private static TrackInfo Track(
        string? title = "Hysteria",
        string? artist = "Muse",
        string? albumArtist = null,
        string? album = "Absolution",
        uint track = 8,
        string ext = ".mp3")
        => new()
        {
            FilePath = $"/music/file{ext}",
            Extension = ext,
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = album,
            TrackNumber = track,
        };

    [Fact]
    public void Renders_artist_album_numbered_title()
        => Assert.Equal(
            "Muse/Absolution/08 - Hysteria.mp3",
            NamingTemplate.ArtistAlbumNumberedTitle.Render(Track()));

    [Fact]
    public void AlbumArtist_takes_precedence_for_folder()
        => Assert.Equal(
            "Various Artists/Absolution/08 - Hysteria.mp3",
            NamingTemplate.ArtistAlbumNumberedTitle.Render(Track(albumArtist: "Various Artists")));

    [Fact]
    public void Missing_track_number_drops_prefix()
        => Assert.Equal(
            "Muse/Absolution/Hysteria.mp3",
            NamingTemplate.ArtistAlbumNumberedTitle.Render(Track(track: 0)));

    [Fact]
    public void Missing_album_drops_the_album_folder_level()
        => Assert.Equal(
            "Unknown Artist/Hysteria.mp3",
            NamingTemplate.ArtistAlbumNumberedTitle.Render(Track(artist: null, album: null, track: 0)));

    [Fact]
    public void Missing_title_falls_back_to_file_name()
    {
        var t = Track(title: null, track: 0) with { FilePath = "/music/some song.mp3" };
        Assert.Equal("Muse/Absolution/some song.mp3", NamingTemplate.ArtistAlbumNumberedTitle.Render(t));
    }

    [Fact]
    public void Flat_template_has_no_folders()
        => Assert.Equal("Muse - Hysteria.mp3", NamingTemplate.FlatArtistTitle.Render(Track()));

    [Fact]
    public void Segments_are_sanitized_independently()
    {
        var t = Track(artist: "AC/DC", album: "Who Made Who?", title: "What's Next to the Moon");
        Assert.Equal(
            "AC DC/Who Made Who/08 - What's Next to the Moon.mp3",
            NamingTemplate.ArtistAlbumNumberedTitle.Render(t));
    }

    [Fact]
    public void Extension_is_preserved()
        => Assert.EndsWith(".flac", NamingTemplate.FlatArtistTitle.Render(Track(ext: ".flac")));

    [Fact]
    public void Preview_renders_the_sample_track()
        => Assert.Equal(
            "Radiohead/OK Computer/02 - Paranoid Android.flac",
            NamingTemplate.ArtistAlbumNumberedTitle.Preview());

    [Fact]
    public void Preview_never_throws_on_an_empty_pattern()
        => Assert.Null(Record.Exception(() => new NamingTemplate("Empty", "").Preview()));

    [Fact]
    public void Disc_token_prefixes_a_multi_disc_track()
        => Assert.Equal(
            "Muse/Absolution/2-05 - Hysteria.mp3",
            new NamingTemplate("D", "{albumartist}/{album}/{disc}-{track:00} - {title}")
                .Render(Track(track: 5) with { DiscNumber = 2 }));

    [Fact]
    public void Disc_token_takes_a_pad_format()
        => Assert.Equal(
            "Absolution/Disc 02/05 - Hysteria.mp3",
            new NamingTemplate("D", "{album}/Disc {disc:00}/{track:00} - {title}")
                .Render(Track(track: 5) with { DiscNumber = 2 }));

    [Fact]
    public void A_single_disc_track_drops_the_disc_prefix()
        => Assert.Equal(
            "Muse/Absolution/05 - Hysteria.mp3",
            new NamingTemplate("D", "{albumartist}/{album}/{disc}-{track:00} - {title}")
                .Render(Track(track: 5)));   // DiscNumber 0

    [Fact]
    public void Every_advertised_token_resolves()
    {
        // The editor's palette offers exactly NamingTemplate.Tokens, so each must resolve on the
        // sample track: no "{...}" left that the renderer would keep and write into the path.
        var pattern = string.Join("/", NamingTemplate.Tokens);
        Assert.DoesNotContain("{", new NamingTemplate("All tokens", pattern).Preview());
    }

    // ── works and their movements ──

    private static TrackInfo Movement(string? work, string? movement, uint track = 6, uint disc = 1)
        => new()
        {
            FilePath = "/music/file.flac",
            Extension = ".flac",
            Title = work is null ? movement ?? "" : $"{work}: {movement}",
            Artist = "Bart van Oort",
            AlbumArtist = "Wolfgang Amadeus Mozart",
            Album = "Complete Keyboard Works",
            Work = work,
            MovementName = movement,
            TrackNumber = track,
            DiscNumber = disc,
        };

    /// <summary>A movement is filed under its work and named by the movement alone: the work is the
    /// folder it is in, and repeating it in every file name makes a box set hard to read.</summary>
    [Fact]
    public void A_movement_files_under_its_work()
        => Assert.Equal(
            "Wolfgang Amadeus Mozart/Complete Keyboard Works/Sonata In C Major, KV. 330/01-06 - Allegro Moderato.flac",
            NamingTemplate.ArtistAlbumWorkMovement.Render(
                Movement("Sonata In C Major, KV. 330", "Allegro Moderato")));

    /// <summary>A piece that belongs to no work is not filed in an "Unknown Work" folder: it goes next
    /// to the works, one level up, as it is on the record. A recital disc has both kinds, and one
    /// template must handle both.</summary>
    [Fact]
    public void A_piece_in_no_work_keeps_its_place_beside_them()
        => Assert.Equal(
            "Wolfgang Amadeus Mozart/Complete Keyboard Works/01-04 - Fantasy In D Minor, KV. 397.flac",
            NamingTemplate.ArtistAlbumWorkMovement.Render(
                Movement(null, "Fantasy In D Minor, KV. 397", track: 4)));

    /// <summary>"{movement}" on a track that names none falls back to the whole title, so a pattern
    /// written for a classical shelf never gives a file with no name.</summary>
    [Fact]
    public void The_movement_token_falls_back_to_the_title()
        => Assert.Equal(
            "Muse/Absolution/08 - Hysteria.mp3",
            new NamingTemplate("x", "{albumartist}/{album}/{track:00} - {movement}").Render(Track()));

    /// <summary>The work token in a FILE name, not as a folder. Some shelves are flat, and there the
    /// pattern has to include everything.</summary>
    [Fact]
    public void The_work_token_reads_in_a_file_name_too()
        => Assert.Equal(
            "Complete Keyboard Works/Sonata In C Major, KV. 330 - 06 Allegro Moderato.flac",
            new NamingTemplate("x", "{album}/{work} - {track:00} {movement}").Render(
                Movement("Sonata In C Major, KV. 330", "Allegro Moderato")));

    [Fact]
    public void The_classical_preset_is_offered()
        => Assert.Contains(NamingTemplate.ArtistAlbumWorkMovement, NamingTemplate.Presets);
}
