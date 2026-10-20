namespace RPM.Core.Metadata;

/// <summary>
/// The genres this app files records under. It is one short, fixed list. It is offered wherever a genre is
/// typed, and it is preferred over the names that a catalogue uses for the same music.
///
/// <para>A shelf is only useful if its labels repeat. Without a fixed list, one record might come back as "Hip
/// Hop", the next as "Rap", and the next as "Pop Rap". The library would then have three shelves for one kind of
/// music. So a lookup's answer is translated into this list where possible. If nothing here fits, the catalogue's
/// own name is kept. A wrong shelf is worse than an unusual one.</para>
///
/// <para><b>A shelf is half the answer.</b> The corner of a shelf that a record comes from is a
/// <see cref="Subgenres">style</see>. The two are read together as a <see cref="GenreChoice"/>.
/// <see cref="Read"/> is the way in. <see cref="Match"/> remains for callers that only need to know which
/// shelf a record belongs on.</para>
///
/// <para>This is not a validator. A file may carry any genre at all, typed by hand or inherited from whoever
/// tagged it first. This list is what the app suggests and what it picks. It is nothing more.</para>
/// </summary>
public static class Genres
{
    /// <summary>The answer for a record that matches none of the others. It is the last entry in the list. It has
    /// a name of its own because more than one place must say "not this one". It is skipped when the lookup keys
    /// are built. The genres wall drops it in favour of its own word for the same music.
    ///
    /// <para>The name is longer than the list's own word for it. The list says "Other", and that is deliberate.
    /// <see cref="Library.GenreWall.Unclassified"/> is the literal "Other", and it means no shelf at all. This is
    /// a shelf that somebody chose. Two different things cannot share one string.</para></summary>
    public const string Unknown = "Other / Unknown";

    /// <summary>The list, in the order it is offered. It is generated from <c>scripts/subgenres.json</c>, together
    /// with the styles that hang off each shelf. So a shelf cannot exist in one table and be missing from the
    /// other. <see cref="Unknown"/> is last, because it is the answer for a record that matches no other
    /// shelf.</summary>
    public static readonly IReadOnlyList<string> All = Subgenres.Shelves;

    /// <summary>
    /// Other names the catalogues use for these shelves, beyond the words already in the labels. This also
    /// includes older names that some shelves had.
    ///
    /// <para>Dance styles such as techno, deep house and drum &amp; bass are not listed here. Each one is a style,
    /// with its shelf named in the style table, and <see cref="Read"/> gets a record's shelf from its style. Nothing
    /// has to be written down twice. What is left here is spellings and old names.</para>
    ///
    /// <para>Each half of a slashed label is matched on its own, so "Soul" and "Dance" need no entry.
    /// <see cref="Unknown"/> is left out on purpose. It is a shelf that a person chooses. A lookup must never move
    /// a record into it.</para>
    /// </summary>
    private static readonly (string Alias, string Genre)[] Aliases =
    [
        ("rocknroll", "Rock"), ("rockandroll", "Rock"),

        // What the catalogues say. Discogs files soul records under "Funk / Soul" and film music under
        // "Stage & Screen". MusicBrainz says "edm".
        ("rb", "R&B"), ("rnb", "R&B"), ("randb", "R&B"), ("rhythmandblues", "R&B"),
        ("funksoul", "Funk"), ("motown", "Soul"),
        ("electronica", "Electronic / Dance"), ("edm", "Electronic / Dance"),
        ("hiphoprap", "Hip-Hop / Rap"),
        ("worldmusic", "World"), ("ethnic", "World"),
        ("gospel", "Religious"), ("christian", "Religious"), ("spiritual", "Religious"),
        ("ost", "Soundtrack"), ("filmscore", "Soundtrack"), ("stagescreen", "Soundtrack"),
        ("spoken", "Spoken Word"), ("audiobook", "Spoken Word"),
        ("ska", "Reggae"),

        // Words that name a shelf whose label no longer contains them (for example "Game" for the two-word
        // label). "Video Game Soundtrack" is listed in full because a phrase is read last word first. Without
        // this entry it would land on the Soundtrack shelf.
        ("international", "World"), ("score", "Soundtrack"),
        ("game", "Video Game"), ("vgm", "Video Game"),
        ("videogamesoundtrack", "Video Game"), ("gamesoundtrack", "Video Game"),

        // The four shelves that the widened list dropped. They are still written into older files. They are read,
        // but never offered. The first word of a compound label says what the record is, so "R&B / Soul" is an
        // R&B record.
        ("rbsoul", "R&B"),
        ("worldinternational", "World"),
        ("gospelreligious", "Religious"),
        ("soundtrackscore", "Soundtrack"),
    ];

