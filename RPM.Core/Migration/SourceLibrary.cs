namespace RPM.Core.Migration;

/// <summary>The music app a library is imported from.</summary>
public enum SourceKind
{
    MusicBee,
    ITunes,
    Jellyfin,
    Plex,
    Navidrome,
    PlaylistFiles,
}

/// <summary>Where a source track's audio is. It is a file on this computer, or a file on a media
/// server that has to be downloaded.</summary>
public abstract record SourceFile;

/// <summary>A file on this computer. The path is absolute and in Unicode normal form C.</summary>
public sealed record LocalFile(string Path) : SourceFile;

/// <summary>A file on a media server. <paramref name="Key"/> is what the server's reader needs to
/// download it (an item id, or a part path for Plex). <paramref name="Extension"/> starts with a dot.
/// <paramref name="Size"/> is in bytes, or 0 when the server does not say.</summary>
public sealed record RemoteFile(string Key, string Extension, long Size) : SourceFile;

/// <summary>
/// One track as the other app knows it. The tags here are what the other app shows, used for the
/// summary and to group albums. The tags written into rpm come from the file itself when it is
/// imported.
/// </summary>
/// <param name="SourceId">The other app's own id for the track, stable between two reads of the same
/// library.</param>
/// <param name="Rating">0 to 10 (half stars), 0 meaning not rated.</param>
/// <param name="DurationSeconds">0 when the app does not say.</param>
/// <param name="SizeBytes">0 when the app does not say.</param>
public sealed record SourceTrack(
    string SourceId,
    SourceFile File,
    string Title,
    string Artist,
    string Album,
    string AlbumArtist,
    int TrackNumber,
    int DiscNumber,
    double DurationSeconds,
    long SizeBytes,
    int Rating,
    bool Favorite,
    int Plays,
    DateTimeOffset? LastPlayed,
    DateTimeOffset? Added);

/// <summary>
/// One playlist as the other app knows it.
/// </summary>
/// <param name="Folder">The playlist folders it is in, outermost first. Empty when it is not in a
/// folder.</param>
/// <param name="Smart">The other app fills it by rules. It is imported as a plain playlist with the
/// tracks it holds now.</param>
/// <param name="TrackIds">The <see cref="SourceTrack.SourceId"/> of each entry, in order. A track that
/// is in the playlist twice is listed twice.</param>
/// <param name="Unresolved">Entries that are not one of the library's tracks (a track the other app no
/// longer has, or one that was skipped), as the other app names them.</param>
public sealed record SourcePlaylist(
    string SourceId,
    string Name,
    IReadOnlyList<string> Folder,
    bool Smart,
    IReadOnlyList<string> TrackIds,
    IReadOnlyList<string> Unresolved);

/// <summary>Something in the other library that is not imported, and why.</summary>
/// <param name="What">The item as a person would name it ("Artist - Title").</param>
/// <param name="ReasonKey">A <c>Migration_*</c> string key that says why.</param>
public sealed record SourceSkip(string What, string ReasonKey);

/// <summary>
/// Everything a reader found in the other app's library.
/// </summary>
/// <param name="LibraryId">Identifies this library among others of the same kind (an iTunes library id,
/// a server id). Empty for playlist files.</param>
/// <param name="Name">The library's name as shown to the person ("MusicBee", the server's name).</param>
public sealed record SourceLibrary(
    SourceKind Kind,
    string LibraryId,
    string Name,
    IReadOnlyList<SourceTrack> Tracks,
    IReadOnlyList<SourcePlaylist> Playlists,
    IReadOnlyList<SourceSkip> Skipped)
{
    /// <summary>The device name the library's play counts are stored under. Each source library is
    /// one device, so importing it again replaces its plays and does not add them a second time.</summary>
    public string DeviceId => $"import:{Kind.ToString().ToLowerInvariant()}:{LibraryId}";
}

/// <summary>The <c>Migration_*</c> string keys the readers use for skipped items.</summary>
public static class SkipReason
{
    /// <summary>A stream or a web address, not a file.</summary>
    public const string NotAFile = "Migration_SkipNotAFile";

    /// <summary>The other app lists it, but the file is not on this computer (for example a track
    /// that is only in Apple Music's cloud).</summary>
    public const string NotOnThisComputer = "Migration_SkipNotOnThisComputer";

    /// <summary>A podcast, an audiobook or a video.</summary>
    public const string NotMusic = "Migration_SkipNotMusic";
}
