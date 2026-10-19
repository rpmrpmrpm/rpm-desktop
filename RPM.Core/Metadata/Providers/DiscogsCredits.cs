using System.Text.Json;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// Who is credited on a Discogs release, and which tag each person belongs in.
///
/// <para>Discogs has no composer field, no conductor field and no soloist field. It has one list,
/// <c>extraartists</c>, of "this person, this role". The role is free text that the submitter typed, such
/// as "Composed By", "Conductor", "Fortepiano", "Soprano Vocals", "Liner Notes" or "Lacquer Cut By". Every
/// classical credit is in that list, mixed in with the people who designed the sleeve. Sorting the two
/// apart is the main job of this file.</para>
///
/// <para>A credit can also name the tracks it covers, such as <c>"12-1 to 12-7, 13-1 to 13-8, 14-5"</c>.
/// That is how a box set says that a second pianist plays the last three discs. Those ranges use the
/// listing's own positions. So they can only be resolved against the listing. See
/// <see cref="CreditSheet.For"/>.</para>
/// </summary>
internal static class DiscogsCredits
{
    /// <summary>The classical block as it reaches one track. When every member is null, that is the
    /// ordinary case: most records credit nobody beyond the artist on the front.</summary>
    internal readonly record struct Roles(
        string? Composers,
        string? Lyricist,
        string? Arranger,
        string? Conductor,
        string? Ensemble,
        string? Soloists)
    {
        internal bool IsEmpty =>
            Composers is null && Lyricist is null && Arranger is null &&
            Conductor is null && Ensemble is null && Soloists is null;

        /// <summary>These credits, with <paramref name="under"/> showing through wherever they are silent.
        /// This works role by role, not all or nothing. A track that names its own soloist still takes the
        /// composer that the release states once at the top.</summary>
        internal Roles Over(Roles under) => new(
            Composers ?? under.Composers,
            Lyricist ?? under.Lyricist,
            Arranger ?? under.Arranger,
            Conductor ?? under.Conductor,
            Ensemble ?? under.Ensemble,
            Soloists ?? under.Soloists);

        /// <summary>The track with these credits applied, keeping anything it already has.</summary>
        internal ReleaseTrackInfo Applied(ReleaseTrackInfo track) => track with
        {
            Composers = Composers ?? track.Composers,
            Lyricist = Lyricist ?? track.Lyricist,
            Arranger = Arranger ?? track.Arranger,
            Conductor = Conductor ?? track.Conductor,
            Ensemble = Ensemble ?? track.Ensemble,
            Soloists = Soloists ?? track.Soloists,
        };
    }

    /// <summary>The credits written on one element: a track, or the work a movement came from. Their scope
    /// is the element itself, so nothing here has a range to resolve.</summary>
    internal static Roles Of(JsonElement element) => Sort(Entries(element));

    /// <summary>The credits a release states for itself. The ranges that say which tracks each credit
    /// covers are kept.</summary>
    internal static CreditSheet Sheet(JsonElement root) => new(Entries(root));

    /// <summary>A release's credit list. It answers the question "who played on track 40?".</summary>
    internal sealed class CreditSheet(List<Entry> entries)
    {
        internal bool IsEmpty => entries.Count == 0;

        /// <summary>The credits that reach the track at <paramref name="index"/> in a listing whose
        /// positions are
        /// <paramref name="positions"/>.</summary>
        internal Roles For(int index, IReadOnlyList<string> positions) =>
            entries.Count == 0 ? default : Sort(entries.Where(e => Covers(e.Tracks, index, positions)));
    }

    /// <summary>One line of the credit list: who, for what, and where it applies.</summary>
    /// <param name="Tracks">The positions it covers, as Discogs writes them. Blank is the common case and
    /// means the whole record.</param>
    internal readonly record struct Entry(string Name, string Role, string? Tracks);

