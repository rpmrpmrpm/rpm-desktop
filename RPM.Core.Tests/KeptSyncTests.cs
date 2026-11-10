using System.Security.Cryptography;
using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>The catalogue read of a kept folder: every file gets a row with an id, and no file in the
/// folder changes, on the first pass and on later ones.</summary>
public class KeptSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rpm-kept-sync-{Guid.NewGuid():N}");

    public KeptSyncTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        KeptFiles.Close(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static byte[] MinimalMp3()
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        return frame;
    }

    private string Put(string relative, byte[] bytes)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(_root, p).StartsWith(".rpm", StringComparison.Ordinal))
            .ToDictionary(p => Path.GetRelativePath(_root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    [Fact]
    public void A_kept_folder_is_read_without_writing_and_ids_are_stable()
    {
        Put("Plain/Album/01 No id.mp3", MinimalMp3());
        var tagged = Put("Tagged/Album/01 Has id.mp3", MinimalMp3());
        var knownId = TagReader.StampRpmId(tagged)!;   // tagged before the folder became kept
        File.Copy(tagged, Path.Combine(_root, "Tagged", "Album", "02 Copy of it.mp3"));
        Put("Broken/garbage.mp3", [1, 2, 3, 4]);
        Directory.CreateDirectory(Path.Combine(_root, "Empty folder"));

        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        FileKeeping.Write(_root, db, FileHandling.Kept);
        KeptFiles.Open(_root);
        var before = Snapshot();

        // quarantineForeign is on, as the app runs it. A kept folder must still take everything in place.
        LibrarySynchronizer.Sync(db, quarantineForeign: true);
        var first = db.Query().ToDictionary(t => t.RelativePath, t => t.RpmId);

        Assert.Equal(before, Snapshot());
        Assert.Equal(4, first.Count);
        Assert.All(first.Values, id => Assert.True(RpmTag.IsId(id)));
        Assert.Equal(knownId, first["Tagged/Album/01 Has id.mp3"]);
        Assert.NotEqual(knownId, first["Tagged/Album/02 Copy of it.mp3"]);
        Assert.Equal(first.Count, first.Values.Distinct().Count());
        var libraryId = MirrorLocation.LibraryId.Read(_root)!;
        Assert.Equal(KeptIds.For(libraryId, "Plain/Album/01 No id.mp3"), first["Plain/Album/01 No id.mp3"]);

        // A later pass, with a file that arrived meanwhile: an organised library would quarantine it.
        Put("Later/Album/01 New.mp3", MinimalMp3());
        var beforeSecond = Snapshot();
        var result = LibrarySynchronizer.Sync(db, quarantineForeign: true);

        Assert.Equal(0, result.Quarantined);
        Assert.Equal(beforeSecond, Snapshot());
        Assert.True(Directory.Exists(Path.Combine(_root, "Empty folder")));
        Assert.False(Directory.Exists(LibraryInbox.PathFor(_root)));
        var second = db.Query().ToDictionary(t => t.RelativePath, t => t.RpmId);
        Assert.Equal(5, second.Count);
        foreach (var (path, id) in first)
            Assert.Equal(id, second[path]);
    }

    [Fact]
    public void An_organised_folder_still_gets_ids_written()
    {
        var plain = Put("Plain/Album/01 No id.mp3", MinimalMp3());
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));

        LibrarySynchronizer.Sync(db);

        Assert.True(RpmTag.IsId(TagReader.Read(plain).RpmId));
    }
}
