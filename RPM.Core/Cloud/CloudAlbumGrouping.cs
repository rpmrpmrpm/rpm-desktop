using RPM.Core.Analysis;

namespace RPM.Core.Cloud;

/// <summary>
/// What one album is, over cloud rows. This is the desktop's rule, reused here.
///
/// <para>
/// <see cref="Library.AlbumOrder"/> states the rule: <b>an album is a folder with tracks directly in
/// it</b>. This does not depend on tags. It is the shape the library is filed in
/// (Artist/Album/track), and it handles three cases that a tag-based grouping cannot:
/// </para>
///
/// <list type="bullet">
/// <item>A <b>compilation</b>: one album with a different artist on every track. The album belongs
/// to its album artist (often "Various Artists"), and the track artists belong to the tracks.</item>
/// <item>A <b>merged multi-disc set</b>: every disc is in one folder with per-disc track numbers,
/// which is what <c>MultiDiscMode.MergedAlbum</c> files (the default). It is one album with two or
/// more discs.</item>
/// <item>A <b>deliberately separate</b> disc: <c>MultiDiscMode.SeparateAlbums</c> gives each disc its
/// own folder and its own decorated name ("Album (Disc 2)"). These are meant to read as two
/// albums.</item>
/// </list>
///
/// <para>
/// Grouping by track artist and album name would split a compilation into as many albums as it has
/// artists, and would merge nothing that the desktop merges.
/// </para>
///
/// <para>
/// The folder comes from the row's own <see cref="CloudTrack.RelativePath"/>, the desktop's library
/// path stored with the file when it was uploaded. The phone therefore groups by the same folders
/// that the desktop draws its wall from. A row with no path (the field is optional on the wire) falls
/// back to its tags. The comparison is then album artist plus the album name with disc decoration
/// removed, which is the rule the desktop uses when it has only tags
/// (<see cref="Library.LibraryDatabase.FindAlbumFolder"/>).
/// </para>
/// </summary>
public static class CloudAlbumGrouping
{
    /// <summary>The album a cloud row belongs to, as a key that rows can be grouped on. Opaque: it is an
    /// identity to compare, not a name to show.</summary>
    public static string KeyFor(CloudTrack track)
        => KeyFor(track.RelativePath, track.Tags?.AlbumArtist, track.Artist, track.Album);

    /// <inheritdoc cref="KeyFor(CloudTrack)"/>
    public static string KeyFor(string? relativePath, string? albumArtist, string? artist, string? album)
    {
        // The folder wins when there is one: it is the desktop's own answer, already decided by how the
        // user's library is filed.
        if (FolderOf(relativePath) is { } folder)
            return "folder:" + folder.ToLowerInvariant();

        // Otherwise use tags. The album's artist is the album artist when the tag has one. A present but
        // blank tag is not an album artist, which is why this is not `??` (see AlbumArt.KeyForTrack).
        var owner = string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist;
        var name = string.IsNullOrWhiteSpace(album) ? "" : AlbumResolver.NormalizeAlbum(album);

        return $"tags:{Norm(owner)}|{Norm(name)}";
    }

    /// <summary>
    /// The library folder a row sits in: everything before the file name, separated by '/'. It is the
    /// empty string for a track loose at the library root, which is also one album on the desktop's
    /// wall. Null only when the row carries no path, and then the caller has to fall back to tags.
    /// </summary>
    public static string? FolderOf(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        // The library stores '/' whatever system it was filed on. A Windows-style path is normalised
        // and not treated as one long file name.
        var path = relativePath.Trim().Replace('\\', '/').TrimStart('/');
        var cut = path.LastIndexOf('/');

        return cut < 0 ? "" : path[..cut];
    }

    /// <summary>
    /// The album's name: what its files say it is, and if they say nothing, the folder's own name.
    /// The desktop's card uses the same two sources in the same order (folder name first, refined from
    /// the tags once they are read). Null when neither knows.
    /// </summary>
    public static string? AlbumNameFor(IEnumerable<CloudTrack> tracks, string? folder)
        => FirstFilled(tracks.Select(t => t.Album)) ?? Segment(folder, fromEnd: 0);

    /// <summary>
    /// Whose album it is: the album artist, then the track artist if no file carries an album artist,
    /// then the folder's parent name if neither is known. With Artist/Album/track filing, the segment
    /// above the album is the artist, and the desktop's card reads it before it has a tag. Null when
    /// nothing answers.
    ///
    /// <para>The album artist matters more than the track artist: on a compilation the track artists
    /// all differ and only the album artist names the album.</para>
    /// </summary>
    public static string? AlbumArtistFor(IEnumerable<CloudTrack> tracks, string? folder)
    {
        var rows = tracks as IReadOnlyCollection<CloudTrack> ?? [.. tracks];

        return FirstFilled(rows.Select(t => t.Tags?.AlbumArtist))
            ?? FirstFilled(rows.Select(t => t.Artist))
            ?? Segment(folder, fromEnd: 1);
    }

    /// <summary>Which disc of the set a row is on, for <see cref="Library.AlbumTracklist"/>. Untagged
    /// is disc 1, the same as an untagged file on the desktop.</summary>
    public static uint DiscOf(CloudTrack track) => Position(track.Tags?.DiscNumber);

    /// <summary>Where on its disc a row sits. Zero means "not known", and those rows sort last.</summary>
    public static uint TrackOf(CloudTrack track) => Position(track.Tags?.TrackNumber);

    private static uint Position(int? value) => value is > 0 ? (uint)value : 0u;

    /// <summary>One segment of a '/'-separated folder, counted from the end: 0 is the folder itself,
    /// 1 is the one above it. Null when the path is not that deep.</summary>
    private static string? Segment(string? folder, int fromEnd)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > fromEnd ? parts[^(fromEnd + 1)] : null;
    }

    private static string? FirstFilled(IEnumerable<string?> values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Norm(string? value) => (value ?? "").Trim().ToLowerInvariant();
}
