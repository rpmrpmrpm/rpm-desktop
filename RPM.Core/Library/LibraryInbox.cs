namespace RPM.Core.Library;

/// <summary>
/// The library's inbox: a plainly named folder at the top of the collection that anybody may copy
/// music into. Everything in it is <b>waiting to be imported</b>, not part of the collection. The
/// mirror never looks inside it, and the import queue always does.
///
/// <para>The folder exists because the alternative has no good answer. A user who drags an album
/// straight into the library gets files that the app never named, never fingerprinted and never
/// filed. They sit in folders with whatever names the person who ripped them used, and by the time
/// anything notices, the tree is already wrong. Locking the folder down does not fix this, it only
/// moves the copy somewhere else. Giving the copy a proper place to land does, because the files then
/// arrive where the import pipeline already looks.</para>
///
/// <para>The name is deliberately not localized. It is a path on disk that files are moved into and
/// out of over the years. A folder that renames itself when the app's language changes would strand
/// everything already inside it.</para>
/// </summary>
public static class LibraryInbox
{
    /// <summary>The folder's name under the library root. It is visible on purpose: a hidden inbox would
    /// tell the user nothing about where to copy music.
    /// </summary>
    public const string FolderName = "Drop music here";

    private const string ReadmeName = "READ ME.txt";

    private static readonly string ReadmeText = string.Join(Environment.NewLine,
        "This folder is RPM's inbox.",
        "",
        "Copy or drag music in here — a file, an album, a whole drive's worth. RPM picks it up,",
        "works out what each track actually is, and files it into the collection under the names",
        "the collection already uses.",
        "",
        "Everything else in the library folder belongs to RPM. Music copied straight in there,",
        "rather than in here, gets moved into this folder so it can go through the same steps.",
        "",
        "Nothing in here is in your collection yet. Nothing in here is deleted on its own.");

    /// <summary>The inbox's full path under a library root. The folder need not exist.</summary>
    public static string PathFor(string libraryRoot) => Path.Combine(libraryRoot, FolderName);

    /// <summary>Creates the inbox if it is not there and returns its path. The note that explains the
    /// folder is written once, when the folder is created. A user who deletes the folder has read the note,
    /// so the app must not write it back on every launch.
    /// </summary>
    public static string Ensure(string libraryRoot)
    {
        var path = PathFor(libraryRoot);
        if (Directory.Exists(path))
            return path;

        Directory.CreateDirectory(path);
        try { File.WriteAllText(Path.Combine(path, ReadmeName), ReadmeText); }
        catch { /* the folder is what matters; a note that could not be written is not a failure */ }
        return path;
    }

    /// <summary>
    /// Whether a library-relative path (forward slashes, as the mirror stores them) is inside the
    /// inbox. This keeps the inbox out of the collection: the mirror never adds a row for anything
    /// under here, so a file waiting to be imported is never mistaken for one already filed.
    /// </summary>
    public static bool Contains(string relativePath) =>
        relativePath.StartsWith(FolderName + "/", StringComparison.OrdinalIgnoreCase)
        || relativePath.Equals(FolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a full path lies inside this library's inbox. The import asks this before it decides
    /// whether a file is being taken out of a staging area or copied from a source.
    /// </summary>
    public static bool IsInside(string libraryRoot, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(libraryRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            return Contains(relative);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Moves a file that turned up loose in the collection into the inbox. The folders it was under
    /// are kept, so a copied album stays an album. The queue groups tracks by folder, and a set
    /// flattened into one directory would lose which disc each track was on.
    /// </summary>
    /// <returns>The file's new full path, or null when it could not be moved (locked, or the inbox
    /// could not be created). In that case the caller must leave the file where it is.</returns>
    public static string? Quarantine(string libraryRoot, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(libraryRoot, fullPath);
            // A file from outside the root would otherwise get a path under the root built from "../..". No
            // caller passes such a file, and guessing where it belongs is worse than declining.
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return null;

            var inbox = Ensure(libraryRoot);
            var target = Path.Combine(inbox, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            target = FreeName(target);
            File.Move(fullPath, target);
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The path itself when nothing is there, or else the first "name (2).ext" that is free. The
    /// same album copied in twice does not overwrite its first copy. Both go in the queue, where the
    /// duplicate marks show which one is the better encode.
    /// </summary>
    private static string FreeName(string target)
    {
        if (!File.Exists(target))
            return target;

        var dir = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){extension}");
    }
}
