using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RPM.Fingerprint.Catalog;

/// <summary>
/// Cleans the metadata that arrives with user fingerprint reports, and decides when two reports
/// are making the same claim. User libraries carry whatever tags their files happened to have:
/// "CELIne   dion", "celine dion", "Céline Dion (feat.)" all mean one artist.
///
/// <para>Two forms, on purpose. <see cref="Clean"/> is the DISPLAY form. It is conservative, because
/// it is what gets stored and later served as an answer: whitespace tidied, obviously broken
/// casing repaired, deliberate casing left alone. <see cref="ComparisonKey"/> is the EQUIVALENCE
/// form. It is aggressive, because it only ever decides "do these two reports agree", and the
/// fingerprint has already said the audio is the same: case folded, accents dropped, punctuation
/// dropped, feat-clauses and remaster-noise stripped.</para>
/// </summary>
public static partial class MetadataCleaner
{
    /// <summary>The display form: trimmed, inner whitespace runs collapsed, invisible characters
    /// removed, Unicode composed (NFC), and broken casing repaired word by word. "CELIne   dion"
    /// comes out "Celine Dion"; "McCartney" and "MJ" pass through untouched.</summary>
    public static string Clean(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var text = value.Normalize(NormalizationForm.FormC);

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // Any whitespace (tab, newline, NBSP, thin space) is meant as a space. It is checked
            // before the control check, or tabs would be deleted and glue their words together.
            if (char.IsWhiteSpace(ch))
                sb.Append(' ');
            // Control and zero-width characters (ZWSP/ZWNJ/ZWJ/BOM) carry nothing anyone meant.
            else if (char.IsControl(ch) || ch is '\u200B' or '\u200C' or '\u200D' or '\uFEFF')
                continue;
            else
                sb.Append(ch);
        }

        var words = sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RepairWordCasing);
        return string.Join(' ', words);
    }

    /// <summary>Null-tolerant <see cref="Clean"/>; a cleaned-to-nothing value comes back null.</summary>
    public static string? CleanOptional(string? value)
    {
        if (value is null)
            return null;
        var cleaned = Clean(value);
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// The album title as a RELEASE GROUP is titled: the display form with the feat clause and the
    /// edition parenthetical taken off. Null when nothing readable is left.
    ///
    /// <para>A release group is the album; "(Deluxe Edition)", "(Remastered 2011)" and "(feat. …)"
    /// name a release, a remaster or a track, none of which the group carries. Asking the catalogue
    /// for <c>releasegroup:"Them Vs. You Vs. Me (Deluxe Edition)"</c> therefore finds nothing at all,
    /// where the same query without the suffix finds the record first time.</para>
    ///
    /// <para>A third form rather than either of the two above, because neither fits: <see cref="Clean"/>
    /// keeps the suffix, and <see cref="ComparisonKey"/> drops it but also folds case and strips
    /// punctuation, which is right for comparing two strings and wrong for a query somebody has to
    /// read. The stripping is narrow by construction: <see cref="NoiseParenthetical"/> only matches a
    /// bracket that actually contains one of its words, so "(What's the Story) Morning Glory?" comes
    /// through whole.</para>
    /// </summary>
    public static string? ReleaseGroupTitle(string? value)
    {
        if (value is null)
            return null;

        var stripped = NoiseParenthetical().Replace(value, " ");
        stripped = FeatClause().Replace(stripped, " ");

        var cleaned = Clean(stripped);
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>True when two values are the same claim once the noise is gone, see
    /// <see cref="ComparisonKey"/>. Two nulls agree; a null and a value do not.</summary>
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return ComparisonKey(a) == ComparisonKey(b);
    }

    /// <summary>
    /// The equivalence form, for deciding agreement only. It is never stored or shown:
    /// case folded, diacritics dropped ("Céline" = "Celine"), "&amp;" read as "and",
    /// feat/ft/featuring clauses and remaster/edition parentheticals stripped, every remaining
    /// non-letter-or-digit turned to space, and a leading "the " dropped.
    /// </summary>
    public static string ComparisonKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var text = Clean(value).ToLowerInvariant();
        text = StripDiacritics(text);
        text = text.Replace("&", " and ");
        text = FeatClause().Replace(text, " ");
        text = NoiseParenthetical().Replace(text, " ");

        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0)
                    sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }
            else
            {
                pendingSpace = true;
            }
        }

        var key = sb.ToString();
        return key.StartsWith("the ", StringComparison.Ordinal) ? key[4..] : key;
    }

    /// <summary>
    /// Repairs a word whose casing is evidently broken and leaves deliberate casing alone.
    /// Broken means: an upper-case run of two or more letters falling into lower case ("CELIne"),
    /// all lower case, or all upper case at three letters or longer ("DION"). The cost is that acronym
    /// bands are changed, so "ABBA" becomes "Abba". That is accepted for a junk cleanup. "McCartney",
    /// "iPod", "MJ" and "U2" all read as deliberate and pass through.
    /// </summary>
    private static string RepairWordCasing(string word)
    {
        var letters = word.Count(char.IsLetter);
        if (letters == 0)
            return word;

        var upper = word.Count(char.IsUpper);
        var lower = letters - upper;

        var broken = BrokenUpperRun().IsMatch(word)
            || lower == 0 && letters >= 3
            || upper == 0;
        if (!broken)
            return word;

        // The first letter of the word, and of each -, /, ., & or + separated segment, goes up,
        // the rest down. Apostrophes are not segment breaks: "don't" → "Don't".
        var chars = word.ToLowerInvariant().ToCharArray();
        var startOfSegment = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (startOfSegment && char.IsLetter(chars[i]))
            {
                chars[i] = char.ToUpper(chars[i], CultureInfo.InvariantCulture);
                startOfSegment = false;
            }
            else if (chars[i] is '-' or '/' or '.' or '&' or '+')
            {
                startOfSegment = true;
            }
        }
        return new string(chars);
    }

    private static string StripDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>An upper-case run of two or more letters followed by lower case, such as "CELIne" or "DIon".</summary>
    [GeneratedRegex(@"\p{Lu}{2,}\p{Ll}")]
    private static partial Regex BrokenUpperRun();

    /// <summary>A feat/ft/featuring/with clause, bracketed or trailing bare.</summary>
    [GeneratedRegex(@"[(\[{]\s*(feat\.?|ft\.?|featuring|with)\b[^)\]}]*[)\]}]?|\b(feat\.?|ft\.?|featuring)\b.*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FeatClause();

    /// <summary>A bracketed remaster, edition or version note. It is noise about the same audio, which the
    /// fingerprint has already vouched for.</summary>
    [GeneratedRegex(@"[(\[{][^)\]}]*\b(remaster(ed)?|mono|stereo|deluxe|anniversary|edition|version|edit|single|album|bonus|explicit|clean|live|demo|remix|mix)\b[^)\]}]*[)\]}]",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoiseParenthetical();
}
