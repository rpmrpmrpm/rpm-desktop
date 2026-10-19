using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>
/// Something the sentence named that the answer could not use, and why.
///
/// <para>The drop is reported, never hidden. A sentence that asks for two moods, or for a mood and a decade
/// that the list cannot be cut by, gets an answer that is less than what was asked. The surface must be
/// able to say so, for example: "Showing Chill, rock. The 1970s was not used." Silently throwing away part
/// of the sentence is the failure this type exists to prevent.</para>
/// </summary>
public enum MoodQueryDrop
{
    /// <summary>A mood word was there, but a negator came before it, so it is not used. It is never
    /// inverted. "not loud" does not mean calm. It means not loud, and there is no list for
    /// that.</summary>
    NegatedMood,

    /// <summary>A genre was named and negated. A generated list can be limited to one genre, but it cannot
    /// exclude one. So the genre is dropped rather than applied in reverse.</summary>
    NegatedGenre,

    /// <summary>A decade was named and negated, for the same reason as
    /// <see cref="NegatedGenre"/>.</summary>
    NegatedDecade,

    /// <summary>Two different moods were named, and one had to go. Which one stays is
    /// <see cref="MoodQuery.List"/>. The rule that picks it is in <see cref="MoodQuery.Read"/>.</summary>
    SecondMood,

    /// <summary>Two genres were named, and the later one is not used.</summary>
    SecondGenre,

    /// <summary>A genre and a decade were both named. A list is narrowed by one or the other, never by
    /// both. The genre is kept, because it says what the music is. A decade only says when it was made.
    /// See <see cref="SmartPlaylistId"/>, whose factories enforce this.</summary>
    DecadeLostToGenre,

    /// <summary>A year was named that is not one of <see cref="SmartPlaylists.Decades"/>. The shelf is
    /// broken out by six decades, and no list is offered outside them.</summary>
    DecadeNotOffered,

    /// <summary>This kind of list has no per-genre form, so the genre is not used. The kinds that do are
    /// <see cref="SmartPlaylists.BrokenOutByGenre"/> and
    /// <see cref="SmartPlaylists.EssentialForAGenre"/>.</summary>
    KindTakesNoGenre,

    /// <summary>This kind of list has no per-decade form, so the decade is not used. The kinds that do are
    /// listed in <see cref="SmartPlaylists.BrokenOutByEra"/>.</summary>
    KindTakesNoDecade,
}

