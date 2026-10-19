namespace RPM.Core.Radio;

/// <summary>
/// Pulls an artist and a track out of what a station announces.
///
/// <para>ICY carries one string. The convention is <c>Artist - Title</c>. It is used almost everywhere, but
/// it is never written down. Many stations put a hyphen inside the artist or the title, as in "Emerson,
/// Lake - Palmer" or "Blue Öyster Cult - (Don't Fear) The Reaper - Live". So the split must use the FIRST
/// separator that has spaces around it, not any hyphen. <c>Jay-Z</c> must stay whole.</para>
///
/// <para>What does not look like a pair is left alone. A station announcing its own name, or a bare track
/// title, becomes the title with no artist. That is better than inventing an artist from half a
/// string.</para>
/// </summary>
public static class IcyTitle
{
    /// <summary>The separators that stations use. When more than one occurs in the text, the one that comes
    /// first is used (see <see cref="Split"/>).</summary>
    private static readonly string[] Separators = [" - ", " – ", " — ", " -- "];

    /// <summary>Splits one ICY string. Artist is null when it does not announce one.</summary>
    public static (string? Artist, string? Title) Split(string? announced)
    {
        var text = announced?.Trim();
        if (string.IsNullOrEmpty(text))
            return (null, null);

        int at = -1;
        int width = 0;
        foreach (var separator in Separators)
        {
            int found = text.IndexOf(separator, StringComparison.Ordinal);
            // The earliest separator wins, so "A - B - C" reads as artist "A" and title "B - C". The
            // trailing part is nearly always a remix or a live tag, and it belongs to the title.
            if (found > 0 && (at < 0 || found < at))
            {
                at = found;
                width = separator.Length;
            }
        }

        if (at < 0)
            return (null, text);

        var artist = text[..at].Trim();
        var title = text[(at + width)..].Trim();

        // One empty half is not a pair. An announcement like "- Live from Paris" is a title.
        if (artist.Length == 0 || title.Length == 0)
            return (null, text);

        return (artist, title);
    }

    /// <summary>
    /// The announcement without the station's own name in front of it.
    /// </summary>
    /// <remarks>
    /// Many stations announce themselves before whatever is on. NPO Radio 2 sends "NPO Radio 2 - Dit is
    /// Jorien! - BNNVARA". Printed under a card that already shows the station's name in bold, that reads
    /// as a stutter rather than as information.
    ///
    /// <para>Only a name at the start, followed by a separator, is removed. So a track whose title merely
    /// mentions the station keeps it. What is left is returned whole, separators included. The rest is the
    /// station's own layout of the programme and its broadcaster. This code cannot tell which part of it
    /// matters.</para>
    ///
    /// <para>The original text is returned when removing the name would leave nothing. This happens with a
    /// station that announces only itself, which several stations send between programmes. A repeated name
    /// is better than a blank line.</para>
    /// </remarks>
    public static string? WithoutStation(string? announced, string? station)
    {
        var text = announced?.Trim();
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(station))
            return text;

        var name = station.Trim();
        if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            return text;

        var rest = text[name.Length..];
        foreach (var separator in Separators)
            if (rest.StartsWith(separator, StringComparison.Ordinal))
                return rest[separator.Length..].Trim() is { Length: > 0 } left ? left : text;

        return text;
    }
}
