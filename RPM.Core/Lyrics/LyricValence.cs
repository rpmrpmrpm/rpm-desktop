namespace RPM.Core.Lyrics;

/// <summary>
/// How positive or negative a song's words are, or the fact that they cannot say.
/// </summary>
/// <param name="Valence">The mean rating of the words the lexicon has an opinion about, in the
/// lexicon's own units. Across a real collection this is about -3.1 to +3.2. The scale is VADER's and
/// is not rescaled here. <c>MoodMeter</c> stores three raw numbers rather than a band, for the same
/// reason: the recipe can change, and the measurement stays the same.</param>
/// <param name="Words">How many words the reading was taken over. A caller can use this to judge how
/// much there was to go on. It is also what <see cref="LyricValence.LeastWords"/> is checked
/// against.</param>
public readonly record struct LyricReading(double Valence, int Words)
{
    /// <summary>No usable reading: no sheet, not English enough, or too few words the lexicon knows.
    /// This is not zero. Zero is the middle of the scale. It would put a song nobody has read in the
    /// centre of a distribution it was never measured against.</summary>
    public static readonly LyricReading Unknown = new(double.NaN, 0);

    public bool Known => !double.IsNaN(Valence);
}

/// <summary>
/// Reads a lyric sheet as one number.
///
/// <para><b>What this is for.</b> <c>MoodMeter</c> measures loud or quiet, and busy or still. It cannot
/// measure happy or sad. Two songs with the same brightness, onset rate and dynamic range can feel very
/// different if one is in a major key and the other is minor. The words can measure this. Published
/// research shows that a lyrics model beats an audio model on valence in particular.</para>
///
/// <para><b>It never decides whether a song is in a list.</b> A reading only adds to what the audio
/// already says, for the songs that have one. About 7% of a real collection can be read this way,
/// because sheets are fetched when a record is played, not for the whole collection. A rule that needed
/// a reading would apply only to the played part of a collection. Every other song is ranked by the
/// measurements that exist for all of them, as now. Same rule as <c>MoodFlavour</c>: a preference,
/// never a filter.</para>
///
/// <para>Pure: no database, no network, no files. It is handed a sheet that somebody already
/// fetched.</para>
/// </summary>
public static class LyricValence
{
    /// <summary>
    /// How much of the text must be ordinary English before the lexicon is trusted.
    ///
    /// <para>This gate separates a result that works from one that is confidently wrong. Without it,
    /// across 1,857 real songs, six of the eight most negative songs in the whole collection were Dutch
    /// or French. Foreign words match English lexicon entries by accident. A song that matches only a
    /// few words gets an extreme mean. Extremes are what a list built on this value draws from. A Sad
    /// list would then be mostly Dutch pop, and nothing would report a fault.</para>
    ///
    /// <para>The value is 12%. It was set from measurements: 0.12 and 0.15 select the same songs at both
    /// ends, so the exact value is not critical. English prose is far above either one.</para>
    /// </summary>
    public const double LeastEnglishShare = 0.12;

    /// <summary>
    /// The fewest words the lexicon must recognise before a mean over them means anything.
    ///
    /// <para>Twenty. A mean over ten words is noise, and noise lands at the extremes, which is where a
    /// list draws from. The same measurement as the gate above applies: 20 and 25 select the same songs
    /// at both ends. The median real song has 27 recognised words.</para>
    /// </summary>
    public const int LeastWords = 20;

    /// <summary>How far back a negation reaches. VADER's own window.</summary>
    private const int NegationReach = 3;

    /// <summary>What a negation does to the word it reaches. It flips the word and removes some of its
    /// force. This is VADER's value. It is not a clean flip, because "not bad" is not as positive as
    /// "good".</summary>
    private const double Negated = -0.74;

    /// <summary>How much a booster changes the word after it, up or down. VADER's value.</summary>
    private const double Boost = 0.293;

