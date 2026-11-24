namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The photographs behind the frame of the app's own playlist folders: the moods, the decades and each
/// mood (see <see cref="FolderCover"/>).
///
/// <para>They ship with the app, as the lists' photographs do (<see cref="PlaylistPhotos"/>). They are chosen
/// in <c>scripts/folder-photos.json</c> in the rpm repo, and <c>scripts/gen_folder_photos.py</c> cuts them
/// to a square and writes them to <c>Assets/FolderPhotos</c>, with the table of which folder wears which
/// (<see cref="Files"/>, in the generated half of this class). The phones carry the same files under the
/// same keys. None of them is one of the lists' photographs: a folder holds those lists, and a picture of its
/// own makes it read as a folder.</para>
///
/// <para>The files are the bare photographs. The folder's colour goes on when the cover is composed.</para>
/// </summary>
internal static partial class FolderPhotos
{
    /// <summary>The folder that holds the photographs, next to the built app.</summary>
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Assets", "FolderPhotos");

    /// <summary>Where the photograph filed under <paramref name="key"/> is on this machine, or null where no
    /// folder has that key. The file itself may still be missing from an install; the caller checks.</summary>
    internal static string? PathFor(string key)
        => Files.TryGetValue(key, out var file) ? Path.Combine(Dir, file) : null;
}