    /// <summary>One of the app's genres as it would be said in a sentence: "rock", "hip-hop", "electronic".
    ///
    /// <para>The shelf labels are not English sentences. They are built to sort and to be picked from a list. Some
    /// are slashed pairs, such as "Electronic / Dance" and "Hip-Hop / Rap". A sentence such as "the quiet end of
    /// your Electronic / Dance" reads like a filing system. The first half of a pair is the word people actually
    /// use for the music, so that is what is spoken.</para>
    ///
    /// <para>The result is lower case, because it appears in the middle of a sentence. The one exception is R&amp;B.
    /// It is an abbreviation, and "r&amp;b" would look like a typo.</para>
    ///
    /// <para>It is not translated. The reason is the same as for the playlist titles. These are the names of kinds
    /// of music. The app already shows them untranslated on the genres wall. A sentence that translated only some
    /// of its words would be worse than one that translated none.</para>
    /// </summary>
    public static string Spoken(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return "";

        var head = genre.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault(genre)
                        .Trim();

        return head.Equals("R&B", StringComparison.OrdinalIgnoreCase) ? "R&B" : head.ToLowerInvariant();
    }

    /// <summary>
    /// The genres that sit next to one another, as families: a head, and the kinds of it that the app files
    /// separately.
    ///
    /// <para><b>Why this exists.</b> <see cref="All"/> is a flat list, because it is a list to pick from, in a
    /// reading order. But its labels are not unrelated. Rap, Trap and Old School Hip Hop are three shelves for one
    /// kind of music. Someone standing on any one of them is one press from the other two. A flat list cannot show
    /// that, so a genre's page had nowhere to send a reader next.</para>
    ///
    /// <para><b>A family, not a tree.</b> A genre belongs to as many families as it honestly does. Disco is a kind
    /// of pop and a kind of R&amp;B. Club is dance music and pop music. Blues comes from both Folk and Jazz. A
    /// single parent would have to pick one and be wrong about the others. Nothing here assumes that a genre has
    /// only one home.</para>
    ///
    /// <para><b>The head comes first, and the order matters.</b> The head is the broader shelf. A page for one of
    /// the kinds lists the head before its siblings. That is the order a reader wants: up before across.</para>
    ///
    /// <para>A genre in no family, such as Reggae, Latin or Spoken Word, has no relations. Its page does not show
    /// the card. That is the honest answer. RPM does not file a kind of reggae separately, so there is nothing to
    /// list.</para>
    /// </summary>
    private static readonly string[][] Families =
    [
        ["Rock", "Indie", "Punk", "Metal", "Noise"],
        ["Pop", "Indie", "Disco", "Club"],
        ["R&B", "Soul", "Funk", "Disco"],
        ["Hip-Hop / Rap", "Rap", "Trap", "Old School Hip Hop"],
        ["Electronic / Dance", "House", "Techno", "Club"],
        ["Folk", "Country", "Blues"],
        ["Jazz", "Blues", "Avant-Garde"],
        ["Classical", "Neo-Classical", "Opera"],
        ["Soundtrack", "Video Game", "Musical"],
        ["Avant-Garde", "Noise"],
    ];