    /// <summary>
    /// Words that reverse the sense of the words after them.
    ///
    /// <para>Folded to letters only, so "don't" and "dont" are the same entry. The tokenizer then does
    /// not need to keep apostrophes to find them.</para>
    /// </summary>
    private static readonly HashSet<string> Negators = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "none", "nobody", "nothing", "neither", "nowhere", "cannot", "cant",
        "dont", "didnt", "wont", "wouldnt", "couldnt", "shouldnt", "aint", "isnt", "arent", "wasnt",
        "werent", "havent", "hasnt", "hadnt", "without", "rarely", "seldom", "hardly", "barely",
    };

    /// <summary>Words that make the next word count for more, or for less.</summary>
    private static readonly Dictionary<string, double> Boosters = new(StringComparer.Ordinal)
    {
        ["very"] = Boost, ["really"] = Boost, ["so"] = Boost, ["too"] = Boost, ["such"] = Boost,
        ["absolutely"] = Boost, ["completely"] = Boost, ["extremely"] = Boost, ["totally"] = Boost,
        ["utterly"] = Boost, ["deeply"] = Boost, ["always"] = Boost,
        ["almost"] = -Boost, ["kinda"] = -Boost, ["slightly"] = -Boost, ["somewhat"] = -Boost,
        ["sorta"] = -Boost, ["nearly"] = -Boost,
    };

    /// <summary>
    /// Common English function words, used by the language gate.
    ///
    /// <para>Function words are used rather than content words, because every English text has them and
    /// texts in other languages do not. The list was chosen so that a foreign sheet does not pass the
    /// gate by accident.</para>
    /// </summary>
    private static readonly HashSet<string> English = new(StringComparer.Ordinal)
    {
        "the", "and", "you", "your", "that", "this", "with", "have", "from", "they", "what", "was",
        "were", "been", "are", "is", "its", "of", "to", "in", "on", "for", "but", "not", "all",
        "just", "like", "can", "will", "would", "there", "their", "them", "she", "he", "him", "her",
        "we", "my", "me", "be", "do", "does", "did", "about", "when", "where", "how", "who", "why",
        "out", "up", "down", "off", "over", "into", "only", "some", "know", "got", "get", "dont",
    };

    /// <summary>
    /// Reads one sheet.
    /// </summary>
    /// <param name="sheet">A sheet that somebody already fetched. Null or empty gives
    /// <see cref="LyricReading.Unknown"/>. That is an ordinary answer, not a fault.</param>
    public static LyricReading Read(LyricSheet? sheet)
    {
        if (sheet is null || sheet.Lines.Count == 0)
            return LyricReading.Unknown;

        var words = Words(sheet);
        if (words.Count == 0)
            return LyricReading.Unknown;

        var english = words.Count(English.Contains) / (double)words.Count;
        if (english < LeastEnglishShare)
            return LyricReading.Unknown;

        double total = 0;
        var counted = 0;

        for (var i = 0; i < words.Count; i++)
        {
            if (LyricLexicon.Rating(words[i]) is not { } rating)
                continue;

            // The nearest of the two wins, and the walk stops there. "not very good" is negated. If both
            // were read, the word would be boosted first and then the boosted value flipped. The sentence
            // would be read twice.
            for (var back = 1; back <= NegationReach && i - back >= 0; back++)
            {
                var before = words[i - back];
                if (Negators.Contains(before))
                {
                    rating *= Negated;
                    break;
                }

                if (Boosters.TryGetValue(before, out var boost))
                {
                    rating *= rating > 0 ? 1 + boost : 1 - boost;
                    break;
                }
            }

            total += rating;
            counted++;
        }

        return counted >= LeastWords ? new LyricReading(total / counted, counted) : LyricReading.Unknown;
    }

    /// <summary>
    /// The sheet as plain words, in lower case, with letters only.
    ///
    /// <para>Apostrophes are dropped, not kept, so "don't" becomes "dont". It then matches the negator
    /// list however the transcriber typed it. This is safe for the lexicon, because no word the generator
    /// keeps has an apostrophe in it. A test checks this.</para>
    /// </summary>
    private static List<string> Words(LyricSheet sheet)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();

        foreach (var line in sheet.Lines)
        {
            foreach (var c in line.Text)
            {
                if (char.IsAsciiLetter(c))
                    word.Append(char.ToLowerInvariant(c));
                else if (c is not ('\'' or '’'))
                    Flush();
            }

            Flush();
        }

        return words;

        void Flush()
        {
            if (word.Length > 0)
                words.Add(word.ToString());
            word.Clear();
        }
    }
}
