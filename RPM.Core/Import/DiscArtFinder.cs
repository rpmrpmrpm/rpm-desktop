using System.Text.RegularExpressions;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Import;

/// <summary>Where a record's picture was found.</summary>
public enum ArtOrigin
{
    /// <summary>Nowhere yet.</summary>
    None,

    /// <summary>Inside the music files.</summary>
    Files,

    /// <summary>An image file in the record's folder.</summary>
    Folder,
}

/// <summary>A picture found for a record, and where it was found.</summary>
public sealed record FoundPicture(byte[] Bytes, ArtOrigin Origin);

/// <summary>
/// Finds a picture of a record's disc (the CD or the record label) that came with the record: a scan beside the
/// files, or a picture inside them.
///
/// <para><b>Beside the files.</b> First the names rpm itself writes (disc.png, disc.jpg, see
/// <see cref="FolderArtWriter.FindDiscImage"/>). Then the names rippers and scanners use for a scan of the disc:
/// "cd", "cd1", "disc 1", "cdart", "cd-art", "disc art", "media", "cd label". A folder of scans one level down
/// ("Scans", "Artwork", "Covers") is looked in too, because that is where a record bought with a booklet keeps
/// them. The lowest disc number wins, and a name with no number wins over all of them.</para>
///
/// <para><b>Inside the files.</b> A picture that the tags type "Media", the label side of the CD (see
/// <see cref="TagReader.LoadDiscArt"/>). Never the cover.</para>
///
/// <para>A scan is a square photograph. The app cuts the disc out of it when it draws it, and the caller of
/// <see cref="Carry"/> may cut it before it is saved.</para>
/// </summary>
public static partial class DiscArtFinder
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>The folders, one level down, where scans of a record are kept.</summary>
    private static readonly string[] ScanFolders = ["scans", "scan", "artwork", "art", "covers", "images"];

    /// <summary>A file name (without its extension) that says the picture is of the disc. Group "n" is the disc
    /// number, when there is one.</summary>
    [GeneratedRegex(
        @"^(?:(?:cd|disc|disk)[\s_\-]*(?<n>\d{1,2})?(?:[\s_\-]*(?:art|label|face|scan))?|cdart|discart|media)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscName();

    /// <summary>A scan of the disc in <paramref name="folder"/> or in a scans folder inside it, or null.</summary>
    public static string? FindScan(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return null;

            if (Best(Directory.EnumerateFiles(folder)) is { } here)
                return here;

            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                if (!ScanFolders.Contains(Path.GetFileName(sub).ToLowerInvariant()))
                    continue;
                if (Best(Directory.EnumerateFiles(sub)) is { } scan)
                    return scan;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;   // an unreadable folder is a folder with no scan in it
        }
    }

    /// <summary>The disc picture among <paramref name="files"/>: no number first, then the lowest number.</summary>
    private static string? Best(IEnumerable<string> files)
    {
        string? best = null;
        var bestRank = int.MaxValue;
        foreach (var file in files)
        {
            if (!ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                continue;
            var match = DiscName().Match(Path.GetFileNameWithoutExtension(file).Trim());
            if (!match.Success)
                continue;
            var rank = match.Groups["n"].Success && int.TryParse(match.Groups["n"].Value, out var n) ? n : 0;
            if (rank < bestRank || (rank == bestRank && string.CompareOrdinal(file, best) < 0))
            {
                best = file;
                bestRank = rank;
            }
        }
        return best;
    }

    /// <summary>
    /// The disc picture that came with a record, or null.
    /// </summary>
    /// <param name="folders">The folders the record's files are in.</param>
    /// <param name="files">The record's music files, which may carry a picture of the disc.</param>
    public static FoundPicture? ForRecord(IEnumerable<string> folders, IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(files);

        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var image = FolderArtWriter.FindDiscImage(folder) ?? FindScan(folder);
            if (image is not null && Read(image) is { Length: > 0 } bytes)
                return new FoundPicture(bytes, ArtOrigin.Folder);
        }

        foreach (var file in files)
            if (TagReader.LoadDiscArt(file) is { } inside)
                return new FoundPicture(inside.Bytes, ArtOrigin.Files);

        return null;
    }

    /// <summary>
    /// After an import: gives every album folder the run filed into, and that has no disc picture, the one that
    /// came with its files. The scan is looked for beside the files' old place, and a picture inside the files
    /// is read from their new place.
    /// </summary>
    /// <param name="cut">Turns a found picture into the one to save (the app cuts the disc out of a square
    /// photograph). Null, or an empty answer, saves the picture as it was found.</param>
    /// <returns>The album folders that were given a disc picture.</returns>
    public static IReadOnlyList<string> Carry(IEnumerable<ApplyResult> results, Func<byte[], byte[]?>? cut = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        var written = new List<string>();
        foreach (var group in results
                     .Where(r => r.Success)
                     .GroupBy(r => Path.GetDirectoryName(r.DestinationPath), StringComparer.OrdinalIgnoreCase)
                     .Where(g => !string.IsNullOrEmpty(g.Key)))
        {
            var dest = group.Key!;
            if (FolderArtWriter.FindDiscImage(dest) is not null)
                continue;

            var found = ForRecord(
                group.Select(r => Path.GetDirectoryName(r.SourcePath)).Where(d => !string.IsNullOrEmpty(d)).Select(d => d!),
                group.Select(r => r.DestinationPath));
            if (found is null)
                continue;

            var bytes = Cut(found.Bytes, cut);
            if (FolderArtWriter.WriteFoundDisc(dest, bytes))
                written.Add(dest);
        }
        return written;
    }

    private static byte[] Cut(byte[] bytes, Func<byte[], byte[]?>? cut)
    {
        if (cut is null)
            return bytes;
        try
        {
            return cut(bytes) is { Length: > 0 } done ? done : bytes;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return bytes;   // a picture the cut cannot read is still better saved as it is
        }
    }

    private static byte[]? Read(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
