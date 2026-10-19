using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPM.Core.Library;

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// A set of strings as a JSON array, for the <c>json_each($paths)</c> parameters the queries
    /// narrow themselves with.
    /// </summary>
    /// <remarks>
    /// The published build is Native AOT, so this uses a source-generated context instead of
    /// reflection. The queries only ever pass a string array, so one shape is enough. The output is
    /// the same JSON that reflection-based serialization would write.
    /// </remarks>
    internal static string JsonList(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values as string[] ?? [.. values], LibraryDatabaseJson.Default.StringArray);

    /// <summary>The one shape <see cref="JsonList"/> writes.</summary>
    [JsonSerializable(typeof(string[]))]
    private sealed partial class LibraryDatabaseJson : JsonSerializerContext;
}
