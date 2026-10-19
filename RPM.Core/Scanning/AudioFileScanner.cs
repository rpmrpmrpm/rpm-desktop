namespace RPM.Core.Scanning;

/// <summary>What a scan found.</summary>
/// <param name="Files">Full paths of the audio files found, sorted.</param>
/// <param name="Unreadable">Folders that could not be listed. Examples: no permission, a network share that
/// dropped, or a folder removed during the scan. What is under them is unknown, not absent. A caller that
/// treats a missing file as a deleted one must not do that for anything under these folders.</param>
public sealed record ScanResult(IReadOnlyList<string> Files, IReadOnlyList<string> Unreadable);

/// <summary>Enumerates audio files under a root folder, recursively.</summary>
public static class AudioFileScanner
{
    /// <summary>Extensions we treat as music, lower-case with dot.</summary>
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus",
        ".wav", ".aif", ".aiff", ".wma", ".alac", ".ape", ".wv", ".mpc",
    };

    /// <summary>
    /// Finds all audio files under <paramref name="root"/>, and names the folders it could not list.
    /// Hidden files and folders (names that start with a dot) are skipped. Nothing else is hidden. What is on
    /// disk is what the queue shows. That includes the albums that a split kept in
    /// <see cref="Splitting.AlbumFileSplitter.OriginalsFolderName"/>.
    ///
    /// <para>The unreadable folders are part of the answer, not skipped quietly. The library sync reads
    /// "not found" as "deleted". Without this, a folder the scanner could not list would remove every row under it.</para>
    /// </summary>
    public static ScanResult Scan(string root)
    {
        var results = new List<string>();
        var unreadable = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            string[] subdirs, files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // DirectoryNotFoundException is an IOException. A folder that disappears during the scan is still
                // recorded, so the next scan can see that it is gone.
                unreadable.Add(dir);
                continue;
            }

            foreach (string sub in subdirs)
            {
                string subName = Path.GetFileName(sub);
                if (subName.StartsWith('.'))
                    continue;
                // The splitter's Originals folder is NOT skipped. The album a split was made from is what you need
                // in front of you when the split comes out wrong. A file the scanner hides is a file the app cannot
                // offer to cut again.
                pending.Push(sub);
            }

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith('.'))
                {
                    // A name with a dot at the start is not junk. iCloud replaces an evicted "Track.mp3" with a stub
                    // called ".Track.mp3.icloud". Skipping the stub would lose the track: no row, no error, and a folder
                    // that quietly holds fewer records than it should. The track's own path goes in the list. That file
                    // is not on disk, and TagReader answers for it (see CloudFile.IsDataless).
                    if (Files.CloudFile.IsICloudStub(file)
                        && Files.CloudFile.TrackBehindStub(file) is { } evicted
                        && AudioExtensions.Contains(Path.GetExtension(evicted)))
                        results.Add(evicted);
                    continue;
                }
                if (AudioExtensions.Contains(Path.GetExtension(file)))
                    results.Add(file);
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return new ScanResult(results, unreadable);
    }
}
