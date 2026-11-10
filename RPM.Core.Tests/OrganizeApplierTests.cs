using RPM.Core.Analysis;
using RPM.Core.Import;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class OrganizeApplierTests : IDisposable
{
    private readonly string _root;
    private readonly string _src;
    private readonly string _dest;

    public OrganizeApplierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}");
        _src = Path.Combine(_root, "src");
        _dest = Path.Combine(_root, "dest");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dest);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TrackSuggestion Suggestion(string sourceName, string relativeTarget)
    {
        string source = Path.Combine(_src, sourceName);
        File.WriteAllText(source, "audio-bytes");
        return new TrackSuggestion
        {
            Track = new TrackInfo { FilePath = source, Extension = Path.GetExtension(sourceName) },
            ProposedRelativePath = relativeTarget,
        };
    }

    [Fact]
    public void Move_relocates_file()
    {
        var s = Suggestion("a.mp3", "Muse/Absolution/08 - Hysteria.mp3");
        var result = OrganizeApplier.Apply(s, _dest, OrganizeMode.Move, writeTags: false);

        Assert.True(result.Success);
        Assert.False(File.Exists(s.Track.FilePath));
        Assert.True(File.Exists(Path.Combine(_dest, "Muse", "Absolution", "08 - Hysteria.mp3")));
    }

    [Fact]
    public void Copy_keeps_original()
    {
        var s = Suggestion("a.mp3", "Muse/x.mp3");
        var result = OrganizeApplier.Apply(s, _dest, OrganizeMode.Copy, writeTags: false);

        Assert.True(result.Success);
        Assert.True(File.Exists(s.Track.FilePath));
        Assert.True(File.Exists(Path.Combine(_dest, "Muse", "x.mp3")));
    }

    [Fact]
    public void Copy_is_written_under_a_hidden_name_and_renamed_when_complete()
    {
        // A copy stopped part-way (the app quit) must never sit under the real name: the library would
        // read it as the song, cut off.
        var s = Suggestion("a.mp3", "Muse/x.mp3");
        string? target = null;

        var result = OrganizeApplier.Apply(s, _dest, OrganizeMode.Copy, writeTags: false, (from, to) =>
        {
            target = to;
            File.Copy(from, to);
        });

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_dest, "Muse"), Path.GetDirectoryName(target));
        Assert.StartsWith(".", Path.GetFileName(target));
        Assert.Equal("audio-bytes", File.ReadAllText(Path.Combine(_dest, "Muse", "x.mp3")));
        Assert.Equal(["x.mp3"], Directory.GetFiles(Path.Combine(_dest, "Muse")).Select(Path.GetFileName));
    }

    [Fact]
    public void Failed_copy_never_removes_a_file_another_import_put_at_the_destination()
    {
        var s = Suggestion("a.mp3", "Muse/x.mp3");
        var destination = Path.Combine(_dest, "Muse", "x.mp3");

        var result = OrganizeApplier.Apply(s, _dest, OrganizeMode.Copy, writeTags: false, (from, to) =>
        {
            File.WriteAllText(destination, "the other import's copy");   // lands while this copy runs
            File.Copy(from, to);
        });

        Assert.False(result.Success);
        Assert.Equal("the other import's copy", File.ReadAllText(destination));
        Assert.Equal(["x.mp3"], Directory.GetFiles(Path.Combine(_dest, "Muse")).Select(Path.GetFileName));
    }

    [Fact]
    public void Old_hidden_copies_left_by_a_stop_are_removed_from_the_folder()
    {
        var folder = Path.Combine(_dest, "Muse");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, ".rpm-copy-left.mp3");
        var fresh = Path.Combine(folder, ".rpm-copy-running.mp3");
        File.WriteAllText(old, "half");
        File.WriteAllText(fresh, "half");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));

        OrganizeApplier.Apply(Suggestion("a.mp3", "Muse/x.mp3"), _dest, OrganizeMode.Copy, writeTags: false);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Never_overwrites_existing_destination()
    {
        var s = Suggestion("a.mp3", "taken.mp3");
        string taken = Path.Combine(_dest, "taken.mp3");
        File.WriteAllText(taken, "original");

        var result = OrganizeApplier.Apply(s, _dest, OrganizeMode.Move, writeTags: false);

        Assert.False(result.Success);
        Assert.Equal("original", File.ReadAllText(taken));
        Assert.True(File.Exists(s.Track.FilePath));
    }

    [Fact]
    public void InPlace_suggestion_is_a_noop_success()
    {
        string path = Path.Combine(_dest, "keep.mp3");
        File.WriteAllText(path, "x");
        var s = new TrackSuggestion
        {
            Track = new TrackInfo { FilePath = path, Extension = ".mp3" },
            ProposedRelativePath = "keep.mp3",
            IsAlreadyInPlace = true,
        };

        var result = OrganizeApplier.Apply(s, _dest, OrganizeMode.Move, writeTags: false);
        Assert.True(result.Success);
        Assert.True(File.Exists(path));
    }

    /// <summary>A minimal valid MPEG-1 Layer III frame (silence): enough for TagLib to accept the
    /// file, which the tag half of an import needs and a text stub cannot provide.</summary>
    private string CreateMp3(string name, TrackInfo tags)
    {
        var path = Path.Combine(_src, name);
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;   // 128 kbps 44.1 kHz
        File.WriteAllBytes(path, frame);
        TagReader.WriteAll(path, tags with { FilePath = path });
        return path;
    }

    [Fact]
    public void A_deduced_album_artist_reaches_the_file_and_not_only_the_folder()
    {
        // A compilation tagged by hand: every track names its own artist, none names the record's.
        // The deduced album artist must be WRITTEN to the file. Otherwise the file is filed under
        // "Various Artists/" but its tags still say nothing, and the mirror (which re-reads the file)
        // agrees with the file.
        var scanned = new[] { ("1.mp3", "Snap!"), ("2.mp3", "The Shamen") }
            .Select(t => TagReader.Read(CreateMp3(t.Item1, new TrackInfo
            {
                FilePath = "",
                Extension = ".mp3",
                Title = Path.GetFileNameWithoutExtension(t.Item1),
                Artist = t.Item2,
                Album = "Now 22",
            })))
            .ToList();

        Assert.All(scanned, t => Assert.Equal(MetadataSource.Tags, t.Source));
        Assert.All(scanned, t => Assert.Null(t.AlbumArtist));

        var filled = AlbumArtistFill.Fill(scanned);
        foreach (var track in filled)
        {
            var result = OrganizeApplier.Apply(
                new TrackSuggestion
                {
                    Track = track,
                    ProposedRelativePath = $"Various Artists/Now 22/{Path.GetFileName(track.FilePath)}",
                },
                _dest, OrganizeMode.Move);
            Assert.True(result.Success);

            var landed = TagReader.Read(result.DestinationPath);
            Assert.Equal(AlbumResolver.VariousArtists, landed.AlbumArtist);
            Assert.Equal(track.Artist, landed.Artist);   // the track's own artist is untouched
        }
    }

    [Fact]
    public void CleanupEmptyFolders_removes_empty_tree_but_keeps_root()
    {
        string deep = Path.Combine(_src, "a", "b", "c");
        Directory.CreateDirectory(deep);
        string keeper = Path.Combine(_src, "d");
        Directory.CreateDirectory(keeper);
        File.WriteAllText(Path.Combine(keeper, "f.txt"), "x");

        OrganizeApplier.CleanupEmptyFolders(_src);

        Assert.True(Directory.Exists(_src));
        Assert.False(Directory.Exists(Path.Combine(_src, "a")));
        Assert.True(File.Exists(Path.Combine(keeper, "f.txt")));
    }
}
