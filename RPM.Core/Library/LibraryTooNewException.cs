namespace RPM.Core.Library;

/// <summary>
/// The library database was written by a newer version of rpm than this one. Thrown by
/// <see cref="LibraryDatabase"/> before it writes anything, so the file is left as the newer build
/// wrote it and that build can still open it.
/// </summary>
/// <param name="path">The database file.</param>
/// <param name="found">The schema version the file carries.</param>
/// <param name="supported">The newest schema version this build knows.</param>
public sealed class LibraryTooNewException(string path, long found, long supported)
    : Exception($"The library database {path} is at schema version {found}; this build supports up to {supported}. Nothing was changed.")
{
    /// <summary>The database file.</summary>
    public string Path { get; } = path;

    /// <summary>The schema version the file carries.</summary>
    public long Found { get; } = found;

    /// <summary>The newest schema version this build knows.</summary>
    public long Supported { get; } = supported;
}
