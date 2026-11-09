using System.Diagnostics;
using System.Text;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>
/// The iTunes library XML, as Apple Music exports it and as MusicBee writes it. The samples are in
/// Assets/Migration (copied from the rpm repo); every name in them is made up.
/// </summary>
public class ITunesLibraryReaderTests
{
    private static SourceLibrary ReadSample(string folder, string file, SourceKind kind)
    {
        using var xml = File.OpenRead(SourceJson.AssetPath(folder, file));
        return ITunesLibraryReader.Read(xml, kind);
    }

    private static SourceLibrary Itunes() => ReadSample("itunes", "Library.xml", SourceKind.ITunes);
    private static SourceLibrary MusicBee() => ReadSample("musicbee", "iTunes Music Library.xml", SourceKind.MusicBee);

    private static SourceTrack Track(SourceLibrary library, string id) => library.Tracks.Single(t => t.SourceId == id);

    [Fact]
    public void Itunes_sample_matches_expected() =>
        Assert.Equal(SourceJson.Expected("itunes"), SourceJson.Of(Itunes()));

    [Fact]
    public void MusicBee_sample_matches_expected() =>
        Assert.Equal(SourceJson.Expected("musicbee"), SourceJson.Of(MusicBee()));

    [Fact]
    public void Rating_computed_reads_as_zero() =>
        Assert.Equal(0, Track(Itunes(), "AAAA000000000105").Rating);

    [Fact]
    public void Loved_and_Favorited_are_favourites()
    {
        var library = Itunes();
        Assert.True(Track(library, "AAAA000000000101").Favorite);
        Assert.True(Track(library, "AAAA000000000102").Favorite);
        Assert.False(Track(library, "AAAA000000000103").Favorite);
    }

    [Fact]
    public void Half_stars_are_kept()
    {
        var library = MusicBee();
        Assert.Equal(1, Track(library, "1").Rating);
        Assert.Equal(7, Track(library, "3").Rating);
    }

    [Fact]
    public void Location_is_unescaped_and_normalised_to_NFC()
    {
        var library = Itunes();
        var cafe = (LocalFile)Track(library, "AAAA000000000105").File;
        Assert.EndsWith("Café Society/Le Café/01 Café.flac", cafe.Path);
        Assert.True(cafe.Path.IsNormalized(NormalizationForm.FormC));
        Assert.EndsWith("Album #1/01 Rock & Roll.mp3", ((LocalFile)Track(library, "AAAA000000000103").File).Path);
        Assert.EndsWith("Album #1/02 100% Pure.mp3", ((LocalFile)Track(library, "AAAA000000000104").File).Path);
    }

    [Fact]
    public void Windows_location_becomes_a_drive_path() =>
        Assert.Equal(@"C:\Users\Test\Music\Artist F\Album F\01 One.mp3", ((LocalFile)Track(MusicBee(), "1").File).Path);

    [Fact]
    public void Master_and_distinguished_lists_are_skipped()
    {
        var names = Itunes().Playlists.Select(p => p.Name).ToList();
        Assert.DoesNotContain("Library", names);
        Assert.DoesNotContain("Music", names);
        Assert.DoesNotContain("Moods", names);
    }

    [Fact]
    public void Folders_become_the_playlist_folder_chain() =>
        Assert.Equal(["Moods", "Evening"], Itunes().Playlists.Single(p => p.Name == "Late").Folder);

    [Fact]
    public void Smart_playlist_is_flagged_and_keeps_its_items()
    {
        var top = Itunes().Playlists.Single(p => p.Name == "Top Rated");
        Assert.True(top.Smart);
        Assert.Equal(["AAAA000000000101", "AAAA000000000102"], top.TrackIds);
    }

    [Fact]
    public void Unknown_track_id_goes_to_unresolved() =>
        Assert.Equal(["999", "107"], Itunes().Playlists.Single(p => p.Name == "Party").Unresolved);

    [Fact]
    public void Kind_must_be_an_itunes_library_kind()
    {
        using var xml = File.OpenRead(SourceJson.AssetPath("itunes", "Library.xml"));
        Assert.Throws<ArgumentException>(() => ITunesLibraryReader.Read(xml, SourceKind.Plex));
    }

    [Fact]
    public void Reads_20000_tracks_in_under_5_seconds()
    {
        var xml = new StringBuilder("""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>Library Persistent ID</key><string>1111222233334444</string><key>Tracks</key><dict>
            """);
        for (var i = 1; i <= 20_000; i++)
            xml.Append($"<key>{i}</key><dict><key>Track ID</key><integer>{i}</integer><key>Name</key><string>Song {i}</string>"
                + $"<key>Artist</key><string>Band {i % 300}</string><key>Album</key><string>Record {i % 2000}</string>"
                + $"<key>Total Time</key><integer>200000</integer><key>Size</key><integer>5000000</integer>"
                + $"<key>Play Count</key><integer>{i % 50}</integer><key>Rating</key><integer>{i % 6 * 20}</integer>"
                + $"<key>Date Added</key><date>2015-01-01T00:00:00Z</date><key>Persistent ID</key><string>P{i:D15}</string>"
                + $"<key>Location</key><string>file:///Users/test/Music/Band%20{i % 300}/Record%20{i % 2000}/{i}.mp3</string></dict>");
        xml.Append("</dict><key>Playlists</key><array></array></dict></plist>");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));

        var clock = Stopwatch.StartNew();
        var library = ITunesLibraryReader.Read(stream, SourceKind.ITunes);

        Assert.Equal(20_000, library.Tracks.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }
}
