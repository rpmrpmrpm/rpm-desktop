namespace RPM.Core.Metadata.Providers;

/// <summary>
/// What somebody did in a band, written the way a person would say it.
///
/// <para>MusicBrainz gives the answer in the <c>member of band</c> relation, as an <c>attributes</c> list.
/// That list is not a role. It holds everything the catalogue knows about the membership, in no particular
/// order and in its own vocabulary. Here is the real list for Dire Straits, the nine members the app
/// draws:</para>
///
/// <code>
/// David Knopfler   guitar | original
/// Pick Withers     membranophone | original
/// Mark Knopfler    guitar | lead vocals | original
/// Chris White      additional | saxophone
/// </code>
///
/// <para><b>So this is not "take the first one".</b> That would give Chris White the role "additional" and
/// Pick Withers the role "membranophone". Two steps are needed first.</para>
///
/// <para><b>Words that are not roles are removed.</b> "Original", "additional", "guest" and the rest say
/// how somebody was in the band, not what they played. They can appear anywhere in the list, including
/// first.</para>
///
/// <para><b>The instrument family is translated into English.</b> MusicBrainz files instruments under the
/// Hornbostel-Sachs families. So a drummer is a "membranophone". That is correct, but nobody wants that
/// word on a line under a record sleeve.</para>
/// </summary>
public static class BandRoles
{
    /// <summary>
    /// Attributes that describe the membership, not the instrument.
    ///
    /// <para>These are true facts about a band, such as "original member". They answer a different question
    /// from the one this code asks. A screen that wanted to show the founders would read these same
    /// attributes for the other half.</para>
    /// </summary>
    private static readonly HashSet<string> NotARole = new(StringComparer.OrdinalIgnoreCase)
    {
        "original", "additional", "guest", "founder", "solo", "co-founder",
    };

    /// <summary>
    /// The Hornbostel-Sachs families MusicBrainz files instruments under, in plain words.
    ///
    /// <para>These appear when the catalogue knows that somebody played a kind of instrument, but not which
    /// one. "Membranophone" is the most common, and it means a drummer.</para>
    /// </summary>
    private static readonly Dictionary<string, string> Plainly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["membranophone"] = "drums",
        ["idiophone"] = "percussion",
        ["aerophone"] = "wind",
        ["chordophone"] = "strings",
        ["electrophone"] = "electronics",
        ["other instruments"] = "instruments",
    };

    /// <summary>
    /// How many roles one line shows.
    ///
    /// <para>Two. The line shares a row with a name and is cut off at the end of it. Two is also the number
    /// that says something real. One would print "guitar" for a man who sang every word too (Mark Knopfler
    /// is "guitar, lead vocals"). Three would list all of a session player's instruments.</para>
    /// </summary>
    public const int MostRoles = 2;

    /// <summary>
    /// The roles in one membership's attributes, or null when the catalogue names none.
    ///
    /// <para>This returns null, not an empty string. That matters. The screens draw nothing where there is
    /// no role. They do not fall back to the person's birth date. A date says nothing about why that name
    /// is on the record.</para>
    /// </summary>
    /// <param name="attributes">The relation's attribute list, in the order MusicBrainz gave it. That is
    /// the order they are shown in. The catalogue does not rank them, and inventing a ranking here would
    /// invent a fact.</param>
    public static string? Spoken(IEnumerable<string>? attributes)
    {
        if (attributes is null)
            return null;

        var roles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var attribute in attributes)
        {
            if (attribute is not { Length: > 0 })
                continue;

            var word = attribute.Trim();
            if (word.Length == 0 || NotARole.Contains(word))
                continue;

            if (Plainly.TryGetValue(word, out var plain))
                word = plain;

            // Two attributes can become one word after the families are translated. A drummer credited as
            // both "membranophone" and "drums" is one drummer.
            if (!seen.Add(word))
                continue;

            roles.Add(word);
            if (roles.Count == MostRoles)
                break;
        }

        return roles.Count == 0 ? null : string.Join(", ", roles);
    }
}
