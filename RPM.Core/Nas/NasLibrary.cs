using RPM.Core.Library;

namespace RPM.Core.Nas;

/// <summary>Facts about a library folder that the NAS code needs.</summary>
public static class NasLibrary
{
    /// <summary>The library id in the folder's <c>.rpm/library.json</c>, or null when there is none or it
    /// cannot be read. A folder on a network volume gets one when it is first opened.</summary>
    public static string? IdOf(string libraryRoot)
    {
        if (string.IsNullOrEmpty(libraryRoot))
            return null;
        try
        {
            return MirrorLocation.LibraryId.Read(libraryRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Writes a new library id into an empty folder on the NAS, or returns the one already there.</summary>
    public static string EnsureId(string libraryRoot) => MirrorLocation.LibraryId.ReadOrCreate(libraryRoot);
}
