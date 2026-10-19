using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What the listener has said BELONGS, as <see cref="MoodShelf.Build"/> wants it.
///
/// <para>Here rather than in each test class because every cut needs one and almost none of them care
/// what is in it: a like only decides whether a song is held inside its list's era window, so a test
/// about ranges, caps or narrowings says <see cref="None"/> and means it. The tests that ARE about a
/// like build one with <see cref="Of"/>, where the pair being named, a path AND a list, is the whole
/// point of the table it stands for.</para>
/// </summary>
internal static class Likes
{
    /// <summary>Nobody has said anything about anything. What almost every cut is.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>> None =
        new Dictionary<string, IReadOnlySet<SmartPlaylistKind>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>One like per pair named. A path said of two lists is named twice.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>> Of(
        params (string Path, SmartPlaylistKind Kind)[] said)
    {
        var byPath = new Dictionary<string, HashSet<SmartPlaylistKind>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, kind) in said)
        {
            if (!byPath.TryGetValue(path, out var kinds))
                byPath[path] = kinds = [];
            kinds.Add(kind);
        }

        return byPath.ToDictionary(
            p => p.Key, p => (IReadOnlySet<SmartPlaylistKind>)p.Value, StringComparer.OrdinalIgnoreCase);
    }
}
