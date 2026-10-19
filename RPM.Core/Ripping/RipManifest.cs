using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPM.Core.Ripping;

/// <summary>Where a staging folder's audio came from. This is the name a user would give the thing, when the
/// files themselves cannot say it.</summary>
public enum RipSource
{
    /// <summary>An audio CD.</summary>
    Cd,
    /// <summary>A video DVD: a concert or a music disc, ripped to its audio.</summary>
    Dvd,
    /// <summary>A record played through the line-in.</summary>
    Vinyl,
}

/// <summary>
/// What a rip or recording knew about itself at the moment it started. It is written beside its audio as
/// <c>rip.json</c>.
///
/// <para>The staging folder is named with a GUID, and it has to be, because two rips of the same album must
/// not collide. But a GUID is not a useful name to show anyone. The only other name available is the
/// tracks' own tags. Those work for a CD looked up before the rip. They do not work for a record, which is
/// untagged audio until someone splits and names it. So the folder is not renamed. Instead, what was known
/// is written down once, at the start.</para>
///
/// <para>Everything here is best effort, and none of it is required. A missing or unreadable manifest just
/// means the pending list falls back to the tags, and then to a plain description such as
/// "a vinyl recording from Tuesday".</para>
/// </summary>
/// <param name="Source">CD, DVD or record.</param>
/// <param name="Title">The album, as the disc lookup gave it. Null for a record, because nothing has named
/// it yet.</param>
/// <param name="Artist">Album artist, same provenance as <paramref name="Title"/>.</param>
/// <param name="TrackCount">How many tracks were meant to land. This is worth writing, because a rip that
/// stopped halfway shows the gap: three files in the folder and twelve here says what happened.</param>
/// <param name="CreatedUtc">When the rip or recording started.</param>
/// <param name="Device">The drive or input it came off, as the user picked it.</param>
/// <param name="DiscId">CD only: the MusicBrainz disc ID, so that a disc can be recognised again.</param>
public sealed partial record RipManifest(
    RipSource Source,
    string? Title = null,
    string? Artist = null,
    int TrackCount = 0,
    DateTime CreatedUtc = default,
    string? Device = null,
    string? DiscId = null)
{
    /// <summary>What the manifest is called inside the staging folder.</summary>
    public const string FileName = "rip.json";

    /// <summary>The manifest's shape, and how it is written.</summary>
    /// <remarks>UseStringEnumConverter is used instead of a JsonStringEnumConverter instance. The non-generic
    /// converter builds its mapping at run time, which a published Native AOT build cannot do. The generated
    /// one writes the same names.</remarks>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UseStringEnumConverter = true)]
    [JsonSerializable(typeof(RipManifest))]
    private sealed partial class RipManifestJson : JsonSerializerContext;

    /// <summary>Writes the manifest into <paramref name="folder"/>. This never throws. A rip that cannot write a
    /// name for itself is still a rip, and failing the rip because of this would be wrong.</summary>
    public void WriteTo(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, RipManifestJson.Default.RipManifest));
        }
        catch { }
    }

    /// <summary>The manifest in <paramref name="folder"/>, or null when there is none. That is the case for
    /// every folder ripped before manifests existed, and for any folder whose file was lost in a disk failure.</summary>
    public static RipManifest? Read(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), RipManifestJson.Default.RipManifest) : null;
        }
        catch { return null; }
    }
}
