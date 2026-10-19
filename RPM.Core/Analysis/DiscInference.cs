using System.Text.RegularExpressions;
using RPM.Core.Models;

namespace RPM.Core.Analysis;

/// <summary>
/// Fills in the disc numbers that a multi-disc rip did not write into its tags, using the names of
/// the folders it was ripped into.
///
/// <para>A two-CD set often arrives as <c>Album/CD 1</c> and <c>Album/CD 2</c> with no DISCNUMBER
/// tag on either. The library models discs (album page sections, disc counts, per-disc subtitles,
/// continuing track numbers), but none of that works for files that do not say which disc they
/// are. Without a disc number the set is one album with track 1 twice: the queue shows two rows
/// called "01", the duplicate pass treats the second disc as a lesser copy of the first, and the
/// import puts both discs into one folder as if they were one record.</para>
///
/// <para>The only evidence used is a folder name that states a disc, such as <c>CD 2</c>,
/// <c>Disc 03</c> or <c>Album (CD1)</c>. Two folders of the same album with repeating track numbers
/// may also be two rips of one record, and numbering those as discs would invent a box set by
/// mistake.</para>
/// </summary>
public static partial class DiscInference
{
    /// <summary>A disc number stated by a path segment: "CD 2", "Disc 03", "Album (CD1)".</summary>
    [GeneratedRegex(@"(?:^|[\s\-–—_\(\[])(?:cd|disc|disk)\s*[-_ ]?(\d{1,2})(?:$|[\s\-–—_\)\]\.])",
                    RegexOptions.IgnoreCase)]
    private static partial Regex DiscInPath();

    /// <summary>
    /// The same tracks, each with the disc number and disc count that its folders imply, where its
    /// tags have none. The order is kept. A record whose folders name only one disc is left alone:
    /// one disc is what an ordinary album is, and writing "disc 1 of 1" would add nothing.
    ///
    /// <para>A track that this changes is marked <see cref="TrackInfo.TagsEdited"/>, for the same
    /// reason as in <see cref="AlbumArtistFill.Fill"/>. Otherwise a set whose files have good tags
    /// would be moved without a tag write, and the inferred disc numbers would reach the file names
    /// but not the files.</para>
    /// </summary>
    public static IReadOnlyList<TrackInfo> Fill(IReadOnlyList<TrackInfo> tracks)
    {
        // Group the way the queue groups: an album is what the tags say it is, under the artist it
        // is filed under. The grid shows the same group as one node, so this code and the user's
        // view always agree.
        var byAlbum = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => (Artist: FilingArtist(t), Album: AlbumResolver.NormalizeAlbum(t.Album!)),
                     AlbumKeyComparer.Instance);

        var discs = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        foreach (var album in byAlbum)
        {
            var members = album.ToList();
            var found = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in members)
            {
                var disc = track.DiscNumber > 0 ? track.DiscNumber : DiscFromPath(track.FilePath);
                if (disc > 0)
                    found[track.FilePath] = disc;
            }

            // One disc, or none named, is not a set. Leave it alone.
            uint discCount = found.Count == 0 ? 0 : found.Values.Max();
            if (found.Values.Distinct().Count() < 2)
                continue;

            foreach (var track in members)
            {
                if (found.TryGetValue(track.FilePath, out var disc))
                    discs[track.FilePath] = disc;
                counts[track.FilePath] = discCount;
            }
        }

        if (discs.Count == 0)
            return tracks;

        var filled = new List<TrackInfo>(tracks.Count);
        foreach (var track in tracks)
        {
            var disc = track.DiscNumber > 0 || !discs.TryGetValue(track.FilePath, out var d)
                ? track.DiscNumber
                : d;
            // The count may be raised to the set's, never lowered. A file that says it is one of three
            // discs must not be changed by an import that happens to hold only two of them.
            var count = counts.TryGetValue(track.FilePath, out var c) && c > track.DiscCount
                ? c
                : track.DiscCount;

            filled.Add(disc == track.DiscNumber && count == track.DiscCount
                ? track
                : track with { DiscNumber = disc, DiscCount = count, TagsEdited = true });
        }
        return filled;
    }

    /// <summary>The disc that a file's path names, or 0. The path is read from the deepest segment
    /// outwards, so <c>Box Set 2/CD 3/01.flac</c> is disc 3 and not disc 2.</summary>
    public static uint DiscFromPath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (DiscInPath().Match(Path.GetFileName(dir)) is { Success: true } m
                && uint.TryParse(m.Groups[1].Value, out var disc) && disc > 0)
                return disc;
            dir = Path.GetDirectoryName(dir);
        }
        return 0;
    }

    /// <summary>The artist a track is filed under: the album artist first, as in the store's own group
    /// key.</summary>
    private static string FilingArtist(TrackInfo track)
    {
        var artist = (track.AlbumArtist ?? "").Trim();
        return artist.Length > 0 ? artist : (track.Artist ?? "").Trim();
    }

    private sealed class AlbumKeyComparer : IEqualityComparer<(string Artist, string Album)>
    {
        public static readonly AlbumKeyComparer Instance = new();

        public bool Equals((string Artist, string Album) x, (string Artist, string Album) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Artist, y.Artist)
               && StringComparer.OrdinalIgnoreCase.Equals(x.Album, y.Album);

        public int GetHashCode((string Artist, string Album) key)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Artist),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Album));
    }
}
