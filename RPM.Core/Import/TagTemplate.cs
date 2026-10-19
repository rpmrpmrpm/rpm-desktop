using System.Text;
using System.Text.RegularExpressions;

namespace RPM.Core.Import;

/// <summary>
/// Builds one tag's value out of the other tags, against a pattern in the same vocabulary
/// <see cref="NamingTemplate"/> files with and <see cref="FilenameTags"/> reads back:
/// <c>{artist} - {title}</c>, <c>{track:00}</c>, and so on. Learn the tokens once and they work
/// wherever the app asks for a pattern.
///
/// <para>Where the naming template renders a PATH (sanitised, one segment per folder, extension on
/// the end), this renders a VALUE. Nothing is sanitised, because a slash or a colon in a comment is a
/// slash or a colon. Nothing is invented: a token whose field is empty renders empty, and the
/// separator it was leaning on goes with it. "{track:00} - {title}" on an unnumbered track is the
/// title, not " - " and the title.</para>
///
/// <para>The values come from a lookup rather than from a track, so the rule can be checked without a
/// row, a store or a window. What this class knows is patterns, not tags.</para>
/// </summary>
public static class TagTemplate
{
    /// <summary>The patterns the dialog opens with. Each is something somebody actually wants: the
    /// title carrying its performer, the file's own number in front of it, a comment saying where the
    /// record came from.</summary>
    public static readonly IReadOnlyList<string> Presets =
    [
        "{artist} - {title}",
        "{track:00} - {title}",
        "{title} ({artist})",
        "{artist} - {album} - {title}",
        "{album} ({year})",
        // The two single-token patterns copy a value across. {artist} fills in the album artist of a
        // record that never had one. {albumartist} fills in the artist of a compilation whose tracks
        // lost theirs.
        "{albumartist}",
        "{artist}",
        "{album}",
    ];

    /// <summary>Characters a token leans on: the glue between two fields, and never part of one.</summary>
    private static readonly char[] Glue = [' ', '-', '.', '_', ',', '/', ':', ';', '|'];

    private static readonly Regex TokenPattern = new(@"\{(\w+)(?::([^}]*))?\}", RegexOptions.Compiled);

    /// <summary>
    /// Renders <paramref name="pattern"/> against <paramref name="value"/>, which answers a bare token
    /// name ("track", never "{track:00}") with what that field currently holds, or null when there is
    /// no such field.
    /// </summary>
    public static string Render(string pattern, Func<string, string?> value)
    {
        if (string.IsNullOrEmpty(pattern))
            return "";

        // Literals and tokens, alternating: literal 0, token 0, literal 1, token 1, and so on. So a
        // token's glue is the literal either side of it, and it can be taken away with the token.
        var literals = new List<string>();
        var values = new List<string>();
        var at = 0;
        foreach (Match token in TokenPattern.Matches(pattern))
        {
            literals.Add(pattern[at..token.Index]);
            values.Add(Field(
                value(token.Groups[1].Value.ToLowerInvariant()),
                token.Groups[2].Success ? token.Groups[2].Value : ""));
            at = token.Index + token.Length;
        }
        literals.Add(pattern[at..]);

        // A field that said nothing takes its punctuation with it. Which side goes depends on what the
        // punctuation was doing. A bracket that opened around the field closes with it. Glue is dropped
        // on the side where it would otherwise be left hanging: before the token when there is something
        // in front of it, after when the token was the opening word.
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Length > 0)
                continue;

            var before = literals[i];
            var after = literals[i + 1];

            if (Bracketed(before, after, out var openAt, out var closeAt))
            {
                literals[i] = before[..openAt].TrimEnd(Glue);
                literals[i + 1] = after[(closeAt + 1)..];
                continue;
            }

            var anythingYet = values.Take(i).Any(v => v.Length > 0)
                || literals.Take(i + 1).Any(Meaningful);

            if (anythingYet)
                literals[i] = Meaningful(before) ? before.TrimEnd(Glue) : "";
            else if (!Meaningful(after))
                literals[i + 1] = "";
        }

        var built = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
            built.Append(literals[i]).Append(values[i]);
        built.Append(literals[^1]);

        return built.ToString().Trim().Trim(Glue).Trim();
    }

    /// <summary>Whether the token stood inside a bracket of its own, as in "{album} ({year})". If so,
    /// the bracket has nothing left to hold and goes with it.</summary>
    private static bool Bracketed(string before, string after, out int openAt, out int closeAt)
    {
        openAt = -1;
        closeAt = -1;

        var trimmed = before.TrimEnd(Glue);
        if (trimmed.Length == 0)
            return false;

        var open = trimmed[^1];
        var close = open switch { '(' => ')', '[' => ']', '{' => '}', _ => '\0' };
        if (close == '\0')
            return false;

        var end = after.IndexOf(close);
        if (end < 0 || Meaningful(after[..end]))
            return false;

        openAt = trimmed.Length - 1;
        closeAt = end;
        return true;
    }

    /// <summary>Whether a literal says anything of its own, or is only the glue between two fields.</summary>
    private static bool Meaningful(string literal) => literal.Any(char.IsLetterOrDigit);

    /// <summary>Every token the pattern names, in the order it names them. A preview underlines these,
    /// and a validator checks them against the fields it has.</summary>
    public static IReadOnlyList<string> TokensIn(string pattern) =>
        [.. TokenPattern.Matches(pattern ?? "").Select(m => m.Groups[1].Value.ToLowerInvariant())];

    /// <summary>One field's contribution: what it holds, formatted if the token asked for a format and
    /// the field holds a number. A zero is an absent number, as it is everywhere else in the app, and it
    /// renders as nothing rather than as "00".</summary>
    private static string Field(string? held, string format)
    {
        var text = (held ?? "").Trim();
        if (text.Length == 0)
            return "";

        if (format.Length == 0)
            return text;

        if (!long.TryParse(text, out var number))
            return text;    // a format on a word is a format that does not apply, not an error
        return number == 0 ? "" : number.ToString(format);
    }
}
