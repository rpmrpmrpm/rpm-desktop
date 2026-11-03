namespace RPM.Core.Metadata;

/// <summary>
/// The soloists tag read as two lists: the people who played, and the people who sang.
///
/// <para>The file keeps ONE tag for both, the way MusicBrainz and Discogs credit them and Picard writes
/// them: each person once, with what they did in brackets, "Cecilia Bartoli (mezzo-soprano); András Schiff
/// (piano)". The editors show it as two lists because a classical record is read that way, the singers
/// apart from the players. Splitting the tag in the file instead would leave a second tag that no other
/// player reads.</para>
///
/// <para>What decides the list is the role in brackets. A person who both played and sang is a
/// vocalist, with every role kept. A person with no role at all is a performer, because a sleeve that
/// names nobody's voice is crediting the band.</para>
/// </summary>
public static class SoloistCredits
{
    /// <summary>
    /// The entries of a soloists tag, in the order they were credited.
    ///
    /// <para>Cut at a semicolon or a new line, which is how this app and Picard write the list, and at a
    /// comma that closes a role, which is how a good many taggers write it: "Theo Adam (bass), Edith Mathis
    /// (soprano)". A comma anywhere else is part of a name ("Bach, Johann Sebastian") and stays.</para>
    /// </summary>
    public static IReadOnlyList<string> Entries(string? soloists) =>
        string.IsNullOrWhiteSpace(soloists)
            ? []
            : [.. EntryBreak.Split(soloists).Select(e => e.Trim()).Where(e => e.Length > 0)];

    private static readonly System.Text.RegularExpressions.Regex EntryBreak =
        new(@"\s*;\s*|\r?\n|(?<=\))\s*,\s*", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The people who played, each as credited.</summary>
    public static IReadOnlyList<string> Performers(string? soloists) =>
        [.. Entries(soloists).Where(e => !IsVocalist(e))];

    /// <summary>The people who sang, each as credited.</summary>
    public static IReadOnlyList<string> Vocalists(string? soloists) =>
        [.. Entries(soloists).Where(IsVocalist)];

    /// <summary>
    /// The tag again, from the two lists: the performers first, then the vocalists. Null when both are
    /// empty, so an emptied field clears the tag rather than writing a blank one.
    ///
    /// <para>A name typed under Vocalists with no role is given "(vocals)". Without it the next read would
    /// put the singer back under Performers, because the role is the only thing the file says about
    /// them.</para>
    /// </summary>
    public static string? Join(IEnumerable<string> performers, IEnumerable<string> vocalists)
    {
        var entries = Clean(performers)
            .Concat(Clean(vocalists).Select(v => IsVocalist(v) ? v : $"{v} (vocals)"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return entries.Count == 0 ? null : string.Join("; ", entries);
    }

    /// <summary>The tag with its performers replaced and its vocalists kept.</summary>
    public static string? WithPerformers(string? soloists, string? performers) =>
        Join(Entries(performers), Vocalists(soloists));

    /// <summary>The tag with its vocalists replaced and its performers kept.</summary>
    public static string? WithVocalists(string? soloists, string? vocalists) =>
        Join(Performers(soloists), Entries(vocalists));

    /// <summary>
    /// Whether an entry credits a voice.
    ///
    /// <para>Any role that names a voice counts: "vocals" and "voice" in every form the catalogues use
    /// ("lead vocals", "soprano vocals", "Voice"), and the voice types on their own, which is how a
    /// classical sleeve credits a singer ("Ian Bostridge (tenor)"). The voice types count only as a whole
    /// role, so a tenor saxophone is still an instrument. Bass is left out on its own: on its own it is
    /// nearly always the instrument, and the singer is credited "bass vocals" or "bass-baritone".</para>
    /// </summary>
    public static bool IsVocalist(string entry)
    {
        var open = entry.IndexOf('(');
        var close = entry.LastIndexOf(')');
        if (open < 0 || close <= open)
            return false;

        foreach (var role in entry[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries))
        {
            var r = role.ToLowerInvariant();
            if (r.Contains("vocal", StringComparison.Ordinal)
                || r.Contains("voice", StringComparison.Ordinal)
                || r.Contains("singer", StringComparison.Ordinal)
                || VoiceTypes.Contains(r))
                return true;
        }
        return false;
    }

    private static readonly HashSet<string> VoiceTypes =
    [
        "soprano", "mezzo-soprano", "mezzo", "alto", "contralto", "countertenor", "tenor", "baritone",
        "bass-baritone", "treble", "boy soprano",
    ];

    private static IEnumerable<string> Clean(IEnumerable<string> entries) =>
        entries.Select(e => e.Trim()).Where(e => e.Length > 0);
}
