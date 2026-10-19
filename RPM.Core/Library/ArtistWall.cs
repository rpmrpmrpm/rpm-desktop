using RPM.Fingerprint.Catalog;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Library;

/// <summary>One album folder's credits as the mirror holds them: the record's own artist, one of its
/// tracks' artists, and how many tracks that pairing covers.</summary>
/// <param name="Folder">The library-relative album folder.</param>
/// <param name="AlbumArtist">What the record is filed under, or null where nothing is tagged.</param>
/// <param name="Artist">What the track is credited to, or null.</param>
/// <param name="Tracks">How many of the folder's tracks carry this pairing.</param>
public readonly record struct ArtistCreditRow(string Folder, string? AlbumArtist, string? Artist, int Tracks);

/// <summary>One person or band on the artists wall: their name, the records the collection files under
/// them, and the records they only turn up on.
/// </summary>
/// <param name="Name">The spelling to show. It is the one that the most rows carry, where the collection
/// spells the act more than one way.</param>
/// <param name="Albums">Album folders filed under this artist ALONE, in folder order.</param>
/// <param name="Together">Album folders filed under a credit that names this artist and somebody else. That
/// is a record the two of them made, not a record of theirs with a guest on it. It is kept apart from
/// <paramref name="Albums"/> because it is a different fact about the artist, and the page says so. A joint
/// record is on both artists' pages, and it is not solely either one's work.</param>
/// <param name="Appearances">Album folders where they are credited on a track, but the record is somebody
/// else's. This is what puts one album in two artists' listings.</param>
/// <param name="Tracks">How many tracks name them, anywhere.</param>
public sealed record ArtistWallItem(
    string Name,
    IReadOnlyList<string> Albums,
    IReadOnlyList<string> Together,
    IReadOnlyList<string> Appearances,
    int Tracks)
{
    /// <summary>The records that are theirs, alone or shared. This is what a surface should ask for when it
    /// wants "one of their own records". An artist whose only records are collaborations has none of the
    /// first kind, and is still not a stranger to the collection.</summary>
    public IEnumerable<string> Own => Albums.Concat(Together);

    /// <summary>Every folder they are on at all. Their own records come first.</summary>
    public IEnumerable<string> Folders => Own.Concat(Appearances);

    /// <summary>Which letter of the index rail they sit under.</summary>
    public char Initial => ArtistWall.Initial(Name);
}

/// <summary>The collection read by WHO made it: one entry per act, not per credit.
///
/// <para>The main difficulty of the wall is in one line of the tags. A track's artist field is a credit,
/// not a name. "Ellie Goulding" and "Calvin Harris feat. Ellie Goulding" are two strings that name one
/// person. A wall built on the strings would show her twice, with a different photo each time. So every
/// credit is split by <see cref="ArtistCredit.Split"/> into the acts it names. The album then appears in
/// the listing of each of them. One record, two boxes. That is what a guest appearance is.</para>
///
/// <para><b>Except where the credit is itself a band.</b> The separators that mean "and also" also appear
/// in "Earth, Wind &amp; Fire" and "AC/DC". Split blindly, those would become three artists and two. The
/// collection decides which is which. A credit that the library files a RECORD under is a name. Somebody's
/// tagger wrote it into the album-artist field of an album. "Queen &amp; David Bowie" is nobody's album
/// artist, so it is split. "Simon &amp; Garfunkel" is, so it stands. The rule needs no list of known bands.
/// It gets better as the collection grows, which is the only kind of rule that works on a real
/// library.</para>
///
/// <para><b>An act with nothing of their own is not on the wall.</b> A guest verse is a credit, not a
/// discography. A collection of thirty records holds two hundred names that each appear once. A wall built
/// on every one of them would mostly show people whose page holds a single track of somebody else's. So a
/// box is for an act that the collection files a RECORD under. That is either their own record or one they
/// made with somebody. Everyone else is still named, split and matched. They are still listed on the pages
/// of the artists they appear beside. See <see cref="ArtistWallItem.Own"/>.</para>
///
/// <para><b>And the compilations are an act.</b> "Various Artists" names nobody. Without special handling,
/// the records filed under it would have no page. Their tracks would be spread across the boxes of whoever
/// was on them, and the record itself would be on no page at all. It gets one box, under one spelling
/// however many the collection holds. The compilations are its records.</para>
///
/// <para>It is pure and does not use the database. The caller passes in the credit rows, and this returns
/// the wall.</para>
/// </summary>
public static class ArtistWall
{
    /// <summary>The one name that every spelling of the compilation marker is shown under. It is the app's
    /// own name, so a shelf that the album views call "Various Artists" is called that here too.</summary>
    public const string Compilations = Analysis.AlbumResolver.VariousArtists;

