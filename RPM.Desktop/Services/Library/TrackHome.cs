using RPM.Core.Library;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Services.Library;

/// <summary>
/// Works out which collection a file belongs to: the one on screen, the user's own, or the shelf of
/// the computer that holds it.
///
/// <para>
/// Anything that follows the music, and not the library view, needs all three. A record played from a
/// friend's shelf keeps playing after the user switches back to their own collection. Its row is then
/// in neither of the two databases that the app is looking at. The question "what is this file, and
/// what else is on the record?" then gets the answer "nothing". The queue holds one track where a
/// whole album should be, and the info pane shows only a file name.
/// </para>
/// </summary>
public static class TrackHome
{
    /// <param name="Shelf">The collection to read the row from. Read only. A borrowed shelf belongs to
    /// another computer, and nothing writes to it.</param>
    /// <param name="Root">The folder that collection's relative paths are against.</param>
    /// <param name="Folder">Where in it this file is filed, "" for a record in the root.</param>
    /// <param name="OnScreen">Whether this is the collection the user is currently looking at.</param>
    public sealed record Home(LibraryDatabase Shelf, string Root, string Folder, bool OnScreen);

    public static Home? For(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return null;

        if (library.ShelfRoot is { } shelfRoot && FolderIn(shelfRoot, path) is { } onScreen)
            return new Home(library.Shelf, shelfRoot, onScreen, true);

        if (library.Root is { } root && FolderIn(root, path) is { } own)
            return new Home(library.Db, root, own, false);

        // A shelf that is not on screen. Its rows are the only record of this file here. The file is not
        // on this computer, so its tags cannot be read, and the computer that holds it may be switched off.
        if (PeerLibraryStore.DeviceIdFor(path) is { } deviceId
            && PeerLibraryStore.Exists(deviceId)
            && App.Services.GetService<PeerLibraryStore>() is { } guests
            && PeerLibraryStore.RootFor(deviceId) is { } theirRoot
            && FolderIn(theirRoot, path) is { } theirs
            && guests.Opened(deviceId) is { } shelf)
            return new Home(shelf, theirRoot, theirs, false);

        return null;
    }

    /// <summary>The collection's row for a file, or null when no collection knows it, for example a radio
    /// stream or a file dragged in from the desktop. Code that has only a path uses this. The row holds
    /// the artist, the album and the computer that holds the record.</summary>
    public static LibraryTrack? RowFor(string? path)
        => For(path) is { } home ? RowIn(home, path!) : null;

    /// <summary>The same, for a caller that already has the home and also wants the folder.</summary>
    public static LibraryTrack? RowIn(Home home, string path)
    {
        ArgumentNullException.ThrowIfNull(home);

        var fileName = Path.GetFileName(path);

        // An unanswered read gives no row, and the caller's own fallbacks still apply. A refusal from the
        // user's own mirror has shown the problem screen over the app (see LibraryService.Read).
        return App.Services.GetRequiredService<LibraryService>()
            .Read(home.Shelf, "a playing file's row", mirror => mirror.FolderTracks(home.Folder))
            .Match(rows => rows.FirstOrDefault(t => string.Equals(t.FileName, fileName, StringComparison.Ordinal)),
                _ => null);
    }

    /// <summary>The album folder a file sits in, relative to a collection's root, or null when the file
    /// is not under that root at all.</summary>
    private static string? FolderIn(string root, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return null;

        var relative = Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;

        return relative == "." ? "" : relative;
    }
}