    private static List<Entry> Entries(JsonElement element)
    {
        var entries = new List<Entry>();
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("extraartists", out var credits) ||
            credits.ValueKind != JsonValueKind.Array)
            return entries;

        foreach (var credit in credits.EnumerateArray())
        {
            if (Name(credit) is not { } name)
                continue;

            var tracks = Str(credit, "tracks");

            // One line can carry several roles, such as "Fortepiano, Harpsichord" or "Composed By, Arranged
            // By". Each role is a separate fact about the same person.
            foreach (var role in Stated(Str(credit, "role")))
                entries.Add(new Entry(name, role, tracks));
        }

        return entries;
    }

    /// <summary>The name to print. Discogs tells apart people who share a name with a number in brackets,
    /// such as "John Williams (2)". That number is a fact about Discogs' database, not about the sleeve, so
    /// it is removed.</summary>
    private static string? Name(JsonElement credit)
    {
        if (Str(credit, "name") is not { } name)
            return null;

        name = name.TrimEnd('*').TrimEnd();
        int open = name.LastIndexOf('(');
        return open > 0 && name.EndsWith(')') && name[(open + 1)..^1].All(char.IsDigit)
            ? name[..open].TrimEnd()
            : name;
    }

    /// <summary>The roles one credit line states. Qualifiers that Discogs writes in brackets are dropped,
    /// such as "Fortepiano [Anton Walter, 1795]". The instrument is the credit. Where the instrument came
    /// from is a note about it.</summary>
    private static IEnumerable<string> Stated(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            yield break;

        var text = new System.Text.StringBuilder();
        int depth = 0;
        foreach (var c in role)
        {
            if (c == '[') depth++;
            else if (c == ']') depth = Math.Max(0, depth - 1);
            else if (depth == 0) text.Append(c);
        }

        foreach (var part in text.ToString().Split(','))
            if (part.Trim() is { Length: > 0 } trimmed)
                yield return trimmed;
    }

    // ── which tag a role belongs in ─────────────────────────────────────────────────────────

    private enum Kind { Dropped, Composer, Lyricist, Arranger, Conductor, Ensemble, Soloist }

    /// <summary>
    /// Where a credit goes.
    ///
    /// <para>Named roles are matched first. Then the production roles are dropped. Whatever is left is a
    /// performer, because there is no list of instruments worth writing. A classical record credits
    /// fortepiano, viola da gamba, natural horn, countertenor, theorbo and basset clarinet. A rock record
    /// credits slide guitar and handclaps. Both are people who played on the record, which is what the
    /// soloists tag holds.</para>
    /// </summary>
    private static Kind Bucket(string role)
    {
        var r = role.ToLowerInvariant().Trim();

        if (Any(r, "composed by", "composer", "music by", "written-by", "written by", "songwriter"))
            return Kind.Composer;

        if (Any(r, "lyrics", "lyricist", "words by", "libretto", "text by"))
            return Kind.Lyricist;

        if (Any(r, "arranged", "arrangement", "arranger", "orchestrated", "orchestration",
                   "adapted", "transcribed", "realized"))
            return Kind.Arranger;

        // Checked before the ensembles. A chorus master is a person who conducts, not a choir.
        if (Any(r, "conductor", "conducted by", "chorus master", "choir master", "chorus director"))
            return Kind.Conductor;

        if (Any(r, "orchestra", "choir", "chorus", "ensemble", "quartet", "quintet", "trio",
                   "sextet", "septet", "octet", "consort", "band"))
            return Kind.Ensemble;

        // Everyone who made the object rather than the music.
        if (Any(r, "produc", "engineer", "master", "mix", "record", "edit", "design", "artwork",
                   "art direction", "photo", "illustration", "liner notes", "notes", "sleeve",
                   "cover", "layout", "typography", "coordinat", "management", "a&r", "supervis",
                   "compil", "research", "translat", "restoration", "transfer", "technician",
                   "lacquer", "licen", "publish", "legal", "programmed", "sequenc", "concept",
                   "presenter", "curat", "consultant"))
            return Kind.Dropped;

        return Kind.Soloist;
    }

    private static bool Any(string role, params string[] words) =>
        words.Any(w => role.Contains(w, StringComparison.Ordinal));

    /// <summary>Puts a set of credits into the tags they belong in, in the order they were credited. A
    /// person credited with two instruments is named once, such as "Bart van Oort (fortepiano,
    /// clavichord)". This is how a sleeve prints it, and how it reads back.</summary>
    private static Roles Sort(IEnumerable<Entry> entries)
    {
        List<string> composers = [], lyricists = [], arrangers = [], conductors = [], ensembles = [];
        var soloists = new List<(string Name, List<string> Played)>();

        foreach (var entry in entries)
        {
            switch (Bucket(entry.Role))
            {
                case Kind.Composer: Add(composers, entry.Name); break;
                case Kind.Lyricist: Add(lyricists, entry.Name); break;
                case Kind.Arranger: Add(arrangers, entry.Name); break;
                case Kind.Conductor: Add(conductors, entry.Name); break;
                case Kind.Ensemble: Add(ensembles, entry.Name); break;

                case Kind.Soloist:
                    var played = soloists.FirstOrDefault(s => s.Name == entry.Name).Played;
                    if (played is null)
                        soloists.Add((entry.Name, played = []));
                    var instrument = entry.Role.Trim().ToLowerInvariant();
                    if (!played.Contains(instrument))
                        played.Add(instrument);
                    break;
            }
        }

        return new Roles(
            Join(composers),
            Join(lyricists),
            Join(arrangers),
            Join(conductors),
            Join(ensembles),
            Join(soloists.Select(s => $"{s.Name} ({string.Join(", ", s.Played)})")));

        static void Add(List<string> names, string name)
        {
            if (!names.Contains(name))
                names.Add(name);
        }
    }

    private static string? Join(IEnumerable<string> names)
        => string.Join(", ", names) is { Length: > 0 } joined ? joined : null;

    // ── which tracks a credit covers ────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a credit's track range reaches the track at <paramref name="index"/>.
    ///
    /// <para>Discogs writes the range in the listing's own positions, such as "12-1 to 12-7, 14-5". So it
    /// is resolved by finding those positions, not by parsing them. That means "A1 to A4" and "CD2-01 to
    /// CD2-09" work without extra code. A side named as a whole, such as "B", matches every position that
    /// starts with it. A range that names a position the listing does not have is ignored, not guessed
    /// at.</para>
    /// </summary>
    private static bool Covers(string? tracks, int index, IReadOnlyList<string> positions)
    {
        if (string.IsNullOrWhiteSpace(tracks))
            return true;   // the whole record, which is what most credits mean

        foreach (var segment in tracks.Split(','))
        {
            var parts = segment.Split(" to ", StringSplitOptions.TrimEntries);
            var (first, _) = Span(positions, parts[0]);
            var (_, last) = Span(positions, parts[^1]);

            if (first >= 0 && last >= first && index >= first && index <= last)
                return true;
        }

        return false;
    }

    /// <summary>Where a position named in a range sits in the listing, as a span. That is the one track it
    /// names, or every track of a side or disc it names as a whole. (-1, -1) when it names none.</summary>
    private static (int First, int Last) Span(IReadOnlyList<string> positions, string token)
    {
        token = token.Trim();
        if (token.Length == 0)
            return (-1, -1);

        int first = -1, last = -1;
        for (var i = 0; i < positions.Count; i++)
        {
            if (!string.Equals(positions[i], token, StringComparison.OrdinalIgnoreCase))
                continue;
            return (i, i);
        }

        for (var i = 0; i < positions.Count; i++)
        {
            if (!positions[i].StartsWith(token, StringComparison.OrdinalIgnoreCase))
                continue;
            if (first < 0)
                first = i;
            last = i;
        }

        return (first, last);
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
           value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