    /// <summary>The wall, in name order.</summary>
    /// <param name="rows">Every (folder, album artist, track artist) the mirror holds, with counts.</param>
    public static IReadOnlyList<ArtistWallItem> Build(IEnumerable<ArtistCreditRow> rows)
    {
        var credits = rows as IReadOnlyList<ArtistCreditRow> ?? rows.ToList();

        // What the collection files records under, squashed for comparison. A credit in here is a name,
        // however many separators it has in it. See the class summary. The exception is when the pass below
        // finds that it is two acts working together. The map keeps the spelling as filed, because deciding
        // that means splitting the credit, and a squashed name cannot be split back into people. So a run
        // of split parts that turns out to spell one of these is shown with the collection's own spelling,
        // not the credit's.
        var bands = new Dictionary<string, string>(StringComparer.Ordinal);
        var filed = new List<string>();
        foreach (var row in credits)
            if (row.AlbumArtist is { Length: > 0 } credit && !ArtistCredit.IsVariousArtists(credit)
                && bands.TryAdd(NameMatch.Squash(credit), credit.Trim()))
                filed.Add(credit.Trim());

        var joint = Collaborations(credits, bands, filed);

        var acts = new Dictionary<string, Act>(StringComparer.Ordinal);

        for (var i = 0; i < credits.Count; i++)
        {
            var row = credits[i];

            // The record's own name first, so that whoever it is filed under leads their own album, even
            // when no track of it credits them by name. For example, a compilation of one artist's singles.
            //
            // More than one name from ONE album-artist credit means the pair of them made the record
            // together (see Collaborations). Neither made it alone. Each of them is told so. The page shows
            // those records apart, because "their albums" and "the albums they made with somebody" are two
            // different questions about an artist.
            var leads = Names(row.AlbumArtist, bands, joint);
            foreach (var name in leads)
                Act.For(acts, name).Lead(row.Folder, i, row.Tracks, shared: leads.Count > 1);

            foreach (var name in Names(row.Artist, bands, joint))
                Act.For(acts, name).Guest(row.Folder, i, row.Tracks);
        }

        // In the rail's order, which is not quite name order. The hash bucket is the LAST letter of the
        // rail. It has to be the last part of the wall, or reaching for it would scroll upwards. Every
        // culture sorts digits and symbols in front of letters. Left to a plain sort, the numbers would be
        // at the top, and the rail would be wrong about where they are.
        return acts.Values
            .Select(a => a.Settle())
            // A record of their own is what a box is FOR. See the class summary. This is filtered here, not
            // while the credits are read. A guest credit is still how the collection learns a name. The
            // collaboration rule reads every act it has heard of. An act dropped too early would take a
            // joint record's other half down with it.
            .Where(a => a.Albums.Count > 0 || a.Together.Count > 0)
            .OrderBy(a => a.Initial == '#' ? 1 : 0)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The acts one credit names. A credit that the collection files a record under is one act,
    /// whatever is written in it. Anything else is split. A blank credit names nobody.
    ///
    /// <para>The compilation marker names ONE act, always the same one. "Various", "Various Artists" and
    /// every other spelling of it are the same shelf. Folding them here is what makes the compilations one
    /// box rather than four. It is never split, and it is never a band. It is not a name at all. It is what
    /// a record says when it has no single artist.</para>
    /// </summary>
    private static IReadOnlyList<string> Names(
        string? credit, IReadOnlyDictionary<string, string> bands,
        IReadOnlyDictionary<string, IReadOnlyList<string>> joint)
    {
        if (string.IsNullOrWhiteSpace(credit))
            return [];

        if (ArtistCredit.IsVariousArtists(credit))
            return [Compilations];

        var whole = credit.Trim();
        var key = NameMatch.Squash(whole);

        // A record two acts made together comes first. It is filed under a credit that names both of them.
        // The rule below would call that credit one band and give it a box of its own. That would leave the
        // record missing from both their pages, and add a third box for a name that is not an act.
        return joint.TryGetValue(key, out var together) ? together
             : bands.ContainsKey(key) ? [whole]
             : Rejoined(ArtistCredit.SplitGuests(whole), bands);
    }

    /// <summary>A split credit, with any run of its parts that spells out a name the collection files
    /// records under, put back together.
    ///
    /// <para>The split knows where a separator is. It does not know which separators are inside a name. The
    /// rule in <c>Names</c> protects a name only when it is the WHOLE credit. "A$AP Rocky; Tyler,
    /// The Creator; Jozzy" is not the whole credit for anybody. So it is cut into four, and two of those
    /// four are one act that the collection files eighty-five tracks under. Only a run that squashes to a
    /// credit in <paramref name="bands"/> is put back together. So this can rejoin pieces into an act the
    /// wall already has a box for. It can never invent one.</para>
    ///
    /// <para><b>This also keeps the collaboration test honest.</b> Without it, Tyler would be put into two
    /// boxes, not just fail to be merged. <see cref="Collaborations"/> asks whether every part of a filed
    /// credit is somebody the collection knows alone. The names it reads come from splitting track credits.
    /// "Tyler" and "The Creator" were in that set because the guest credits above were cut there. So
    /// "Tyler, The Creator" appeared to be two people, and that beat the album-artist rule that would have
    /// kept him whole.</para>
    ///
    /// <para><b>Never across a "feat".</b> The credit is taken one guest group at a time. That is the one
    /// cut the credit states explicitly. "Kanye West feat. Kid Cudi" names two acts. A collection that also
    /// files a record under "Kanye West &amp; Kid Cudi" would otherwise read the guest spot as the duo, and
    /// it would never learn Kid Cudi's name on its own. A comma claims nothing of the kind. That is why a
    /// comma is the one cut that can be undone. See <see cref="ArtistCredit.SplitGuests"/>.</para>
    ///
    /// <para>The longest run wins. The name is taken as the collection files it, not as this credit happens
    /// to spell it.</para>
    /// </summary>
    private static IReadOnlyList<string> Rejoined(
        IReadOnlyList<IReadOnlyList<string>> groups, IReadOnlyDictionary<string, string> bands)
    {
        var kept = new List<string>(4);

        foreach (var parts in groups)
            for (var i = 0; i < parts.Count; i++)
            {
                // Squashes concatenate the way the names do. The squash drops every separator, so a run
                // never has to be spelled out again to be recognised.
                var run = NameMatch.Squash(parts[i]);
                var joined = run;
                var last = i;
                for (var j = i + 1; j < parts.Count; j++)
                {
                    joined += NameMatch.Squash(parts[j]);
                    if (bands.ContainsKey(joined))
                    {
                        run = joined;
                        last = j;
                    }
                }

                if (last == i)
                {
                    kept.Add(parts[i]);
                    continue;
                }

                // A rejoin can bring back a name that the credit already names elsewhere. The split's own
                // de-duplication could not see that while the parts were still in pieces.
                if (!kept.Any(had => NameMatch.Squash(had) == run))
                    kept.Add(bands[run]);
                i = last;
            }

        return kept;
    }

    /// <summary>Which of the credits the collection files records under name two acts working together,
    /// rather than one act with a separator in its name. It also gives who each of those names are.
    ///
    /// <para><b>The problem.</b> "Simon &amp; Garfunkel" and "Kanye West &amp; Ty Dolla $ign" have the same
    /// shape. The album-artist rule cannot tell them apart. Both are what somebody's tagger wrote in an
    /// album-artist field, so both stand as one name. For the first, that is right. For the second, it puts
    /// the record on neither artist's page. It also adds a box to the wall for a partnership. So a wall of
    /// a rapper's work becomes a wall of the pairings he has been half of.</para>
    ///
    /// <para><b>The collection decides, again.</b> A collaboration is a credit where every part is somebody
    /// the collection ALREADY knows alone. Kanye West has records of his own, and Ty Dolla $ign is credited
    /// on somebody's track. So that credit names two acts who were on this wall before it was read. "Simon"
    /// and "Garfunkel" are nobody in this collection, and "Earth", "Wind" and "Fire" are nobody anywhere.
    /// So those credits stand. Like the album-artist rule it refines, this needs no list of known duos, and
    /// it gets better as the collection grows.</para>
    ///
    /// <para><b>EVERY part, not most.</b> "Nick Cave &amp; The Bad Seeds" is the case that decides it. A
    /// collection that holds his solo records knows "Nick Cave" alone. A rule that split on one known part
    /// would break the band up. "The Bad Seeds" is nobody's own credit, so the band stands. The cost of
    /// this strict rule is a collaboration whose other half the collection has nothing else by. Those keep
    /// a joint box. That is the honest answer. The collection does not know that name as an act, and
    /// splitting would make a box that nothing else on the wall agrees with.</para>
    ///
    /// <para><b>And the joining words count here.</b> "Kanye West with Common" has no separator that an
    /// ordinary split would find. See <see cref="ArtistCredit.SplitWide"/>. It is safe to use here only
    /// because of the test above. A band called "Sleeping With Sirens" is not cut up, because neither
    /// "Sleeping" nor "Sirens" is an act that this collection has ever heard of.</para>
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> Collaborations(
        IReadOnlyList<ArtistCreditRow> credits, IReadOnlyDictionary<string, string> bands,
        List<string> filed)
    {
        // Everybody the wall would hold under the album-artist rule alone. A guest on one track is in here,
        // and that is the point. Most of an artist's collaborators appear nowhere in a collection except as
        // a featured credit on somebody else's song.
        var known = new HashSet<string>(StringComparer.Ordinal);
        var none = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var row in credits)
        {
            foreach (var name in Names(row.AlbumArtist, bands, none))
                known.Add(NameMatch.Squash(name));
            foreach (var name in Names(row.Artist, bands, none))
                known.Add(NameMatch.Squash(name));
        }

        var joint = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var credit in filed)
        {
            var parts = ArtistCredit.SplitWide(credit);
            if (parts.Count > 1 && parts.All(part => known.Contains(NameMatch.Squash(part))))
                joint[NameMatch.Squash(credit)] = parts;
        }
        return joint;
    }

    /// <summary>Whether one credit as typed names this act. The artist page uses this to pick their own
    /// songs out of somebody else's record.
    ///
    /// <para>The whole credit is checked first. So a band whose name has a separator in it matches itself
    /// before it is cut up. Then each act the credit names is checked. Names are compared the way every
    /// other name in the app is. So case, punctuation, and an ampersand written as "and" are not
    /// differences.</para>
    /// </summary>
    public static bool Names(string? credit, string? artist)
    {
        if (string.IsNullOrWhiteSpace(credit) || string.IsNullOrWhiteSpace(artist))
            return false;

        // The wide reading is used, because a record two acts made is credited "A with B" as often as "A
        // & B". This is the question an artist's page asks when it picks their own songs off such a
        // record. It is safe here in a way it is not when building the wall. This method asks whether one
        // credit names an artist the caller already has. So a name cut in the wrong place matches nobody,
        // and costs nothing. The wall, by contrast, would grow a box for it.
        return NameMatch.Same(credit, artist)
               || ArtistCredit.SplitWide(credit).Any(name => NameMatch.Same(name, artist));
    }

    /// <summary>The letter this name sits under on the index rail: its first letter, or '#' for a name
    /// that starts with a digit, a symbol, or a script the rail has no letter for.
    ///
    /// <para>Accents are folded. So "Sigur Rós" and "Ólafur Arnalds" sit under S and O, not in the '#'
    /// bucket with the numbers. A rail that files half of Iceland under '#' is not an index.</para>
    /// </summary>
    public static char Initial(string? name)
    {
        foreach (var ch in (name ?? "").Trim())
        {
            if (!char.IsLetterOrDigit(ch))
                continue;

            var folded = Fold(char.ToUpperInvariant(ch));
            return folded is >= 'A' and <= 'Z' ? folded : '#';
        }
        return '#';
    }

    /// <summary>Whether this artist matches what has been typed in the wall's search field.
    ///
    /// <para>It is a substring match, but on a folded form of both the name and the query. Case, accents,
    /// spacing and punctuation are dropped from both. So "sigur ros" finds Sigur Rós, "acdc" finds AC/DC,
    /// and "guns n roses" finds Guns N' Roses. A plain substring match misses all three. Those are also
    /// what people type when they cannot remember how a band punctuates its name.</para>
    ///
    /// <para>A query that folds to nothing matches everybody, not nobody. This happens with a space, a
    /// bracket, or a stray apostrophe typed part-way through. A search with one character in it must not
    /// empty the wall while the second character is on its way.</para>
    /// </summary>
    public static bool Matches(string? name, string? query)
    {
        var wanted = SearchKey(query);
        return wanted.Length == 0 || SearchKey(name).Contains(wanted, StringComparison.Ordinal);
    }

    /// <summary>A name as the search compares it: lower case, accents off, and everything that is not a
    /// letter or a digit removed. It is public, so a caller that filters a long list can fold the query
    /// once, not once per name.</summary>
    public static string SearchKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var folded = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.Normalize(System.Text.NormalizationForm.FormD))
            if (char.IsLetterOrDigit(ch))
                folded.Append(char.ToLowerInvariant(ch));

        return folded.ToString();
    }

    /// <summary>A letter with its accent removed, when removing it leaves a letter of the rail's own
    /// alphabet. This uses decomposition, not a table. Every accented Latin letter splits into its base
    /// letter and a combining mark. The base letter is what the rail files it under.
    /// </summary>
    private static char Fold(char ch)
    {
        if (ch is >= 'A' and <= 'Z')
            return ch;

        foreach (var part in ch.ToString().Normalize(System.Text.NormalizationForm.FormD))
            if (part is >= 'A' and <= 'Z')
                return part;

        return ch;
    }

    /// <summary>The alphabet the rail is drawn from: A to Z, then the bucket for everything that does not
    /// start with one. It is shared, so the rail and the wall cannot disagree about what a letter
    /// is.</summary>
    public static readonly IReadOnlyList<char> Alphabet =
        [.. Enumerable.Range('A', 26).Select(c => (char)c), '#'];

    /// <summary>One act while the rows are still being read. It records every spelling seen, with its
    /// weight. So the wall can show the spelling that most rows carry. It also keeps the folders split by
    /// whether the record is theirs.
    /// </summary>
    private sealed class Act
    {
        private readonly Dictionary<string, int> _spellings = new(StringComparer.Ordinal);
        private readonly List<string> _albums = [];
        private readonly List<string> _together = [];
        private readonly List<string> _appearances = [];
        private readonly HashSet<string> _leads = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _guests = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _counted = [];
        private int _tracks;

        public static Act For(Dictionary<string, Act> acts, string name)
        {
            var key = NameMatch.Squash(name);
            if (!acts.TryGetValue(key, out var act))
                acts[key] = act = new Act();
            act._spellings[name] = act._spellings.GetValueOrDefault(name) + 1;
            return act;
        }

        /// <summary>This record is theirs.</summary>
        /// <param name="shared">Somebody else is on the credit the record is filed under. The two of them
        /// made it. The caller states this, and does not work it out here. What shows it is the credit as a
        /// whole, and this method only sees one name from it.</param>
        public void Lead(string folder, int row, int tracks, bool shared)
        {
            Count(row, tracks);
            if (!_leads.Add(folder))
                return;

            // Filed under them after all. The folder came in as an appearance, because a guest credit of
            // theirs was read first. It is their own record.
            if (_guests.Remove(folder))
                _appearances.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            (shared ? _together : _albums).Add(folder);
        }

        /// <summary>They are on this record. The record may or may not be theirs.</summary>
        public void Guest(string folder, int row, int tracks)
        {
            Count(row, tracks);
            if (!_leads.Contains(folder) && _guests.Add(folder))
                _appearances.Add(folder);
        }

        /// <summary>Counts one row's tracks, once. A row names an act twice whenever the record is filed
        /// under the same name its tracks are credited to. That is true of most records. Counted twice, an
        /// ordinary album would report double the songs it holds.</summary>
        private void Count(int row, int tracks)
        {
            if (_counted.Add(row))
                _tracks += tracks;
        }

        public ArtistWallItem Settle()
        {
            // The spelling on the most rows wins. A tie goes to the alphabetically first spelling. So two
            // rebuilds over one collection never disagree about how somebody is written.
            var name = _spellings
                .OrderByDescending(s => s.Value)
                .ThenBy(s => s.Key, StringComparer.Ordinal)
                .First().Key;

            _albums.Sort(StringComparer.OrdinalIgnoreCase);
            _together.Sort(StringComparer.OrdinalIgnoreCase);
            _appearances.Sort(StringComparer.OrdinalIgnoreCase);
            return new ArtistWallItem(name, _albums, _together, _appearances, _tracks);
        }
    }
}
