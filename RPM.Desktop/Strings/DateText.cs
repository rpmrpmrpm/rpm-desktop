using System.Globalization;
using System.Text.RegularExpressions;

namespace RPM.Desktop.Strings;

/// <summary>
/// A day written the way the reader's culture writes one: "Oct 8, 2026" in the US, "8 oct. 2026" in
/// France, "8. Okt. 2026" in Germany, "2026年10月8日" in China.
///
/// <para>A fixed pattern such as <c>d MMM yyyy</c> puts the day first in every culture and reads oddly
/// wherever the year or the month leads. .NET has no medium date pattern, so this one is the culture's
/// long date pattern with the weekday taken out, and for <see cref="Short"/> the month abbreviated.</para>
///
/// <para>The culture is a parameter rather than read here, so a caller states which one it means and
/// a test can ask for any of them.</para>
/// </summary>
internal static partial class DateText
{
    /// <summary>The day with the month abbreviated, for the line under a review, a comment or a wall
    /// post.</summary>
    public static string Short(DateTime when, CultureInfo culture)
        => when.ToString(Pattern(culture, abbreviated: true), culture);

    /// <summary>The day with the month in full, for a release date.</summary>
    public static string Long(DateTime when, CultureInfo culture)
        => when.ToString(Pattern(culture, abbreviated: false), culture);

    /// <summary>The culture's long date pattern without its weekday.</summary>
    internal static string Pattern(CultureInfo culture, bool abbreviated)
    {
        var pattern = Weekday().Replace(culture.DateTimeFormat.LongDatePattern, "").Trim();
        return abbreviated ? pattern.Replace("MMMM", "MMM", StringComparison.Ordinal) : pattern;
    }

    /// <summary>The weekday and the separator that joins it to the date, whether it leads ("dddd, ")
    /// or trails (" dddd").</summary>
    [GeneratedRegex(@"^\s*dddd[\s,.،、]*|[\s,.،、]*dddd\s*$")]
    private static partial Regex Weekday();
}
