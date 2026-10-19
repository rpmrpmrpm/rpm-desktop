using RPM.Core.Library;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The photograph behind a generated playlist's frame, read from the disk.
///
/// <para><b>The pictures ship with the app and are not fetched at run time.</b> A fetched picture would
/// need an API key. That key is not in this repository, and it would be kept in local settings, so the
/// result would differ between installs. The pictures are chosen in advance in
/// <c>scripts/playlist-photos.json</c>, cut to the square a cover is drawn in, and read from
/// <c>Assets/PlaylistPhotos</c>. This way the covers are the same on every machine, with no key and no
/// network. The same approach is used by <see cref="Main.Genres.GenrePhotos"/>.</para>
///
/// <para><b>A list with no entry is not an error, and neither is a missing file.</b>
/// <see cref="BytesAsync"/> returns null in both cases. The caller then draws the figure from the list's
/// own two numbers. That always gives an answer, and it is the same picture on every machine. Most lists
/// have no entry, because moods are split by genre and decade. See the note in the JSON.</para>
///
/// <para><b>The colour and the mark are not in these files.</b> Both are added when the cover is composed
/// (<c>GeneratedCoverService.Framed</c>), from the list's palette entry. So a change of tint reaches every
/// list without re-exporting any file. For this reason the shipped asset is the bare photograph, not the
/// finished cover.</para>
/// </summary>
internal static partial class PlaylistPhotos
{
    /// <summary>
    /// Which list has which picture. The key is the whole id. Two lists that differ only in the part
    /// left out would otherwise share a photograph.
    ///
    /// <para><b>It is built on first use, not in the static initializer. This is required.</b>
    /// <see cref="All"/> is declared in the generated half of this class. Static field initializers run in
    /// textual order within one file, and no order is guaranteed between the files of a partial class. A
    /// plain initializer here threw <see cref="ArgumentNullException"/> on first use, because the table was
    /// still null. A <see cref="Lazy{T}"/> factory runs after the type is initialized, so the order does
    /// not matter.</para>
    ///
    /// <para>The factory is a named method, not a lambda. Reading <see cref="All"/> inside a field
    /// initializer gives warning CS8604, because the compiler cannot prove that the table is set yet. The
    /// warning is correct about the hazard, even though the lazy avoids it.</para>
    /// </summary>
    private static readonly Lazy<Dictionary<SmartPlaylistId, PlaylistPhoto>> ById = new(Index);

    private static Dictionary<SmartPlaylistId, PlaylistPhoto> Index()
        => All.ToDictionary(p => p.List);

    /// <summary>The folder that holds the photographs, next to the built app.</summary>
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Assets", "PlaylistPhotos");

    /// <summary>One list's entry, or null where it has none.</summary>
    internal static PlaylistPhoto? For(SmartPlaylistId id)
        => ById.Value.TryGetValue(id, out var found) ? found : null;

    /// <summary>Where one list's photograph is on this machine, or null where it has none.</summary>
    internal static string? PathFor(SmartPlaylistId id)
        => For(id) is { } photo ? Path.Combine(Dir, photo.File) : null;

    /// <summary>One list's photograph, read off the disk.
    ///
    /// <para>Null when the list has no entry, or when the file is missing. A file can be missing if the
    /// assets were pruned from an install, or if the build left out the Content item. Both cases give
    /// the same result for the caller, which draws the figure instead.</para>
    ///
    /// <para>The file is read off the calling thread, as the genre photographs are. A cover is composed
    /// while a page loads, and this runs on whichever thread is composing it.</para>
    /// </summary>
    internal static async Task<byte[]?> BytesAsync(SmartPlaylistId id, CancellationToken ct)
    {
        if (PathFor(id) is not { } path)
            return null;

        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
