using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Scanning;

namespace RPM.Core.Import;

/// <summary>
/// Writes a folder image (cover.jpg) into every destination folder that contains audio
/// files, using the first track's embedded art. Folders that already have a cover /
/// folder image are left alone. Best effort: failures skip the folder.
/// </summary>
public static class FolderArtWriter
{
    /// <summary>
    /// What an album folder's picture is called, in the order the answer is preferred.
    ///
    /// <para>The first two are what this app writes, and what Windows has long used. The rest are what
    /// other people's rippers wrote. A collection that came from somewhere else is full of
    /// <c>front.jpg</c> and <c>albumart.jpg</c>. A sleeve under a name this list does not know would
    /// show as a blank card.</para>
    /// </summary>
    private static readonly string[] ExistingImageNames =
    [
        "cover.jpg", "cover.jpeg", "cover.png", "cover.webp",
        "folder.jpg", "folder.jpeg", "folder.png", "folder.webp",
        "front.jpg", "front.jpeg", "front.png",
        "album.jpg", "album.jpeg", "album.png",
        "albumart.jpg", "albumart.jpeg", "albumart.png",
        "albumartsmall.jpg",
    ];

    /// <summary>The picture extensions a folder image may have. Used for the last-resort look, where
    /// there is no name to go on.</summary>
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>Runs over the folders that received files in this apply run. Returns folders written.</summary>
    public static int WriteCovers(IEnumerable<ApplyResult> results)
    {
        var folders = results
            .Where(r => r.Success)
            .Select(r => Path.GetDirectoryName(r.DestinationPath))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var written = 0;
        foreach (var folder in folders)
        {
            try
            {
                if (!Directory.Exists(folder) || HasFolderImage(folder))
                    continue;

                foreach (var audio in Directory.EnumerateFiles(folder)
                             .Where(f => AudioFileScanner.AudioExtensions.Contains(Path.GetExtension(f)))
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (TagReader.LoadArt(audio) is not { } art)
                        continue;
                    var name = art.Mime.Contains("png", StringComparison.OrdinalIgnoreCase) ? "cover.png" : "cover.jpg";
                    File.WriteAllBytes(Path.Combine(folder, name), art.Bytes);
                    written++;
                    break;
                }
            }
            catch
            {
                // best effort per folder
            }
        }
        return written;
    }

    private static bool HasFolderImage(string folder) => FindFolderImage(folder) is not null;

    /// <summary>
    /// The folder's image file, or null. This is shared with the art display, which falls back to it
    /// when a track has no embedded art.
    ///
    /// <para>By name first (<see cref="ExistingImageNames"/>), and then, for a folder holding exactly one
    /// picture whatever it is called, that picture. One image beside an album's tracks is that album's
    /// sleeve. It is only when there are several that a name has to say which, and that is the case this
    /// declines to guess at.</para>
    ///
    /// <para>A folder that is not there is an ordinary answer, not a failure. An offloaded or adopted
    /// record is a row in the library with nothing on this disk behind it, and the album wall asks this
    /// for every card it draws. The folder is tested for, rather than left to the catch below, so a shelf
    /// of cloud records does not throw once per record.</para>
    /// </summary>
    public static string? FindFolderImage(string folder)
    {
        if (!Directory.Exists(folder))
            return null;

        foreach (var name in ExistingImageNames)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
                return path;
        }