    /// <summary>
    /// The app's other genres that this one sits next to. Where this genre is a head, these are its kinds. Where
    /// it is one of the kinds, these are the head and its siblings.
    ///
    /// <para>Duplicates are removed. The order is the one a reader wants, not the order in <see cref="All"/>. Heads
    /// come first, because going up is a bigger step than going across. Then come the kinds, in the order the family
    /// lists them. A genre reached through two families keeps its first position.</para>
    ///
    /// <para>The list is empty for a genre in no family, for <see cref="Unknown"/>, and for a word that is not in
    /// <see cref="All"/> at all. Every caller must be able to draw nothing here. Most genres are in no family, and
    /// a page is complete without the card.</para>
    /// </summary>
    public static IReadOnlyList<string> Related(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return [];

        var related = new List<string>();

        // Heads first, across ALL the families this genre is in, and then the kinds. Not family by family.
        // Otherwise Disco, which is a kind of two things, would list Pop, Club, R&B, Soul and Funk. The second head
        // would be buried among the first family's siblings.
        Gather(heads: true);
        Gather(heads: false);
        return related;

        void Gather(bool heads)
        {
            foreach (var family in Families)
            {
                var at = Array.FindIndex(
                    family, name => name.Equals(genre, StringComparison.OrdinalIgnoreCase));
                if (at < 0)
                    continue;

                for (var i = 0; i < family.Length; i++)
                {
                    // The heads pass keeps only index 0, the head. The kinds pass keeps the others. The genre itself
                    // is always skipped. So when the genre is the head, nothing is added on the heads pass.
                    if (i == at || heads != (i == 0))
                        continue;
                    if (!related.Contains(family[i], StringComparer.OrdinalIgnoreCase))
                        related.Add(family[i]);
                }
            }
        }
    }

    /// <summary>Every spelling that leads to a genre: the label itself, each half of a slashed label, and the
    /// aliases. It is built once. This is asked once per lookup, and once per keystroke in a genre box.</summary>
    private static readonly Dictionary<string, string> ByKey = BuildKeys();

