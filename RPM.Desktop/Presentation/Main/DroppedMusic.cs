using System.IO;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// What a drop from Finder or Explorer means to the import queue: which of the dropped items are folders
/// worth scanning.
///
/// <para>Only folders. A scan reads a whole folder tree (see
/// <see cref="RPM.Core.Scanning.AudioFileScanner"/>). Loose files have no folder that could stand for them
/// without also pulling in every other file in that folder, which the user did not drop.</para>
/// </summary>
public static class DroppedMusic
{
    /// <summary>
    /// The folders to scan, in the order they were dropped: existing directories, each named once, and none
    /// of them inside another one that was dropped too.
    ///
    /// <para>The nesting rule stops a drop of "an album and the artist folder it lives in" from reading the
    /// same files twice. The queue removes duplicate rows later, but only after both scans have run.</para>
    /// </summary>
    public static IReadOnlyList<string> FoldersIn(IEnumerable<string?> droppedPaths)
    {
        var folders = new List<string>();
        foreach (var path in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                continue;

            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!folders.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)))
                folders.Add(full);
        }

        return folders.Where(f => !folders.Any(other => IsInside(other, f))).ToList();
    }

    /// <summary>Whether <paramref name="path"/> lies under <paramref name="ancestor"/>. The test is strict:
    /// a folder is not inside itself, and "Live" is not inside "Live Sessions".</summary>
    private static bool IsInside(string ancestor, string path)
        => path.Length > ancestor.Length
           && path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
           && path[ancestor.Length] == Path.DirectorySeparatorChar;
}
