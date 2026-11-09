using System.Text;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>
/// .m3u, .m3u8 and .pls playlists. The samples are read as if they were in /Users/test/Playlists/.
/// </summary>
public class PlaylistFileReaderTests
{
    private const string Folder = "/Users/test/Playlists/";

    private static PlaylistFileInput Sample(string name) =>
        new(Folder + name, File.ReadAllBytes(SourceJson.AssetPath("playlists", name)));

    private static SourceLibrary Samples() => PlaylistFileReader.Read(
        [Sample("absolute.m3u"), Sample("latin1.m3u"), Sample("mac.pls"), Sample("relative.m3u8")]);

    private static PlaylistFileInput Text(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Playlist_files_sample_matches_expected() =>
        Assert.Equal(SourceJson.Expected("playlistfiles"), SourceJson.Of(Samples()));

    [Fact]
    public void Relative_paths_resolve_against_the_playlist_folder()
    {
        var library = PlaylistFileReader.Read([Sample("relative.m3u8")]);
        Assert.Equal(
            ["/Users/test/Music/Artist A/Album A/01 Track One.flac", "/Users/test/Playlists/Artist B/Album B/02 Track Two.mp3"],
            library.Tracks.Select(t => ((LocalFile)t.File).Path));
    }

    [Fact]
    public void Windows_relative_paths_use_backslashes()
    {
        var library = PlaylistFileReader.Read([Text(@"C:\Playlists\road.m3u", "Music\\a.mp3\r\n..\\Other\\b.mp3\r\n./c.mp3\r\n")]);
        Assert.Equal(
            [@"C:\Playlists\Music\a.mp3", @"C:\Other\b.mp3", @"C:\Playlists\c.mp3"],
            library.Tracks.Select(t => ((LocalFile)t.File).Path));
    }

    [Fact]
    public void Windows_paths_are_kept_as_they_are()
    {
        var library = PlaylistFileReader.Read([Sample("absolute.m3u")]);
        Assert.Equal(@"C:\Users\Test\Music\Artist C\Album C\03 Track Three.mp3", ((LocalFile)library.Tracks[0].File).Path);
        Assert.Equal(@"D:\Music\Artist D\Album D\04 Track Four.flac", ((LocalFile)library.Tracks[1].File).Path);
    }

    [Fact]
    public void Extinf_gives_artist_and_title()
    {
        var track = PlaylistFileReader.Read([Sample("absolute.m3u")]).Tracks[0];
        Assert.Equal("Artist C", track.Artist);
        Assert.Equal("Track Three", track.Title);
        Assert.Equal(180.0, track.DurationSeconds);
    }

    [Fact]
    public void Extinf_with_attributes_and_no_artist()
    {
        var track = PlaylistFileReader.Read([Text(Folder + "x.m3u", "#EXTM3U\n#EXTINF:-1 tvg-id=\"x\",Only A Title\n/m/a.mp3\n")]).Tracks[0];
        Assert.Equal("", track.Artist);
        Assert.Equal("Only A Title", track.Title);
        Assert.Equal(0.0, track.DurationSeconds);
    }

    [Fact]
    public void Entry_without_extinf_is_named_after_its_file() =>
        Assert.Equal("02 Track Two", PlaylistFileReader.Read([Sample("relative.m3u8")]).Tracks[1].Title);

    [Fact]
    public void Pls_entries_are_read_in_order()
    {
        var library = PlaylistFileReader.Read([Text(Folder + "o.pls", "[playlist]\nFile2=/m/b.mp3\nFile1=/m/a.mp3\nNumberOfEntries=2\n")]);
        Assert.Equal(["/m/a.mp3", "/m/b.mp3"], library.Playlists[0].TrackIds);
    }

    [Fact]
    public void Latin1_m3u_is_read_as_windows_1252()
    {
        var track = PlaylistFileReader.Read([Sample("latin1.m3u")]).Tracks[0];
        Assert.Equal(@"C:\Music\Beyoncé Tribute\Café.mp3", ((LocalFile)track.File).Path);
        Assert.Equal("Beyoncé Tribute", track.Artist);
    }

    [Fact]
    public void Utf8_bom_is_ignored()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("#EXTM3U\n/m/é.mp3\n")).ToArray();
        var library = PlaylistFileReader.Read([new PlaylistFileInput(Folder + "bom.m3u", bytes)]);
        Assert.Equal("/m/é.mp3", ((LocalFile)library.Tracks.Single().File).Path);
    }

    [Fact]
    public void Web_addresses_are_skipped_and_left_unresolved()
    {
        var library = PlaylistFileReader.Read([Text(Folder + "r.m3u", "#EXTINF:-1,Radio\nhttp://radio.example.com/stream\n/m/a.mp3\n")]);
        Assert.Single(library.Tracks);
        Assert.Equal(["http://radio.example.com/stream"], library.Playlists[0].Unresolved);
        Assert.Equal(SkipReason.NotAFile, library.Skipped.Single().ReasonKey);
    }

    [Fact]
    public void Playlist_name_is_the_file_name() =>
        Assert.Equal(["absolute", "latin1", "mac", "relative"], Samples().Playlists.Select(p => p.Name));

    [Fact]
    public void Repeated_entries_are_kept_in_the_playlist() =>
        Assert.Equal(3, PlaylistFileReader.Read([Sample("relative.m3u8")]).Playlists[0].TrackIds.Count);
}
