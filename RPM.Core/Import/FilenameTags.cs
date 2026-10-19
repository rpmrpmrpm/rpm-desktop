using System.Text;
using System.Text.RegularExpressions;

namespace RPM.Core.Import;

/// <summary>
/// Reads tags back OUT of a path, against the same patterns <see cref="NamingTemplate"/> writes them
/// into. One vocabulary runs in both directions: a pattern that files a track is a pattern that can
/// name one, and somebody who has learned the tokens once has learned them for good.
///
/// <para>Matched from the RIGHT. A pattern with no slash is about the file name and nothing else. One
/// with slashes reaches that many folders up and ignores whatever is above them, because where a pile
/// of files happens to be sitting is not part of what they are called.</para>
/// </summary>
public static class FilenameTags
{
    /// <summary>The patterns offered in the dialog. Every one of them round-trips against
    /// <see cref="NamingTemplate.Render"/>, which the tests hold to.</summary>
    public static readonly IReadOnlyList<string> Presets =
    [
        // The shapes files actually arrive in, commonest first: the three ways a ripper writes a
        // numbered track, then the ones that carry the artist, then the ones that put the album and
        // the artist in folders above it. The list is long enough that most libraries match one of
        // them, so the window is useful and not a blank box.
        "{track} - {title}",
        "{track}. {title}",
        "{track} {title}",
        "{artist} - {title}",
        "{track} - {artist} - {title}",
        "{artist} - {album} - {track} - {title}",
        "{disc}-{track} {title}",
        "{album}/{track} - {title}",
        "{artist}/{album}/{track} - {title}",
        "{artist}/{album} ({year})/{track} - {title}",
    ];

    /// <summary>
    /// Which preset reads the most of these paths, as an index into <see cref="Presets"/>.
    ///
    /// <para>A library is filed one way, so the window opening on the pattern that fits it is the
    /// difference between answering a question and asking one. Ties go to the earlier preset, which is
    /// the commoner shape. A set of paths that no preset reads gives 0 rather than nothing, because a
    /// preset with no match still shows what a pattern looks like, which an empty box does not.</para>
    /// </summary>
    public static int BestPreset(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var files = paths as IReadOnlyList<string> ?? [.. paths];
        var best = 0;
        var most = 0;
        for (var i = 0; i < Presets.Count; i++)
        {
            var matched = files.Count(p => Parse(p, Presets[i]) is { Count: > 0 });
            if (matched <= most)
                continue;

            most = matched;
            best = i;
        }
        return best;
    }

    /// <summary>Tokens that hold a number and may therefore only match digits. Everything else is free
    /// text. Without this, "One - Song" reads as a track called "Song" numbered "One".</summary>
    private static readonly HashSet<string> Numeric = new(StringComparer.Ordinal)
        { "track", "disc", "year", "movement" };

    private static readonly Regex TokenPattern = new(@"\{(\w+)(?::[^}]*)?\}", RegexOptions.Compiled);

    /// <summary>
    /// What <paramref name="pattern"/> says the tags of <paramref name="path"/> are, or null when it
    /// does not match. Keys are bare token names ("track", not "{track:00}"), so a caller never has to
    /// know which spelling of a number token was used.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Parse(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(pattern))
            return null;

        var names = new List<string>();
        var regex = BuildRegex(pattern, names);
        if (regex is null)
            return null;

        // The extension belongs to the file, never to a field: "{title}" must not come back as
        // "On My Way Home.flac".
        var withoutExtension = StripExtension(path.Replace('\\', '/'));

        // As many trailing segments as the pattern asks for. A pattern of one segment is matched
        // against the file name, so the folders above it cannot make it fail.
        var segments = withoutExtension.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var want = pattern.Split('/').Length;
        if (segments.Length < want)
            return null;
        var subject = string.Join('/', segments[^want..]);

        var match = regex.Match(subject);
        if (!match.Success)
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            var value = match.Groups[i + 1].Value.Trim();
            if (value.Length > 0)
                result[names[i]] = value;
        }
        return result;
    }

    /// <summary>The pattern as an anchored regex, with one capture per token in the order they appear.
    /// Null when the pattern holds no token at all, which would match everything and mean nothing.</summary>
    private static Regex? BuildRegex(string pattern, List<string> names)
    {
        var sb = new StringBuilder("^");
        var at = 0;
        foreach (Match token in TokenPattern.Matches(pattern))
        {
            sb.Append(Regex.Escape(pattern[at..token.Index]));
            var name = token.Groups[1].Value.ToLowerInvariant();
            names.Add(name);
            // Numbers take digits and nothing else. Text is LAZY and refuses a slash: a field lives
            // inside one path segment, and a greedy one would swallow the separator its neighbour is
            // waiting for. "Simon - Garfunkel - The Boxer" has to break at the first dash, not the last.
            sb.Append(Numeric.Contains(name) ? "(\\d+)" : "([^/]+?)");
            at = token.Index + token.Length;
        }
        if (names.Count == 0)
            return null;

        sb.Append(Regex.Escape(pattern[at..])).Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }

    private static string StripExtension(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        return dot > slash && dot > 0 ? path[..dot] : path;
    }
}
