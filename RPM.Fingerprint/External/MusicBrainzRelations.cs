using System.Text.Json;

namespace RPM.Fingerprint.External;

/// <summary>
/// Reads the parts of a MusicBrainz release that are not the track list: who is credited for what,
/// which composition a recording is a performance of, and what is printed on the sleeve.
///
/// <para>Kept separate from <see cref="MusicBrainzClient"/> because it does different work. The
/// client fetches and rate-limits; this follows a relationship graph. MusicBrainz has no
/// "composer" field. It has a <c>work</c> that a <c>recording</c> is a performance <i>of</i>, and the
/// composer is an artist related to that work. A classical track's credits are correct only if that
/// chain is followed, and the classical fields can be filled from the catalogue only through
/// it.</para>
/// </summary>
internal static class MusicBrainzRelations
{
    /// <summary>An artist as a credit names them: what to print, what to sort by, and who they are.</summary>
    internal readonly record struct Credit(string? Name, string? SortName, string? Id)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Name);
    }

    /// <summary>Every artist credited on an element, joined with the credit's own punctuation.
    /// MusicBrainz gives the "feat. " and " &amp; " between names as <c>joinphrase</c>. Without them,
    /// "Simon &amp; Garfunkel" becomes "Simon, Garfunkel".</summary>
    internal static Credit ArtistCredit(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out var credits) ||
            credits.ValueKind != JsonValueKind.Array)
            return default;

        var name = new System.Text.StringBuilder();
        var sort = new System.Text.StringBuilder();
        string? firstId = null;

        foreach (var credit in credits.EnumerateArray())
        {
            var artist = credit.TryGetProperty("artist", out var a) ? a : default;
            string? displayed = Str(credit, "name") ?? Str(artist, "name");
            if (displayed is null)
                continue;

            name.Append(displayed);
            sort.Append(Str(artist, "sort-name") ?? displayed);
            firstId ??= Str(artist, "id");

            if (Str(credit, "joinphrase") is { } join)
            {
                name.Append(join);
                sort.Append(join);
            }
        }

        return name.Length == 0
            ? default
            : new Credit(name.ToString(), sort.ToString(), firstId);
    }

    /// <summary>
    /// The credits on the recording itself: the people who performed it, not the people who wrote
    /// what they performed.
    /// </summary>
    /// <param name="recording">A recording element with <c>relations</c>. Needs
    /// <c>artist-rels</c> and <c>recording-level-rels</c> on the request.</param>
    internal static RecordingCredits Recording(JsonElement recording)
    {
        var conductors = new List<string>();
        var ensembles = new List<string>();
        var soloists = new List<string>();

        foreach (var relation in Relations(recording))
        {
            if (Str(relation, "target-type") is not null and not "artist")
                continue;
            if (Str(relation, "artist", "name") is not { } who)
                continue;

            switch (Str(relation, "type"))
            {
                case "conductor":
                    conductors.Add(who);
                    break;

                // A performer relation's attributes say WHAT they played: "orchestra" and "choir"
                // are the ensemble, an instrument or "vocals" is a soloist. A performer with no
                // attributes is a soloist, because an unnamed instrument is still one person.
                case "performer" or "performing orchestra" or "vocal" or "instrument":
                    var attributes = Attributes(relation);
                    if (attributes.Any(a => a is "orchestra" or "choir vocals" or "choir"))
                        ensembles.Add(who);
                    else
                        soloists.Add(Describe(who, attributes));
                    break;
            }
        }

        return new RecordingCredits(Join(conductors), Join(ensembles), Join(soloists));
    }

    /// <summary>The conductor, the ensemble and the soloists.</summary>
    internal readonly record struct RecordingCredits(string? Conductor, string? Ensemble, string? Soloists);

    /// <summary>
    /// The composition a recording is a performance of, and the movement of it this track is.
    ///
    /// <para>MusicBrainz stores a symphony as a parent work with one child work per movement, so a
    /// recording of the first movement links to the child. The child's title <i>is</i> the movement
    /// ("I. Allegro con brio") and the parent's title is the work ("Symphony No. 5 in C minor, Op. 67").
    /// This is the pair that iTunes and Roon expect, in the opposite order from the JSON. A recording
    /// of a whole piece links directly to a work with no parent, and then there is a work and no
    /// movement, which is also correct: Boléro has no movements.</para>
    ///
    /// <para>The movement number comes from the <c>ordering-key</c> on the part relation, not from
    /// counting, because a work's parts can be in any order in the JSON.</para>
    /// </summary>
    /// <param name="recording">Needs <c>work-rels</c>, <c>work-level-rels</c> and <c>artist-rels</c>
    /// on the request. Without the work-level ones, the parent link and the composer are both
    /// missing.</param>
    internal static WorkCredits Work(JsonElement recording)
    {
        foreach (var relation in Relations(recording))
        {
            if (Str(relation, "type") != "performance" ||
                !relation.TryGetProperty("work", out var work))
                continue;

            string? title = Str(work, "title");
            string? workTitle = title;
            string? movement = null;
            uint movementNumber = 0;

            // "part of", pointing back to the parent: this recording is one movement of a larger work.
            foreach (var partOf in Relations(work))
            {
                if (Str(partOf, "type") != "parts" || Str(partOf, "direction") != "backward" ||
                    !partOf.TryGetProperty("work", out var parent))
                    continue;

                workTitle = Str(parent, "title") ?? title;
                movement = title;
                if (Str(partOf, "ordering-key") is { } key && uint.TryParse(key, out var n))
                    movementNumber = n;
                else if (partOf.TryGetProperty("ordering-key", out var ok) && ok.TryGetUInt32(out var nn))
                    movementNumber = nn;
                break;
            }

            var (composers, composerSort) = People(work, "composer");
            var (lyricists, _) = People(work, "lyricist", "librettist", "writer");
            var (arrangers, _) = People(work, "arranger", "orchestrator");

            return new WorkCredits(
                workTitle, movement, movementNumber, composers, composerSort, lyricists, arrangers);
        }

        return default;
    }

    /// <summary>What the composition says about itself. Every member is null when the recording is not
    /// linked to a work, which is most popular music.</summary>
    internal readonly record struct WorkCredits(
        string? Work,
        string? MovementName,
        uint MovementNumber,
        string? Composers,
        string? ComposerSort,
        string? Lyricist,
        string? Arranger);

    /// <summary>The artists related to a work in any of the given roles, and their sort names
    /// ("Beethoven, Ludwig van"). Sort names are what let classical music be sorted by composer.</summary>
    private static (string? Names, string? SortNames) People(JsonElement work, params string[] roles)
    {
        var names = new List<string>();
        var sorts = new List<string>();

        foreach (var relation in Relations(work))
        {
            if (Str(relation, "type") is not { } type || !roles.Contains(type))
                continue;
            if (Str(relation, "artist", "name") is not { } who)
                continue;
            names.Add(who);
            sorts.Add(Str(relation, "artist", "sort-name") ?? who);
        }

        return (Join(names), Join(sorts));
    }

    // ── the pressing ──

    /// <summary>What is printed on the sleeve: the label that released it and its catalogue number.
    /// A release can list several labels; the first one with a name is the one printed on the
    /// spine.</summary>
    internal static (string? Label, string? CatalogNumber) LabelInfo(JsonElement release)
    {
        if (!release.TryGetProperty("label-info", out var infos) || infos.ValueKind != JsonValueKind.Array)
            return (null, null);

        string? label = null;
        string? catalogNumber = null;
        foreach (var info in infos.EnumerateArray())
        {
            label ??= Str(info, "label", "name");
            catalogNumber ??= Str(info, "catalog-number");
            if (label is not null && catalogNumber is not null)
                break;
        }
        return (label, catalogNumber);
    }

    /// <summary>The genre with the most MusicBrainz votes. Its <c>genres</c> are a curated list,
    /// unlike the free-text <c>tags</c> next to them, so only genres are read: a tag list can contain
    /// "albums i own" as a genre.</summary>
    internal static string? TopGenre(params JsonElement[] elements)
    {
        string? best = null;
        int bestCount = -1;

        foreach (var element in elements)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var genre in genres.EnumerateArray())
            {
                int count = genre.TryGetProperty("count", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
                if (Str(genre, "name") is { } name && count > bestCount)
                    (best, bestCount) = (Capitalise(name), count);
            }
        }
        return best;
    }

    /// <summary>
    /// Every voted genre of a release and its release group, most votes first, capitalised the way
    /// <see cref="TopGenre"/> capitalises its one answer.
    ///
    /// <para><b>Why the whole list.</b> <see cref="TopGenre"/> keeps one word, and the word with the most
    /// votes is usually the broadest: "electronic" outvotes "trip hop" on most trip hop records. The broad
    /// word gives a shelf and nothing narrower. The app files records under a shelf AND a style, and the
    /// style is almost always further down the list. So the list goes over the wire, and the reader decides
    /// which of it fits its own shelves.</para>
    ///
    /// <para><b>Votes are summed across the release and its group.</b> A pressing often carries no genres
    /// of its own, or one or two, while the release group has the record's real tally. One genre voted on
    /// both is one genre with the votes of both, not two entries. Ties keep the order the genres were first
    /// met in, release before group, which is MusicBrainz's own order.</para>
    /// </summary>
    internal static IReadOnlyList<string> VotedGenres(params JsonElement[] elements)
    {
        var votes = new Dictionary<string, (string Name, int Count, int First)>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in elements)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var genre in genres.EnumerateArray())
            {
                if (Str(genre, "name") is not { } raw || raw.Trim() is not { Length: > 0 } name)
                    continue;
                int count = genre.TryGetProperty("count", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
                votes[name] = votes.TryGetValue(name, out var had)
                    ? had with { Count = had.Count + count }
                    : (Capitalise(name), count, votes.Count);
            }
        }

        return [.. votes.Values.OrderByDescending(v => v.Count).ThenBy(v => v.First).Select(v => v.Name)];
    }

    /// <summary>MusicBrainz stores genres in lower case. Tag editors and other players show them
    /// capitalised, and a library that mixes "rock" and "Rock" shows two entries for one genre.</summary>
    private static string Capitalise(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length <= 1 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>The four-digit year of a date MusicBrainz may have given as "1977", "1977-02" or
    /// "1977-02-04".</summary>
    internal static uint Year(JsonElement element, string property)
        => Str(element, property) is { Length: >= 4 } date && uint.TryParse(date[..4], out var year)
            ? year : 0u;

    /// <summary>The first ISRC on a recording. A recording can have several (reissues get their own),
    /// and any of them identifies it, so the first one is used.</summary>
    internal static string? FirstIsrc(JsonElement recording)
        => recording.TryGetProperty("isrcs", out var isrcs) && isrcs.ValueKind == JsonValueKind.Array
            ? isrcs.EnumerateArray().Select(i => i.GetString()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            : null;

    // ── json plumbing ──

    private static IEnumerable<JsonElement> Relations(JsonElement element)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty("relations", out var relations) &&
           relations.ValueKind == JsonValueKind.Array
            ? relations.EnumerateArray()
            : [];

    private static List<string> Attributes(JsonElement relation)
        => relation.TryGetProperty("attributes", out var attributes) &&
           attributes.ValueKind == JsonValueKind.Array
            ? [.. attributes.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0)]
            : [];

    /// <summary>"Martha Argerich (piano)". The instrument makes a list of soloists easier to read.</summary>
    private static string Describe(string who, List<string> attributes)
    {
        var played = attributes.Where(a => a is not ("solo" or "guest" or "additional" or "live")).ToList();
        return played.Count == 0 ? who : $"{who} ({string.Join(", ", played)})";
    }

    private static string? Str(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? Clean(value.GetString())
            : null;

    private static string? Str(JsonElement element, string property, string nested)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? Str(value, nested)
            : null;

    private static string? Join(List<string> values)
        => values.Count == 0 ? null : string.Join("; ", values.Distinct(StringComparer.Ordinal));

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
