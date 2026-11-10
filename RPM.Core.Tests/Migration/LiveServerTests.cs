using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>A test that runs only against real servers: set RPM_MIGRATION_LIVE=1, and the addresses in
/// RPM_LIVE_JELLYFIN and RPM_LIVE_NAVIDROME (each with a user "test", password "secret", the five test
/// tracks, a favourite, plays and a playlist). Skipped everywhere else, including CI.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RPM_MIGRATION_LIVE") != "1")
            Skip = "Needs RPM_MIGRATION_LIVE=1 and running test servers.";
    }
}

/// <summary>
/// The readers against a real Jellyfin and a real Navidrome: what they read, that the original file
/// downloads, and that downloading does not count as a play on the server.
/// </summary>
public class LiveServerTests
{
    private static Uri Address(string name) => new(Environment.GetEnvironmentVariable(name) ?? "http://localhost");

    private static async Task<byte[]> Bytes(IMediaServer server, SourceTrack track)
    {
        await using var stream = await server.OpenAsync((RemoteFile)track.File, CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    [LiveFact]
    public async Task Live_jellyfin_reads_and_downloads()
    {
        using var http = new HttpClient();
        var session = await JellyfinSignIn.WithPasswordAsync(http, Address("RPM_LIVE_JELLYFIN"), "test", "secret", CancellationToken.None);
        var server = new JellyfinServer(http, session);

        var library = await server.ReadAsync(new Progress<int>(), CancellationToken.None);

        Assert.Equal(5, library.Tracks.Count);
        var opening = library.Tracks.Single(t => t.Title == "Opening");
        Assert.True(opening.Favorite);
        Assert.True(opening.Plays >= 1);
        Assert.Equal(".flac", ((RemoteFile)opening.File).Extension);
        Assert.Equal("First Light", opening.Album);
        var evening = library.Playlists.Single(p => p.Name == "Evening");
        Assert.Equal(["Shore", "Opening", "Shore"], evening.TrackIds.Select(id => library.Tracks.Single(t => t.SourceId == id).Title));

        var bytes = await Bytes(server, opening);
        Assert.Equal("fLaC"u8.ToArray(), bytes[..4]);
        Assert.Equal(((RemoteFile)opening.File).Size, bytes.Length);

        var again = await server.ReadAsync(new Progress<int>(), CancellationToken.None);
        Assert.Equal(opening.Plays, again.Tracks.Single(t => t.Title == "Opening").Plays);
    }

    [LiveFact]
    public async Task Live_navidrome_reads_and_downloads()
    {
        using var http = new HttpClient();
        var login = new SubsonicLogin(Address("RPM_LIVE_NAVIDROME"), "test", "secret");
        await SubsonicCheck.CheckAsync(http, login, CancellationToken.None);
        var server = new SubsonicServer(http, login);

        var library = await server.ReadAsync(new Progress<int>(), CancellationToken.None);

        // The server may hold more than the five test tones (a larger library to watch the progress with).
        Assert.InRange(library.Tracks.Count, 5, int.MaxValue);
        Assert.All(library.Tracks, t => Assert.Contains(((RemoteFile)t.File).Extension, RPM.Core.Scanning.AudioFileScanner.AudioExtensions));
        var opening = library.Tracks.Single(t => t.Title == "Opening");
        Assert.True(opening.Favorite);
        Assert.Equal(3, opening.Plays);
        Assert.Equal(8, library.Tracks.Single(t => t.Title == "Middle").Rating);
        var weekend = library.Playlists.Single(p => p.Name == "Weekend");
        Assert.Equal(["Tide", "Opening", "Tide"], weekend.TrackIds.Select(id => library.Tracks.Single(t => t.SourceId == id).Title));

        var bytes = await Bytes(server, opening);
        Assert.Equal("fLaC"u8.ToArray(), bytes[..4]);

        var again = await server.ReadAsync(new Progress<int>(), CancellationToken.None);
        Assert.Equal(3, again.Tracks.Single(t => t.Title == "Opening").Plays);
    }

    [LiveFact]
    public async Task Live_wrong_passwords_are_refused()
    {
        using var http = new HttpClient();
        await Assert.ThrowsAsync<MediaServerSignInFailed>(() =>
            JellyfinSignIn.WithPasswordAsync(http, Address("RPM_LIVE_JELLYFIN"), "test", "wrong", CancellationToken.None));
        await Assert.ThrowsAsync<MediaServerSignInFailed>(() =>
            SubsonicCheck.CheckAsync(http, new SubsonicLogin(Address("RPM_LIVE_NAVIDROME"), "test", "wrong"), CancellationToken.None));
    }
}
