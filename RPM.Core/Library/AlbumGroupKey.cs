using System.Globalization;
using System.Text;

namespace RPM.Core.Library;

/// <summary>
/// What two records have to have in common before a shelf puts them under one heading.
///
/// <para>Tags are typed by people and by a dozen rippers. So the same genre reaches the library as
/// "Hip Hop", "hip-hop" and "HipHop", and the absence of one reaches it as "", "None", "Unknown" and
/// "N/A". A grouping that takes the raw string at face value draws three Acoustic headings, and it files
/// the untagged records under a genre called None. So the shelf groups by a KEY, not by the spelling.
/// The spelling is only what the heading is printed in.</para>
///
/// <para>The shelf also sorts by this key. The two must agree: a group is only a group if its records are
/// next to each other. So the order and the break between headings are decided by one comparison.
/// Ordinal equality on this key is that comparison.</para>
/// </summary>
public static class AlbumGroupKey
{
    /// <summary>
    /// The spellings that mean "nothing was tagged here". A file whose genre says None has no genre.
    /// The tagger wrote a word in a field that was empty. A shelf that took the word at face value would
    /// invent a genre called None, and place it in the alphabet between Metal and Pop.
    /// </summary>
    private static readonly HashSet<string> Nothing = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "(none)", "null", "(null)", "nil", "unknown", "(unknown)", "undefined", "unspecified",
        "unset", "n/a", "n.a.", "na", "-", "--", "?", "??", "???", "empty", "blank",
    };

    /// <summary>Characters that no tag means to carry: the zero-width characters and the BOM. A bad
    /// encoder can leave the BOM in front of a value, and then one genre would become two.</summary>
    private const string Invisible = "​‌‍⁠﻿";

    /// <summary>
    /// What a raw tag value is worth as a heading: the value itself, tidied. The result is null when it
    /// says nothing. That covers the empty field and every spelling of "nothing" that people write.
    /// </summary>
    public static string? Display(string? raw)
    {
        if (raw is null)
            return null;

        var text = Clean(raw);
        return text.Length == 0 || Nothing.Contains(text) ? null : text;
    }

    /// <summary>Whitespace and invisibles off both ends, inner whitespace collapsed to single spaces,
    /// and the trailing separator a multi-value tag leaves behind ("Rock;") dropped.</summary>
    private static string Clean(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var space = false;
        foreach (var c in raw)
        {
            if (Invisible.Contains(c))
                continue;
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space)
            {
                builder.Append(' ');
                space = false;
            }
            builder.Append(c);
        }
        return builder.ToString().TrimEnd(';', ',', '/', '|');
    }

    /// <summary>
    /// The key two spellings of one thing share: accents folded away, case dropped, and everything that
    /// is not a letter or a digit taken out, so "Hip-Hop", "hip hop" and "HipHop" are one genre, and
    /// "R.E.M." and "REM" are one artist.
    ///
    /// <para>A name made of nothing else, such as the band !!! or a genre written "???", keeps its own
    /// characters as the key. Otherwise it would collapse into the same empty key as every other name
    /// made of symbols.</para>
    /// </summary>
    public static string Key(string? display)
    {
        var text = Clean(display ?? "");
        if (text.Length == 0)
            return "";

        var folded = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToUpperInvariant(c));
        }

        return builder.Length > 0
            ? builder.ToString().Normalize(NormalizationForm.FormC)
            : text.ToUpperInvariant();
    }

    /// <summary>
    /// One spelling per group, for the heading to be printed in: the spelling most of the records under
    /// it actually carry, and the first in ordinal order where two are tied. A heading must not change
    /// between two rebuilds that read the same rows. Otherwise the shelf changes its mind about a name
    /// nobody edited.
    /// </summary>
    /// <returns>Key → the heading to print. Values that say nothing are not in it: they have no group of
    /// their own to name (the shelf gathers them under its own "ungrouped" heading).</returns>
    public static Dictionary<string, string> Headings(IEnumerable<string?> raw)
    {
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var value in raw)
        {
            if (Display(value) is not { } display)
                continue;
            var key = Key(display);
            if (!counts.TryGetValue(key, out var spellings))
                counts[key] = spellings = new Dictionary<string, int>(StringComparer.Ordinal);
            spellings[display] = spellings.TryGetValue(display, out var n) ? n + 1 : 1;
        }

        var headings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, spellings) in counts)
        {
            var best = "";
            var bestCount = -1;
            foreach (var (spelling, count) in spellings)
            {
                if (count > bestCount
                    || (count == bestCount && string.CompareOrdinal(spelling, best) < 0))
                {
                    best = spelling;
                    bestCount = count;
                }
            }
            headings[key] = best;
        }
        return headings;
    }
}