/// <summary>
/// One typed sentence, read as a generated list.
///
/// <para>"Something mellow for a rainy Sunday" is a question this app can already answer. The app has a
/// mood cut for each genre and a decade breakout, and each one is a type. This type gets from typed
/// words to those types. It needs no model and no network.
/// Its only data is <see cref="MoodWords"/> and the genre matcher.</para>
///
/// <para><b>The list is the answer, and it can be nothing.</b> <see cref="List"/> is null when the
/// sentence names no kind. That is a normal result. "rock" is a genre, not a mood, and the right thing to
/// do with it is to open the rock page, not to invent a mood. <see cref="Genre"/> is still reported in
/// that case, so the caller can route it.</para>
///
/// <para><b>What was read and what was used are separate answers.</b> <see cref="Genre"/> and
/// <see cref="Decade"/> say what the sentence named. <see cref="List"/> says what could be built from it.
/// <see cref="Dropped"/> says where the two differ. A surface that shows only the first would promise a cut
/// that was not made.</para>
/// </summary>
/// <param name="List">The generated list the sentence asks for, or null where it asks for none.</param>
/// <param name="Genre">The genre the sentence named, one of <see cref="Genres.All"/>, or null. It may
/// differ from the genre on <see cref="List"/>. See <see cref="Dropped"/>.</param>
/// <param name="Decade">The decade the sentence named, or zero. It may differ from the decade on
/// <see cref="List"/>, for the same reason.</param>
/// <param name="Dropped">Everything named and not used, in the order the rules dropped it.</param>
public readonly record struct MoodQuery(
    SmartPlaylistId? List,
    string? Genre,
    int Decade,
    IReadOnlyList<MoodQueryDrop> Dropped)
{
    /// <summary>The sentence named nothing this app can build. It is handed out instead of a null, so
    /// callers never have to test for null.</summary>
    public static readonly MoodQuery Nothing = new(null, null, 0, []);

    /// <summary>Whether there is a list to open.</summary>
    public bool Understood => List is not null;

    /// <summary>One word of the sentence, folded, and whether a clause break came before it.</summary>
    /// <param name="AfterBreak">True where a comma, semicolon, full stop or the word "but" separated this
    /// word from the one before. Negation does not cross one. For example, "no metal, something calm"
    /// refuses the metal and keeps the calm.</param>
    private readonly record struct Word(string Folded, bool AfterBreak);

    /// <summary>One run of words that meant something, and what it meant. A span can mean a kind and a
    /// genre at once, which is the "classical" case.</summary>
    private sealed class Span
    {
        public int At;
        public int Words;
        public SmartPlaylistKind? Kind;
        public string? Genre;
        public int Decade;
        public bool Negated;
        public int Past => At + Words;
    }

    /// <summary>
    /// Read a sentence.
    ///
    /// <para><b>The order of the rules is the whole design, so the rules are written down here, not spread
    /// over the steps.</b></para>
    ///
    /// <para><b>1. Phrases are claimed longest first, left to right.</b> "Never played" is one phrase for
    /// <see cref="SmartPlaylistKind.NeverPlayed"/>. Claiming it whole stops the "never" from negating the
    /// "played" beside it. Every later rule reads only what this rule left over. So that is true by design,
    /// not by a special case.</para>
    ///
    /// <para><b>2. A negator applies to the first span after it, and does not cross a clause break.</b> A
    /// fixed window of words would be wrong. It would either miss "nothing that is too loud", or take in
    /// the "calm" in "no metal, something calm". A negated thing is dropped, and never inverted. There is
    /// no list for "not loud", and guessing one would mean the parser decides what somebody meant.</para>
    ///
    /// <para><b>3. A word that is both a kind and a genre is the kind only where nothing else supplies
    /// one.</b> "Classical" alone is the classical list. "Calm classical" is the calm cut of the
    /// classical shelf. So is "classical, something calm". One rule covers both orders.</para>
    ///
    /// <para><b>4. Among kinds, the longest phrase wins, then the earliest.</b> Length comes before
    /// position, so that a two-word phrase beats a one-word phrase inside it.</para>
    ///
    /// <para><b>5. A genre beats a decade.</b> A genre and a decade cannot both be carried, and the genre
    /// says what the music is. The loss is reported, never silent.</para>
    /// </summary>
    /// <param name="sentence">What the listener typed. Null, blank or unreadable gives
    /// <see cref="Nothing"/>.</param>
    public static MoodQuery Read(string? sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            return Nothing;

        var words = Split(sentence);
        if (words.Count == 0)
            return Nothing;

        var spans = Claim(words);
        Negate(words, spans);
        return Assemble(spans);
    }

    /// <summary>
    /// The sentence as words, with the clause breaks marked.
    ///
    /// <para>An apostrophe is not a break, and it does not end a word. "don't" is one word, and it folds
    /// to "dont", which is the spelling the negator table holds. Every other character that is not a
    /// letter or a digit ends the word. Commas, semicolons, full stops and similar marks also start a new
    /// clause.</para>
    /// </summary>
    private static List<Word> Split(string sentence)
    {
        List<Word> words = [];
        var word = new System.Text.StringBuilder();
        var breakPending = false;

        foreach (var c in sentence)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
                continue;
            }

            if (c is '\'' or '’')
                continue;

            Flush();
            if (c is ',' or ';' or '.' or ':' or '!' or '?' or '\n' or '/' or '(' or ')')
                breakPending = true;
        }

        Flush();
        return words;

        void Flush()
        {
            if (word.Length == 0)
                return;

            var folded = GenreText.Fold(word.ToString());
            word.Clear();
            if (folded.Length == 0)
                return;

            // "But" is a clause break here, because it is where a refusal stops applying. It is never
            // itself read as a word.
            if (folded == "but")
            {
                breakPending = true;
                return;
            }

            words.Add(new Word(folded, breakPending));
            breakPending = false;
        }
    }

    /// <summary>Every run of words that named something, claimed longest first and left to right so
    /// that no word is read twice.</summary>
    private static List<Span> Claim(List<Word> words)
    {
        List<Span> spans = [];

        for (var at = 0; at < words.Count;)
        {
            var longest = Math.Min(MoodWords.LongestPhrase, words.Count - at);
            var claimed = 0;

            for (var length = longest; length >= 1 && claimed == 0; length--)
            {
                // A phrase never spans a clause break: "chill, out of the way" is not "chill out".
                if (Enumerable.Range(at + 1, length - 1).Any(i => words[i].AfterBreak))
                    continue;

                var folded = string.Concat(Enumerable.Range(at, length).Select(i => words[i].Folded));

                var kind = MoodWords.Kind(folded);

                // The folded form is used, not the spaced form. This is the difference between a correct
                // parse and a bad one. Genres.Match splits its input on spaces and matches from the left.
                // So it answers "Metal" to the phrase "no metal", which would claim the negator as part
                // of the span and leave nothing to refuse with. A window whose spaces are already gone
                // can only match a whole genre. That is the only safe reading here.
                var genre = Genres.Match(folded);
                var decade = MoodWords.Decade(folded) is var named && named > 0 ? named : YearIn(folded);

                if (kind is null && genre is null && decade == 0)
                    continue;

                spans.Add(new Span
                {
                    At = at, Words = length, Kind = kind, Genre = genre, Decade = decade,
                });
                claimed = length;
            }

            at += claimed == 0 ? 1 : claimed;
        }

        return spans;
    }

    /// <summary>The decade a bare four-digit year falls in, or zero if the word is not a year. Whether
    /// that decade is offered is a separate question. It is asked in <see cref="Assemble"/>, so the
    /// answer can be reported and not silently dropped.</summary>
    private static int YearIn(string folded)
    {
        if (folded.Length != 4 || !folded.All(char.IsAsciiDigit))
            return 0;

        var year = int.Parse(folded);
        return year is >= 1900 and <= 2099 ? year - year % 10 : 0;
    }

    /// <summary>Marks the spans that a refusal reaches: the first span after each leftover negator. The
    /// mark stops at a clause break.</summary>
    private static void Negate(List<Word> words, List<Span> spans)
    {
        var claimed = new bool[words.Count];
        foreach (var span in spans)
            for (var i = span.At; i < span.Past; i++)
                claimed[i] = true;

        for (var at = 0; at < words.Count; at++)
        {
            if (claimed[at] || !MoodWords.Negates(words[at].Folded))
                continue;

            var next = spans.FirstOrDefault(s => s.At > at);
            if (next is null)
                continue;

            // Everything between the refusal and the thing refused has to be part of the same clause.
            if (Enumerable.Range(at + 1, next.At - at).Any(i => words[i].AfterBreak))
                continue;

            next.Negated = true;
        }
    }

    /// <summary>Picks the kind, the genre and the decade from what was claimed. Then it builds the one list
    /// that can carry them.</summary>
    private static MoodQuery Assemble(List<Span> spans)
    {
        List<MoodQueryDrop> dropped = [];

        foreach (var span in spans.Where(s => s.Negated))
        {
            if (span.Kind is not null)
                dropped.Add(MoodQueryDrop.NegatedMood);
            else if (span.Genre is not null)
                dropped.Add(MoodQueryDrop.NegatedGenre);
            else if (span.Decade > 0)
                dropped.Add(MoodQueryDrop.NegatedDecade);
        }

        var live = spans.Where(s => !s.Negated).ToList();

        // Rules 3 and 4. A span that is only a kind is preferred over one that is also a genre. So
        // "classical" becomes the shelf as soon as another word supplies the mood.
        var kinds = live.Where(s => s.Kind is not null).ToList();
        var chosen = Best(kinds.Where(s => s.Genre is null)) ?? Best(kinds);

        var genres = live.Where(s => s.Genre is not null && s != chosen).ToList();
        var genre = genres.FirstOrDefault()?.Genre;

        var eras = live.Where(s => s.Decade > 0 && s != chosen).ToList();
        var decade = eras.FirstOrDefault()?.Decade ?? 0;

        if (kinds.Any(s => s != chosen && s.Kind != chosen?.Kind && s.Genre is null))
            dropped.Add(MoodQueryDrop.SecondMood);
        if (genres.Skip(1).Any(s => !string.Equals(s.Genre, genre, StringComparison.Ordinal)))
            dropped.Add(MoodQueryDrop.SecondGenre);

        var kind = chosen?.Kind;
        var offered = decade > 0 && SmartPlaylists.Decades.Contains(decade);
        if (decade > 0 && !offered)
            dropped.Add(MoodQueryDrop.DecadeNotOffered);

        if (kind is null)
        {
            // There is no mood, but a decade is a list of its own: the songs of that decade that the
            // world plays most. A genre cannot go with it, because that list is not cut by genre.
            if (!offered)
                return new MoodQuery(null, genre, decade, dropped);

            if (genre is not null)
                dropped.Add(MoodQueryDrop.KindTakesNoGenre);

            return new MoodQuery(SmartPlaylistId.In(decade), genre, decade, dropped);
        }

        var takesGenre = SmartPlaylists.BrokenOutByGenre.Contains(kind.Value)
                      || SmartPlaylists.EssentialForAGenre.Contains(kind.Value);
        var takesEra = SmartPlaylists.BrokenOutByEra.Contains(kind.Value);

        var useGenre = genre is not null && takesGenre;
        var useEra = offered && takesEra;

        if (genre is not null && !takesGenre)
            dropped.Add(MoodQueryDrop.KindTakesNoGenre);
        if (offered && !takesEra)
            dropped.Add(MoodQueryDrop.KindTakesNoDecade);

        // Rule 5.
        if (useGenre && useEra)
        {
            dropped.Add(MoodQueryDrop.DecadeLostToGenre);
            useEra = false;
        }

        var list = useGenre ? SmartPlaylistId.Within(kind.Value, genre!)
                 : useEra ? SmartPlaylistId.InEra(kind.Value, decade)
                 : SmartPlaylistId.WholeShelf(kind.Value);

        return new MoodQuery(list, genre, decade, dropped);
    }

    /// <summary>The strongest of some spans: the longest phrase, and the earliest where two are the
    /// same length.</summary>
    private static Span? Best(IEnumerable<Span> spans) =>
        spans.OrderByDescending(s => s.Words).ThenBy(s => s.At).FirstOrDefault();
}
