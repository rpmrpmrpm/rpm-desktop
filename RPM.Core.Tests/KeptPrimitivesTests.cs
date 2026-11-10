using System.Security.Cryptography;
using RPM.Core.Analysis;
using RPM.Core.Files;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// Every primitive that changes a file in place, run against a kept library folder. Each test takes a
/// snapshot of the folder (every path and its SHA-256, outside <c>.rpm</c>) and checks that nothing that
/// was there has changed. New files may appear only where the test adds music.
/// </summary>
public class KeptPrimitivesTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"rpm-kept-prim-{Guid.NewGuid():N}");
    private readonly string _root;
    private readonly string _song;

    public KeptPrimitivesTests()
    {
        _root = Path.Combine(_temp, "Music");
        _song = Path.Combine(_root, "Artist", "Album", "01 Song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(_song)!);
        File.WriteAllBytes(_song, MinimalMp3());
        File.WriteAllBytes(Path.Combine(_root, "Artist", "Album", "folder.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, "Artist", "Album", "disc.png"), [4, 5, 6]);
        Directory.CreateDirectory(Path.Combine(_root, "Empty on purpose"));
        KeptFiles.Open(_root);
    }

    public void Dispose()
    {
        KeptFiles.Close(_root);
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private static byte[] MinimalMp3()
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        return frame;
    }

    /// <summary>Every file and folder under the root, outside .rpm, with the SHA-256 of each file.</summary>
    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(_root, p).StartsWith(".rpm", StringComparison.Ordinal))
            .ToDictionary(
                p => Path.GetRelativePath(_root, p),
                p => File.Exists(p) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) : "folder");

    [Fact]
    public void Tag_writers_refuse_a_kept_file()
    {
        var before = Snapshot();
        var info = new TrackInfo { FilePath = _song, Title = "Changed", Extension = ".mp3" };

        Assert.Throws<KeptFileException>(() => TagReader.Write(_song, info));
        Assert.Throws<KeptFileException>(() => TagReader.WriteAll(_song, info));
        Assert.Throws<KeptFileException>(() => TagReader.StampRpmId(_song));
        Assert.Throws<KeptFileException>(() => TagReader.WriteRpmId(_song, RpmTag.New()));
        Assert.Throws<KeptFileException>(() => TagReader.WriteAnalysisTags(_song, new TrackAnalysis { FilePath = _song, Bpm = 120 }));

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Moving_tagging_or_refiling_a_kept_file_is_refused()
    {
        var before = Snapshot();
        var inPlace = new TrackSuggestion
        {
            Track = new TrackInfo { FilePath = _song, Extension = ".mp3", Title = "x" },
            ProposedRelativePath = "Somewhere/Else.mp3",
        };

        var result = OrganizeApplier.Apply(inPlace, _root, OrganizeMode.Move, writeTags: true);
        Assert.False(result.Success);

        using (var db = new LibraryDatabase(MirrorLocation.InFolder(_root)))
        {
            Assert.Throws<KeptFileException>(() => LibraryRefiler.Refile(db, _root, NamingTemplate.ArtistAlbumNumberedTitle));
            Assert.Throws<KeptFileException>(() => LibraryRefiler.FollowFolders(db, _root, new()));
        }

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Picture_edits_are_kept_in_rpm_data_and_read_before_the_folders_own()
    {
        var before = Snapshot();
        var album = Path.GetDirectoryName(_song)!;
        var chosen = Path.Combine(_root, ".rpm", "art", "Artist", "Album");

        Assert.EndsWith("folder.jpg", FolderArtWriter.FindFolderImage(album));
        Assert.True(FolderArtWriter.WriteCover(album, [9, 9]));
        Assert.Equal(Path.Combine(chosen, "cover.jpg"), FolderArtWriter.FindFolderImage(album));
        Assert.True(FolderArtWriter.DeleteCover(album));
        Assert.Null(FolderArtWriter.FindFolderImage(album));
        Assert.True(FolderArtWriter.WriteCover(album, [8, 8]));
        Assert.Equal([8, 8], File.ReadAllBytes(FolderArtWriter.FindFolderImage(album)!));

        Assert.True(FolderArtWriter.ReplaceDisc(album, [7, 7]));
        Assert.Equal(Path.Combine(chosen, "disc.png"), FolderArtWriter.FindDiscImage(album));
        Assert.True(FolderArtWriter.DeleteDisc(album));
        Assert.Null(FolderArtWriter.FindDiscImage(album));

        Assert.Equal(before, Snapshot());

        // Closed, the library is not kept here any more, and the folder's own pictures are what is read.
        KeptFiles.Close(_root);
        Assert.EndsWith("folder.jpg", FolderArtWriter.FindFolderImage(album));
        KeptFiles.Open(_root);
    }

    [Fact]
    public void Trash_inbox_and_the_empty_folder_sweep_leave_the_folder_alone()
    {
        var before = Snapshot();
        Assert.False(OsTrash.Trash(_song));
        OrganizeApplier.CleanupEmptyFolders(_root);
        Assert.Throws<KeptFileException>(() => LibraryInbox.Quarantine(_root, _song));
        Assert.Throws<KeptFileException>(() => LibraryInbox.Ensure(_root));

        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData(OrganizeMode.Copy)]
    [InlineData(OrganizeMode.Move)]
    public void New_music_is_added_with_its_tags_and_nothing_else_changes(OrganizeMode mode)
    {
        var before = Snapshot();
        var outside = Path.Combine(_temp, "Downloads", "new.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, MinimalMp3());
        var suggestion = new TrackSuggestion
        {
            Track = new TrackInfo
            {
                FilePath = outside, Extension = ".mp3", Title = "New", Artist = "Someone",
                Source = MetadataSource.Manual,
            },
            ProposedRelativePath = "Someone/New Album/01 New.mp3",
        };

        var result = OrganizeApplier.Apply(suggestion, _root, mode, writeTags: true);

        Assert.True(result.Success, result.Error);
        var added = Path.Combine(_root, "Someone", "New Album", "01 New.mp3");
        Assert.True(RpmTag.IsId(TagReader.Read(added).RpmId));
        Assert.Equal("New", TagReader.Read(added).Title);
        Assert.Equal(mode == OrganizeMode.Copy, File.Exists(outside));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(added)!, ".rpm-*"));

        var after = Snapshot();
        foreach (var (path, hash) in before)
            Assert.Equal(hash, after[path]);
        Assert.Equal(
            new[] { Path.Combine("Someone"), Path.Combine("Someone", "New Album"), Path.Combine("Someone", "New Album", "01 New.mp3") }
                .Order(),
            after.Keys.Except(before.Keys).Order());
    }

    [Fact]
    public void A_file_already_at_the_destination_is_never_replaced_and_the_source_stays()
    {
        var outside = Path.Combine(_temp, "Downloads", "only-copy.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, MinimalMp3());
        var target = Path.Combine(_root, "Artist", "Album", "01 Song.mp3");
        var before = Snapshot();
        var suggestion = new TrackSuggestion
        {
            Track = new TrackInfo { FilePath = outside, Extension = ".mp3", Title = "x", Source = MetadataSource.Manual },
            ProposedRelativePath = "Artist/Album/01 Song.mp3",
        };

        var result = OrganizeApplier.Apply(suggestion, _root, OrganizeMode.Move, writeTags: true);

        Assert.False(result.Success);
        Assert.True(File.Exists(outside));
        Assert.Equal(before, Snapshot());
        Assert.Equal(MinimalMp3(), File.ReadAllBytes(target));
    }

    [Fact]
    public void A_moved_file_whose_last_rename_fails_goes_back_where_it_came_from()
    {
        // A folder under the destination's name: the check for an existing file passes, the move to the
        // hidden name and the tag write happen, and the rename into place fails.
        var outside = Path.Combine(_temp, "Downloads", "only-copy.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, MinimalMp3());
        Directory.CreateDirectory(Path.Combine(_root, "Someone", "Blocked.mp3"));
        var suggestion = new TrackSuggestion
        {
            Track = new TrackInfo { FilePath = outside, Extension = ".mp3", Title = "x", Source = MetadataSource.Manual },
            ProposedRelativePath = "Someone/Blocked.mp3",
        };

        var result = OrganizeApplier.Apply(suggestion, _root, OrganizeMode.Move, writeTags: true);

        Assert.False(result.Success);
        Assert.True(File.Exists(outside));
        Assert.True(new FileInfo(outside).Length >= MinimalMp3().Length);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "Someone"), ".rpm-*"));
    }
}