    private static Dictionary<string, string> BuildKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var genre in All)
        {
            if (genre == Unknown)
                continue;
            // "R&B" splits into "r" and "b". A one-letter key would match anything with an R in it. Only the words
            // are worth a key. "rb" itself is in the aliases.
            foreach (var key in GenreText.KeysOf(genre, GenreText.Separators).Where(k => k.Length >= 3))
                keys.TryAdd(key, genre);
        }
        foreach (var (alias, genre) in Aliases)
            keys.TryAdd(alias, genre);
        return keys;
    }

    /// <summary>
    /// The shelf and the corner of it that this text names.
    ///
    /// <para><b>One walk down the tag, asking both tables about each word.</b> The walk does not check styles
    /// first and then shelves, or the other way round. Checking shelves first would be wrong. The shelf table
    /// answers "Pop Rap" on its second word, and would file a rap record under Pop before the style table was
    /// reached. Checking styles first would read the "house" in "Housemartins" as a style. So each key is offered
    /// to the styles first, and then to the shelves. The first answer of either kind wins.</para>
    ///
    /// <para>A style carries its own shelf. Finding a style answers both halves at once. This is why a file tagged
    /// only "Deep House" becomes a House record with a style. Nobody has written "deep house means House" down
    /// anywhere.</para>
    ///
    /// <para><see cref="GenreChoice.None"/> is returned when the text names neither. That is an ordinary answer. The
    /// tag is then kept exactly as it was written.</para>
    /// </summary>
    public static GenreChoice Read(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GenreChoice.None;

        foreach (var key in GenreText.KeysOf(value))
        {
            if (Subgenres.ByFoldedKey(key) is { } style)
                return new GenreChoice(style.Genre, style.Name);
            if (ByKey.TryGetValue(key, out var genre))
                return new GenreChoice(genre, null);
        }

        return GenreChoice.None;
    }

    /// <summary>The shelf that this text means, or null when it means none of them. The whole label is matched
    /// first. So "Folk, World, &amp; Country" is Folk, not World, because its first word is what the record
    /// actually is.
    ///
    /// <para>This is the shelf half of <see cref="Read"/>, and it reads styles the same way. A file tagged "Deep
    /// House" is a House record here too. A caller that also wants the style must use <see cref="Read"/>. This
    /// method throws the style away. That is the reason it has a different name.</para></summary>
    public static string? Match(string? value)
        => Read(value) is { IsKnown: true } choice ? choice.Genre : null;

    /// <summary>
    /// The genre to tag a record with, given what a catalogue said about it. This is the first of its answers that
    /// is one of ours. If none is, it is the first answer the catalogue gave.
    /// </summary>
    /// <param name="genres">What the provider filed the record under, in its own order.</param>
    /// <param name="styles">The narrower labels, where the provider makes that distinction. These are read after
    /// the genres, and only for a match. A record whose genre is not recognised is better tagged with the
    /// catalogue's genre than with its style.</param>
    public static string? Pick(IReadOnlyList<string>? genres, IReadOnlyList<string>? styles = null)
    {
        genres ??= [];
        styles ??= [];

        foreach (var value in genres.Concat(styles))
            if (Match(value) is { } genre)
                return genre;

        return First(genres) ?? First(styles);

        static string? First(IReadOnlyList<string> values)
            => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
    }

    /// <summary>
    /// Everything a catalogue's answer says, after RPM's own lists have read it: the shelf, and every style in it
    /// that RPM has a name for.
    ///
    /// <para><see cref="Genre"/> is null, and <see cref="Styles"/> is empty, when the provider said nothing that
    /// RPM recognises. That is an answer. The record was asked about, and it was filed under nothing this app
    /// knows. It is not the same as never having asked.</para>
    /// </summary>
    /// <param name="Genre">One of <see cref="All"/>, or null.</param>
    /// <param name="Styles">Every style recognised, in the order the provider gave them, styles before genres, each
    /// appearing once.</param>
    public readonly record struct MappedRelease(string? Genre, IReadOnlyList<Subgenre> Styles)
    {
        public static readonly MappedRelease None = new(null, []);
    }

    /// <summary>
    /// A catalogue's whole answer, mapped onto RPM's lists and kept in full.
    ///
    /// <para><b>The list, where <see cref="PickPair"/> keeps a pair.</b> A record tagged for the user carries one
    /// genre and one style, because that is what a person sets and what a file's tags hold. The mood lists need
    /// more than that. They narrow on everything the catalogue said. So a list called Slow Burn can ask for the
    /// ballads, the acoustic takes and the folk rock all at once. Both the pair and the mood lists come from here.
    /// That way, the pair a user is offered and the list a playlist reads cannot disagree about what a provider
    /// said.</para>
    ///
    /// <para>Only recognised entries are kept. A style that RPM has no name for is dropped, not stored. The point of
    /// a controlled list is that everything in it belongs there. The provider's own words are kept beside the
    /// mapping. A wider list can then be run over them later, without asking the catalogue again.</para>
    /// </summary>
    /// <param name="genres">What the provider filed the record under, in its own order.</param>
    /// <param name="styles">The narrower labels. Discogs sends these as <c>styles</c>. MusicBrainz has only one list,
    /// and it is passed as both.</param>
    public static MappedRelease MapAll(IReadOnlyList<string>? genres, IReadOnlyList<string>? styles)
    {
        genres ??= [];
        styles ??= [];

        // The shelf comes from the genres alone. It is what the provider called the kind of music. A style never
        // overrules it. Discogs files plenty of rock records with an electronic style.
        string? shelf = null;
        foreach (var value in genres)
            if (Match(value) is { } genre)
            {
                shelf = genre;
                break;
            }

        // Styles before genres. A provider with one list puts its styles in it. A genre of "Deep House" is a style,
        // whichever field it arrived in. The check is ordinal on the NAME, not on the record. The same style reached
        // through two spellings is one style.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<Subgenre>();
        foreach (var value in styles.Concat(genres))
            if (Subgenres.Match(value) is { } style && seen.Add(style.Name))
                found.Add(style);

        // A provider that said only "Deep House" has told us the record is House. Refusing to hear it would leave
        // the record on no shelf at all.
        return new MappedRelease(shelf ?? found.FirstOrDefault()?.Genre, found);
    }

    /// <summary>
    /// The shelf and the style to tag a record with, given what a catalogue said about it.
    ///
    /// <para><b>This is the method that raises coverage, because nobody has to type anything.</b> Discogs sends a
    /// record's styles along with its genres, so every fetch gives a second source for the style. MusicBrainz's
    /// voted genres are often styles. Its most common answer for a dance record is "deep house". A style such as
    /// that is a corner of a shelf, not a kind of music on its own, so it must be read as one.</para>
    ///
    /// <para>The styles decide the style. They also decide the shelf, when the genres could not. A provider that
    /// says only "Deep House" has told us the record is House. Refusing to hear it would leave the record on no shelf
    /// at all. But a genre that the app recognises is never overruled by a style. Discogs files plenty of rock
    /// records with an electronic style on them.</para>
    /// </summary>
    /// <param name="genres">What the provider filed the record under, in its own order.</param>
    /// <param name="styles">The narrower labels. Discogs sends these as <c>styles</c>. MusicBrainz has only one list,
    /// and it is passed as both.</param>
    public static GenreChoice PickPair(IReadOnlyList<string>? genres, IReadOnlyList<string>? styles)
    {
        var mapped = MapAll(genres, styles);

        if (mapped.Genre is { } shelf)
            return new GenreChoice(shelf, mapped.Styles.FirstOrDefault()?.Name);

        // Nothing was recognised. Fall back to the shelf half's own answer. That keeps whatever the catalogue wrote,
        // rather than filing the record nowhere.
        return Pick(genres, styles) is { } written ? new GenreChoice(written, null) : GenreChoice.None;
    }

    /// <summary>Whether this is one of the shelves exactly as the list spells it, ignoring case. "Hip hop" is
    /// not: it LEADS to a shelf (see <see cref="Read"/>), but it is not one, and a page that groups records by
    /// their genre tag would give it a shelf of its own.</summary>
    public static bool IsListed(string? genre)
        => !string.IsNullOrWhiteSpace(genre)
           && All.Contains(genre.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The shelf as the list spells it, for a tag that is one of the shelves in another case: "rock"
    /// is "Rock". Null for anything that is not a shelf as written. See <see cref="IsListed"/>.</summary>
    public static string? Listed(string? genre)
        => string.IsNullOrWhiteSpace(genre)
            ? null
            : All.FirstOrDefault(g => g.Equals(genre.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The shelf and the style to file a record under, from a catalogue that gives ONE list of genres ranked
    /// by its voters. This is MusicBrainz: "electronic, trip hop, downtempo", most votes first.
    ///
    /// <para><b>Why not <see cref="PickPair"/>.</b> PickPair is built for Discogs, which keeps its genres and
    /// its styles apart, and it takes the first style it finds wherever that style hangs. Given one voted list,
    /// "rock, indie rock" would come back as Rock with the style Indie Rock, which the style table hangs off
    /// Indie. A pair whose style is not under its shelf is the one answer <see cref="GenreChoice"/> says can
    /// never happen, and the genre pages and the mood lists read the two together.</para>
    ///
    /// <para><b>The rule.</b> The shelf is the first entry, in vote order, that the lists recognise
    /// (<see cref="Read"/>). Most votes is what the record mostly is. If that entry is itself a style ("trip
    /// hop" voted above "electronic"), it answers both halves. Otherwise the style is the first entry in vote
    /// order that is a style UNDER that shelf: "electronic, trip hop" is Electronic / Dance and Trip Hop, and
    /// "rock, alternative rock" is Rock and Alternative Rock. Failing that, a style on one of the shelf's own
    /// kinds (see <see cref="Families"/>) moves the record down to that kind: "rock, indie rock" is Indie and
    /// Indie Rock, and "electronic, deep house" is House and Deep House. The voters said which corner of the
    /// broad word the record sits in, and the list files that corner on a shelf of its own. Nothing moves
    /// sideways or up, so "rock, synth-pop" stays Rock with no style.</para>
    ///
    /// <para><see cref="GenreChoice.None"/> when nothing in the list is ours. Unlike <see cref="PickPair"/>,
    /// this never hands back the catalogue's own word: a shelf the app does not have is a tag every genre page,
    /// mood list and radio skips, and a blank says plainly that nobody has filed the record yet. The words
    /// stay in <see cref="Providers.ReleaseDetail.Genres"/> for whoever wants them.</para>
    /// </summary>
    /// <param name="voted">The catalogue's genres, most votes first.</param>
    public static GenreChoice PickVoted(IReadOnlyList<string>? voted)
    {
        if (voted is not { Count: > 0 })
            return GenreChoice.None;

        var lead = GenreChoice.None;
        foreach (var value in voted)
            if (Read(value) is { IsKnown: true } read)
            {
                lead = read;
                break;
            }

        if (!lead.IsKnown || lead.HasSubgenre)
            return lead;

        if (StyleUnder(lead.Genre, voted) is { } style)
            return new GenreChoice(lead.Genre, style);

        var kinds = KindsOf(lead.Genre);
        if (kinds.Count > 0)
            foreach (var value in voted)
                if (Subgenres.Match(value) is { } narrower && kinds.Contains(narrower.Genre))
                    return new GenreChoice(narrower.Genre, narrower.Name);

        return lead;
    }

    /// <summary>
    /// The first of <paramref name="voted"/> that is a style under <paramref name="genre"/>, as the style table
    /// spells it, or null when none is.
    ///
    /// <para>This is for a record whose shelf is already settled, by a person or by the file, and whose style is
    /// still blank. A catalogue that files it somewhere else can still say which corner of THIS shelf it is: a
    /// record a person put on Rock, voted "pop, soft rock", is Soft Rock. A style under a different shelf is
    /// never taken, because a style only makes sense as a corner of its own shelf.</para>
    ///
    /// <para>The shelf's own styles are compared by name first, then the style table's reading of each entry.
    /// The name pass is what finds a style that hangs off two shelves, which the table's lookup leaves to the
    /// rest of the tag (see <see cref="Subgenres"/>).</para>
    /// </summary>
    public static string? StyleUnder(string? genre, IReadOnlyList<string>? voted)
    {
        if (string.IsNullOrWhiteSpace(genre) || voted is not { Count: > 0 })
            return null;

        var under = Subgenres.Under(genre);
        if (under.Count == 0)
            return null;

        foreach (var value in voted)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            var key = GenreText.Fold(value);
            if (under.FirstOrDefault(s => GenreText.Fold(s.Name) == key) is { } named)
                return named.Name;
            if (Subgenres.Match(value) is { } style && style.Genre == genre)
                return style.Name;
        }
        return null;
    }

    /// <summary>The shelves filed as kinds of this one, where it heads a family, and nothing otherwise. A kind is
    /// never asked for its siblings or its head here. Only a step down is a narrower answer.</summary>
    private static IReadOnlyList<string> KindsOf(string genre)
        => [.. Families.Where(f => f[0].Equals(genre, StringComparison.OrdinalIgnoreCase)).SelectMany(f => f.Skip(1))];
}