        try
        {
            string? only = null;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (!ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    continue;

                // The record's own label is a picture in this folder, and it is not the cover.
                if (DiscImageNames.Contains(Path.GetFileName(file).ToLowerInvariant()))
                    continue;

                if (only is not null)
                    return null;    // two pictures and no name to choose by

                only = file;
            }

            return only;
        }
        catch
        {
            return null;   // an unreadable folder is a folder with no picture in it
        }
    }

    private static readonly string[] DiscImageNames = ["disc.png", "disc.jpg", "disc.jpeg"];

    /// <summary>The album folder's disc art (disc.png|jpg|jpeg), or null. This is a transparent PNG of the
    /// record itself, which the turntable shows in place of the drawn disc. It is folder-driven, like the
    /// cover: written at import, read locally while playing, and never looked up dynamically.</summary>
    public static string? FindDiscImage(string folder)
    {
        foreach (var name in DiscImageNames)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>Writes (or replaces) the album folder's cover image as cover.jpg. Unlike the import-time
    /// writers, this is an explicit user request (the "Get Album Artwork" action). So it replaces any
    /// existing folder image, and a chosen cover is never hidden behind an older one. Best effort.
    /// Returns whether a cover was written.</summary>
    public static bool WriteCover(string folder, byte[] bytes)
    {
        try
        {
            if (!Directory.Exists(folder) || bytes.Length == 0)
                return false;
            // Clear any other folder image first. A leftover cover.png would otherwise win the lookup
            // order, and the new cover would never show.
            foreach (var name in ExistingImageNames)
            {
                var existing = Path.Combine(folder, name);
                if (File.Exists(existing))
                    try { File.Delete(existing); } catch { /* keep going: the new write is what matters */ }
            }
            File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Takes the album folder's cover image away. Every spelling of it is removed, so the one
    /// that is left is not simply the next name down the lookup order. This is the counterpart of the
    /// tag-side clear (an empty <c>PendingArt</c>). A cover lives in two places, and removing it from one
    /// only would leave the wall showing a picture the files no longer carry. Best effort. Returns whether
    /// anything went.</summary>
    public static bool DeleteCover(string folder)
    {
        bool gone = false;
        foreach (var name in ExistingImageNames)
        {
            var path = Path.Combine(folder, name);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    gone = true;
                }
            }
            catch
            {
                // A file that will not go leaves the rest of the removal to still happen.
            }
        }
        return gone;
    }

    /// <summary>Writes the fetched scan into the album folder as its cover image, unless one is already
    /// there. This is the full-size tier. The copy inside the track files is scaled down, so the folder
    /// image is deliberately not read back out of them. Best effort.</summary>
    /// <remarks>Runs at rip time, into the staging folder. <see cref="CarryCoverImages"/> brings it to
    /// the final album folder at import, the same route disc art takes.</remarks>
    public static void WriteFolderCover(string folder, byte[] bytes, string? mime)
    {
        try
        {
            if (!Directory.Exists(folder) || bytes.Length == 0 || HasFolderImage(folder))
                return;
            var name = mime is { Length: > 0 } m && m.Contains("png", StringComparison.OrdinalIgnoreCase)
                ? "cover.png"
                : "cover.jpg";
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Carries a ripped album's cover image from the staging folders to the final album folders
    /// at import. This is the sidecar that the organiser's per-file moves would otherwise leave behind, and
    /// it is the only full-size copy there is. It must run before <see cref="WriteCovers"/>, which would
    /// otherwise fill the folder with the scaled copy read back out of a track. It never overwrites a cover
    /// already there.</summary>
    public static void CarryCoverImages(IEnumerable<ApplyResult> results) =>
        CarrySidecars(results, FindFolderImage);

    /// <summary>Writes disc art into the album folder as disc.png, unless one is already there. Best
    /// effort. A folder that cannot be written is simply left without disc art.</summary>
    public static void WriteDisc(string folder, byte[] bytes)
    {
        try
        {
            if (Directory.Exists(folder) && FindDiscImage(folder) is null)
                File.WriteAllBytes(Path.Combine(folder, "disc.png"), bytes);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Writes (or replaces) the album folder's disc art as disc.png. This is the counterpart of
    /// <see cref="WriteCover"/>. <see cref="WriteDisc"/> is the import-time writer that yields to whatever
    /// is already there. This method is used when somebody explicitly puts this picture on this record, so
    /// a stale disc.jpg cannot go on winning the lookup order. Best effort. Returns whether one was written.
    /// </summary>
    public static bool ReplaceDisc(string folder, byte[] bytes)
    {
        try
        {
            if (!Directory.Exists(folder) || bytes.Length == 0)
                return false;
            foreach (var name in DiscImageNames)
            {
                var existing = Path.Combine(folder, name);
                if (File.Exists(existing))
                    try { File.Delete(existing); } catch { /* keep going: the new write is what matters */ }
            }
            File.WriteAllBytes(Path.Combine(folder, "disc.png"), bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Takes the album folder's disc art away. Every spelling of it is removed, so the one that is
    /// left is not simply the next name down the lookup order. Best effort. Returns whether anything went.
    /// </summary>
    public static bool DeleteDisc(string folder)
    {
        bool gone = false;
        foreach (var name in DiscImageNames)
        {
            var path = Path.Combine(folder, name);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    gone = true;
                }
            }
            catch
            {
                // A file that will not go leaves the rest of the removal to still happen.
            }
        }
        return gone;
    }

    /// <summary>Carries a ripped disc's disc.png from the staging folders to the final album folders at
    /// import. This is the sidecar that the organiser's per-file moves would otherwise leave behind. Best
    /// effort. It never overwrites a disc image already in the destination.</summary>
    public static void CarryDiscImages(IEnumerable<ApplyResult> results) =>
        CarrySidecars(results, FindDiscImage);

    /// <summary>The shared move. For every destination folder this run wrote to, it finds the matching image
    /// beside one of the sources and copies it across. A destination that already has one is left alone.
    /// An album the user already gave art to outranks anything a rip staged.</summary>
    private static void CarrySidecars(IEnumerable<ApplyResult> results, Func<string, string?> find)
    {
        foreach (var group in results
                     .Where(r => r.Success)
                     .GroupBy(r => Path.GetDirectoryName(r.DestinationPath))
                     .Where(g => !string.IsNullOrEmpty(g.Key)))
        {
            try
            {
                var dest = group.Key!;
                if (!Directory.Exists(dest) || find(dest) is not null)
                    continue;

                foreach (var result in group)
                {
                    if (Path.GetDirectoryName(result.SourcePath) is { } src
                        && find(src) is { } image)
                    {
                        File.Copy(image, Path.Combine(dest, Path.GetFileName(image)), overwrite: false);
                        break;
                    }
                }
            }
            catch
            {
                // best effort per folder
            }
        }
    }
}
