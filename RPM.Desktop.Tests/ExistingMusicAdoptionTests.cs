using System.Security.Cryptography;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// A folder of music the person already had, made the library through <see cref="LibraryService"/>: kept
/// and later handed to rpm to organise, or organised from the start. Run on a real folder with real tags.
/// </summary>
public sealed class ExistingMusicAdoptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-existing-music-tests", Guid.NewGuid().ToString("n"));

    public ExistingMusicAdoptionTests()
    {
        Directory.CreateDirectory(_dir);
        Song("Messy downloads/track one.mp3", "First", 1);
        Song("Messy downloads/zz/another.mp3", "Second", 2);
    }

    public void Dispose()
    {
        KeptFiles.Close(_dir);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] MinimalMp3()
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        return frame;
    }

    /// <summary>A file with tags, written before the folder becomes a library, as a person's own music is.</summary>
    private void Song(string relative, string title, int number)
    {
        var path = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, MinimalMp3());
        var info = TagReader.Read(path) with { Title = title, Artist = "Tide", Album = "Pools", TrackNumber = (uint)number };
        TagReader.WriteAll(path, info);
        // The write gives every file an rpm id. A person's own music has none.
        TagReader.WriteAll(path, TagReader.Read(path) with { RpmId = null });
    }

    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(_dir, p).StartsWith(".rpm", StringComparison.Ordinal))
            .ToDictionary(p => Path.GetRelativePath(_dir, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    [Fact]
    public async Task Kept_music_is_read_without_changes_then_organised_keeping_every_id()
    {
        var before = Snapshot();
        var library = new LibraryService(TestSessions.SignedOut());

        library.Adopt(_dir, FileHandling.Kept);
        Assert.True(library.KeepsFiles);
        await library.SyncAsync();
        var kept = library.Db.Query().ToDictionary(t => t.Title!, t => t.RpmId);
        Assert.Equal(2, kept.Count);
        Assert.Equal(before, Snapshot());
        Assert.False(Directory.Exists(LibraryInbox.PathFor(_dir)));

        // "Let rpm organise them" in Library settings.
        await library.ReleaseKeptAsync();
        Assert.False(library.KeepsFiles);
        Assert.False(File.Exists(FileKeeping.MarkerPath(_dir)));
        var done = await library.OrganiseIfOwedAsync(NamingTemplate.ArtistAlbumNumberedTitle, progress: null);

        Assert.NotNull(done);
        Assert.Equal(2, done.Moved);
        var first = Directory.EnumerateFiles(_dir, "*First*.mp3", SearchOption.AllDirectories).Single();
        Assert.Equal(Path.Combine("Tide", "Pools"), Path.GetDirectoryName(Path.GetRelativePath(_dir, first)));
        Assert.Equal(kept["First"], TagReader.Read(first).RpmId);

        // The next read finds the same ids in the files, so no track changes identity.
        await library.SyncAsync();
        Assert.Equal(kept, library.Db.Query().ToDictionary(t => t.Title!, t => t.RpmId));
        Assert.Null(await library.OrganiseIfOwedAsync(NamingTemplate.ArtistAlbumNumberedTitle, progress: null));
    }

    [Fact]
    public async Task Music_organised_from_the_start_is_refiled_after_its_first_read()
    {
        var library = new LibraryService(TestSessions.SignedOut());

        library.Adopt(_dir, FileHandling.Organised);
        Assert.False(library.KeepsFiles);
        Assert.Equal("owed", library.Db.GetMeta(LibraryService.OrganiseOwedKey));

        await library.SyncAsync();
        var done = await library.OrganiseIfOwedAsync(NamingTemplate.ArtistAlbumNumberedTitle, progress: null);

        Assert.Equal(2, done!.Moved);
        Assert.Null(library.Db.GetMeta(LibraryService.OrganiseOwedKey));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_dir, "Tide", "Pools"), "*First*.mp3"));
        // Every track now lives under the template, and the folders the re-file emptied are gone.
        Assert.All(Directory.EnumerateFiles(_dir, "*.mp3", SearchOption.AllDirectories),
            f => Assert.StartsWith(Path.Combine(_dir, "Tide", "Pools"), f));
        Assert.False(Directory.Exists(Path.Combine(_dir, "Messy downloads")));
    }
}
