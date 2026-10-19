using System.Collections.Frozen;

namespace RPM.Core.Lyrics;

/// <summary>
/// How positive or negative one word is, from VADER's word list.
///
/// <para>The table itself is generated into <c>LyricLexicon.Data.cs</c> by
/// <c>scripts/gen_lyric_lexicon.py</c>. The generated file carries the licence notice. This file reads
/// the table.</para>
///
/// <para><b>Parsed lazily and only once.</b> A collection whose records have no lyric sheets never pays
/// for it. A collection that has sheets pays for one pass over about seven thousand words, the first
/// time a song is scored.</para>
/// </summary>
public static partial class LyricLexicon
{
    private static readonly Lazy<FrozenDictionary<string, double>> Ratings = new(Parse);

    private static FrozenDictionary<string, double> Parse()
    {
        var ratings = new Dictionary<string, double>(Words, StringComparer.Ordinal);

        // A word and its rating alternate, separated by whitespace. The generator wraps the lines.
        // Splitting on whitespace, and not line by line, means the line width is the generator's choice
        // and this code does not depend on it.
        var fields = Table.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < fields.Length; i += 2)
            if (double.TryParse(fields[i + 1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var rating))
                ratings[fields[i]] = rating;

        return ratings.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The rating of this word, or null when the lexicon has no rating for it. Most words in a
    /// song are null. The table has about seven thousand words, and a song has a few hundred.
    /// </summary>
    /// <param name="word">Lower case, letters and apostrophes, as <see cref="LyricValence"/>
    /// tokenizes them.</param>
    public static double? Rating(string word)
        => Ratings.Value.TryGetValue(word, out var rating) ? rating : null;

    /// <summary>Whether the table has been read yet. Only a test that checks the table stays lazy should
    /// use this.</summary>
    internal static bool Read => Ratings.IsValueCreated;
}
