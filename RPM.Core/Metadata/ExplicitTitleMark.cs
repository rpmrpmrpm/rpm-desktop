using RPM.Core.Models;

namespace RPM.Core.Metadata;

/// <summary>
/// The explicit mark that some download tools add to the end of a song title, as in "Killah 🅴".
///
/// <para>rpm marks an explicit song with its own badge, drawn from the advisory flag. A title that also
/// carries the mark shows two marks in two styles. So the mark is never kept as part of a title. It is
/// taken off, and it counts as the advisory flag when the track has none.</para>
///
/// <para>Two places apply this: <see cref="TagReader.Read"/>, where a title comes out of a file, and the
/// mirror's row write, which every title passes through on its way into the library, whether it came
/// from a file, the cloud catalogue or another computer.</para>
///
/// <para>Only a mark at the end of the title, after a space, is taken off. That is where the tools put
/// it. A mark anywhere else may be part of the name, so it is left alone.</para>
/// </summary>
public static class ExplicitTitleMark
{
    /// <summary>U+1F174, NEGATIVE SQUARED LATIN CAPITAL LETTER E: the character the tools write.</summary>
    public const string Mark = "\U0001F174";

    /// <summary>The title without the mark at its end, and whether it had one. A title without the mark
    /// comes back exactly as it was given.</summary>
    public static (string? Title, bool Marked) Split(string? title)
    {
        if (title is null)
            return (null, false);

        var end = title.TrimEnd();
        if (!end.EndsWith(Mark, StringComparison.Ordinal))
            return (title, false);

        var rest = end[..^Mark.Length];
        if (rest.Length == 0 || !char.IsWhiteSpace(rest[^1]))
            return (title, false);

        return (rest.TrimEnd(), true);
    }

    /// <summary>The advisory flag once the mark is taken into account: explicit for a marked title when
    /// there is no flag, and otherwise the flag as it is. A flag that the file states is the file's own
    /// answer, and the mark does not overrule it.</summary>
    public static ContentAdvisory? Advisory(bool marked, ContentAdvisory? advisory) =>
        marked && (advisory is null or ContentAdvisory.Unknown) ? ContentAdvisory.Explicit : advisory;
}
