using System.Text.Json;
using System.Text.Json.Serialization;

using RPM.Core.Library;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// The library search's recent searches, kept on disk beside the app's other small stores. The rules are
/// <see cref="SearchHistory"/>'s, which the phones keep too; this only holds the list.
/// </summary>
/// <remarks>A file of its own and not a setting: it is written whenever somebody pauses over a search,
/// and a write of the whole settings file for that would be a write of everything else in it too.</remarks>
internal static partial class SearchHistoryStore
{
    /// <summary>Where the file lives. It can be set, so that tests write somewhere of their own.</summary>
    internal static string Dir { get; set; } = LocalData.Dir;

    private static string FilePath => Path.Combine(Dir, "search-history.json");

    private static readonly object Gate = new();

    private static IReadOnlyList<string>? _held;

    /// <summary>False when the file exists and could not be read: nothing is written over it then.</summary>
    private static bool _mayWrite = true;

    /// <summary>The searches, newest first.</summary>
    public static IReadOnlyList<string> Entries
    {
        get
        {
            lock (Gate)
                return Held();
        }
    }

    /// <summary>Raised on the thread that changed the list, after it changed.</summary>
    public static event Action? Changed;

    /// <summary>Puts a search at the top. See <see cref="SearchHistory.Remember"/>.</summary>
    public static void Remember(string asked) => Change(kept => SearchHistory.Remember(kept, asked));

    /// <summary>Takes one search out.</summary>
    public static void Forget(string asked) => Change(kept => SearchHistory.Forget(kept, asked));

    /// <summary>Takes them all out.</summary>
    public static void Clear() => Change(_ => []);

    private static void Change(Func<IReadOnlyList<string>, IReadOnlyList<string>> change)
    {
        IReadOnlyList<string> next;
        lock (Gate)
        {
            var was = Held();
            next = change(was);
            if (was.SequenceEqual(next, StringComparer.Ordinal))
                return;

            _held = next;
            if (_mayWrite)
                Write(next);
        }

        Changed?.Invoke();
    }

    private static IReadOnlyList<string> Held()
    {
        if (_held is { } held)
            return held;

        var read = RPM.Core.Files.StoreFile.Load(
            FilePath, text => JsonSerializer.Deserialize(text, SearchHistoryJson.Default.ListString));
        _mayWrite = read.MayWrite;
        _held = read.Value is { } found
            ? found.Where(e => !string.IsNullOrWhiteSpace(e)).Take(SearchHistory.Most).ToList()
            : [];
        return _held;
    }

    private static void Write(IReadOnlyList<string> entries)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries.ToList(), SearchHistoryJson.Default.ListString));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A search that cannot be written down is one that is not offered again. Nothing else is lost.
        }
    }

    /// <summary>The JSON serialiser for the list. A published build is Native AOT, which serialises from
    /// this and never by reflection.</summary>
    [JsonSerializable(typeof(List<string>))]
    private sealed partial class SearchHistoryJson : JsonSerializerContext;
}
