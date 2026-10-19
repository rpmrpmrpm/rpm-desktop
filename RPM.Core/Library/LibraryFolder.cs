namespace RPM.Core.Library;

/// <summary>What a folder offered as the central music folder turns out to be.</summary>
public enum LibraryFolderKind
{
    /// <summary>Nothing in it but hidden entries: a new collection.</summary>
    Empty,

    /// <summary>It already holds an rpm mirror, so opening it reopens that collection.</summary>
    ExistingLibrary,

    /// <summary>It holds files rpm did not put there, or it cannot be read.</summary>
    NotEmpty,
}

/// <summary>
/// The rule for which folders may become the central music folder. It is kept in one place because
/// both places that ask the user to choose a folder must give the same answer.
///
/// <para>Only <see cref="LibraryFolderKind.Empty"/> and <see cref="LibraryFolderKind.ExistingLibrary"/>
/// may be adopted. A folder with other files in it is refused. The first sync over a fresh mirror runs
/// with quarantine off (<see cref="LibrarySynchronizer"/>), so every audio file under the folder is
/// filed into the collection where it lies, and each one without an id gets an rpm id written into
/// its tags. That is the right thing to do to a folder the user gave the app, and the wrong thing to
/// do to a home directory.</para>
/// </summary>
public static class LibraryFolder
{
    public static LibraryFolderKind Classify(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return LibraryFolderKind.NotEmpty;

        if (LibraryDatabase.ExistsUnder(path))
            return LibraryFolderKind.ExistingLibrary;

        try
        {
            // Hidden entries (names starting with a dot, such as .DS_Store or a leftover .rpm folder) do
            // not make a folder count as in use.
            var visible = Directory.EnumerateFileSystemEntries(path)
                .Any(e => !Path.GetFileName(e).StartsWith('.'));
            return visible ? LibraryFolderKind.NotEmpty : LibraryFolderKind.Empty;
        }
        catch
        {
            // Missing, or unreadable. Either way it is not a folder to adopt.
            return LibraryFolderKind.NotEmpty;
        }
    }

    public static bool Adoptable(string path) =>
        Classify(path) is LibraryFolderKind.Empty or LibraryFolderKind.ExistingLibrary;

    /// <summary>
    /// Whether one of these folders is the other, or lies inside it.
    ///
    /// <para>Two mirrors that overlap claim the same files and stamp the same ids into them, and
    /// neither can see the other. The scanner skips dot-directories, so the nested <c>.rpm</c> is
    /// invisible to the outer collection, and the outer mirror's rows are invisible to the inner
    /// one.</para>
    /// </summary>
    public static bool Overlap(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        var left = WithSeparator(a);
        var right = WithSeparator(b);
        return left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithSeparator(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }
}
