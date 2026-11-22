using System.Text.Json;

namespace RPM.Core.Library;

/// <summary>
/// Where this computer keeps the library databases that are not in their music folder, and whether a music
/// folder on this computer's own disk keeps its database there too. <see cref="MirrorLocation.For(string,
/// DatabasePlaces)"/> reads it.
/// </summary>
/// <param name="ThisComputer">The database folder: one folder per library id, each holding that library's
/// <c>library.db</c>. A network library always keeps its database here.</param>
/// <param name="OffMusicDisk">True when a music folder on this computer's own disk keeps its database in
/// <paramref name="ThisComputer"/> as well, and not in its own <c>.rpm</c> folder. For a music folder on a
/// slow disk.</param>
/// <param name="Before">Database folders chosen before <paramref name="ThisComputer"/>. A library whose
/// database is still in one of them, and not yet in <paramref name="ThisComputer"/>, has it moved across on
/// its next open. So changing the database folder never leaves a library with an empty database.</param>
public sealed record DatabasePlaces(string ThisComputer, bool OffMusicDisk = false, IReadOnlyList<string>? Before = null)
{
    /// <summary>The settings key that names the database folder. Null or missing means the default,
    /// <see cref="MirrorLocation.LibrariesFolderName"/> in the app's local data folder.</summary>
    public const string FolderKey = "LibraryDatabaseFolder";

    /// <summary>The settings key for <see cref="OffMusicDisk"/>.</summary>
    public const string OffMusicDiskKey = "LibraryDatabaseOffMusicDisk";

    /// <summary>The settings key for <see cref="Before"/>.</summary>
    public const string BeforeKey = "LibraryDatabaseFoldersBefore";

    /// <summary>The database folders other than <see cref="ThisComputer"/>, without repeats.</summary>
    internal IEnumerable<string> Elsewhere =>
        (Before ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && !MirrorLocation.SamePath(p, ThisComputer))
            .DistinctBy(MirrorLocation.Comparable);

    /// <summary>
    /// The places as the desktop app has them, read from its <c>settings.json</c> in
    /// <paramref name="localDataDir"/>. This is for the command-line tools in <c>tools/</c>, which open the
    /// user's library outside the app. A tool that opened it with the default places would carry a moved
    /// database back into the music folder. The keys are the ones the app's <c>SettingsService</c> writes.
    /// A file that is missing or cannot be read gives the defaults.
    /// </summary>
    public static DatabasePlaces FromAppSettings(string localDataDir)
    {
        var defaultFolder = Path.Combine(localDataDir, MirrorLocation.LibrariesFolderName);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(localDataDir, "settings.json")));
            var root = doc.RootElement;
            var folder = root.TryGetProperty(FolderKey, out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString()
                : null;
            var off = root.TryGetProperty(OffMusicDiskKey, out var o) && o.ValueKind == JsonValueKind.True;
            var before = root.TryGetProperty(BeforeKey, out var b) && b.ValueKind == JsonValueKind.Array
                ? b.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];
            before.Add(defaultFolder);
            return new DatabasePlaces(string.IsNullOrWhiteSpace(folder) ? defaultFolder : folder, off, before);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DatabasePlaces(defaultFolder);
        }
    }
}
