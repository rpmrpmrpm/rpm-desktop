using RPM.Core.Library;

namespace RPM.Core.Tests;

public class LibraryInboxTests : IDisposable
{
    private readonly string _root;

    public LibraryInboxTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpminbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Touch(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Ensure_creates_the_folder_and_explains_itself()
    {
        var inbox = LibraryInbox.Ensure(_root);

        Assert.True(Directory.Exists(inbox));
        Assert.Equal(Path.Combine(_root, LibraryInbox.FolderName), inbox);
        Assert.NotEmpty(Directory.GetFiles(inbox, "*.txt"));
    }

    /// <summary>The note is written when the folder is created, and not written again later: a user
    /// who deleted it has read it.</summary>
    [Fact]
    public void Ensure_does_not_rewrite_the_note_it_already_wrote()
    {
        var inbox = LibraryInbox.Ensure(_root);
        foreach (var note in Directory.GetFiles(inbox, "*.txt"))
            File.Delete(note);

        LibraryInbox.Ensure(_root);

        Assert.Empty(Directory.GetFiles(inbox, "*.txt"));
    }

    [Theory]
    [InlineData("Drop music here/Nirvana/01.mp3", true)]
    [InlineData("drop MUSIC here/01.mp3", true)]
    [InlineData("Drop music here", true)]
    [InlineData("Nirvana/Nevermind/01.mp3", false)]
    [InlineData("Drop music here elsewhere/01.mp3", false)]
    public void Contains_knows_what_is_waiting_and_what_is_filed(string relative, bool inside)
        => Assert.Equal(inside, LibraryInbox.Contains(relative));

    [Fact]
    public void IsInside_answers_for_full_paths()
    {
        Assert.True(LibraryInbox.IsInside(_root, Path.Combine(_root, LibraryInbox.FolderName, "a.mp3")));
        Assert.False(LibraryInbox.IsInside(_root, Path.Combine(_root, "Nirvana", "a.mp3")));
    }

    /// <summary>The folders of a copied album are kept: the queue groups by them, and a set put into
    /// one directory would lose which disc each track was on.</summary>
    [Fact]
    public void Quarantine_keeps_the_folders_the_file_was_dropped_under()
    {
        var dropped = Touch("Nirvana/Nevermind/01 - Smells Like Teen Spirit.mp3");

        var landed = LibraryInbox.Quarantine(_root, dropped);

        Assert.NotNull(landed);
        Assert.False(File.Exists(dropped));
        Assert.Equal(
            Path.Combine(_root, LibraryInbox.FolderName, "Nirvana", "Nevermind",
                "01 - Smells Like Teen Spirit.mp3"),
            landed);
        Assert.True(File.Exists(landed));
    }

    /// <summary>The same album copied in twice does not overwrite its first copy. Both go in the
    /// queue, where the duplicate marks show which is the better encode.</summary>
    [Fact]
    public void Quarantine_does_not_overwrite_a_copy_already_waiting()
    {
        LibraryInbox.Quarantine(_root, Touch("Nirvana/01.mp3", "first"));
        var second = LibraryInbox.Quarantine(_root, Touch("Nirvana/01.mp3", "second"));

        Assert.Equal(Path.Combine(_root, LibraryInbox.FolderName, "Nirvana", "01 (2).mp3"), second);
        Assert.Equal("first", File.ReadAllText(Path.Combine(_root, LibraryInbox.FolderName, "Nirvana", "01.mp3")));
        Assert.Equal("second", File.ReadAllText(second!));
    }

    /// <summary>A path outside the library would be rebuilt under the inbox from "../..". The app never
    /// does this, and guessing where the file belongs is worse than refusing.</summary>
    [Fact]
    public void Quarantine_refuses_a_file_from_outside_the_library()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"rpmoutside-{Guid.NewGuid():N}.mp3");
        File.WriteAllText(outside, "x");
        try
        {
            Assert.Null(LibraryInbox.Quarantine(_root, outside));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
