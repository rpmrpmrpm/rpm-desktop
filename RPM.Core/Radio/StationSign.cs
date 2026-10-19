using System.Globalization;
using System.Text;

namespace RPM.Core.Radio;

/// <summary>
/// The three- or four-letter mark a station is known by on the dial card. This is its call sign.
///
/// <para>A dial prints short names, not sentences. "Smooth Jazz Lounge Radio, The Netherlands" is a
/// long name. SJL is what fits under a graduation and still tells one station from its neighbour. The
/// directory does not publish call signs, so one is made from the name, the way a listener would say it
/// aloud. It uses the letters that people actually keep.</para>
///
/// <para>The sign is derived rather than stored, and it is pure: the same name always gives the same
/// sign. So the mark on the card and the mark beside the row cannot drift apart. Neither has to be
/// saved with a favourite.</para>
/// </summary>
public static class StationSign
{
    /// <summary>The longest a sign can be. Four is the width the card is laid out for. Longer marks
    /// would stop lining up as a column of equal-width marks.</summary>
    private const int MaxLength = 4;

    /// <summary>
    /// Words that say "this is a radio station", and so say nothing about WHICH one. They are dropped
    /// while anything else is left. So "Radio Nova" gives NOVA, but a name that is only "Radio" keeps
    /// the word.
    /// </summary>
    private static readonly HashSet<string> Noise =
    [
        "RADIO", "RADIOS", "RADIU", "RADION", "FM", "AM", "HD", "DAB", "STATION", "STATIONS",
        "ONLINE", "LIVE", "STREAM", "STREAMING", "NET", "WEB", "DIGITAL", "CHANNEL", "THE",
    ];

    /// <summary>
    /// Small joining words, such as OF and THE. They are dropped before the initials of a multi-word
    /// name are taken, but they are never allowed to empty the name.
    /// </summary>
    private static readonly HashSet<string> Joiners =
    [
        "OF", "AND", "THE", "A", "AN", "DE", "DA", "DI", "DU", "DEL", "LA", "LE", "LES", "EL",
        "IL", "DER", "DIE", "DAS", "VAN", "VON", "EN", "ET", "Y",
    ];

    /// <summary>
    /// The station's mark. Never empty for a name with a letter or a digit in it, and never longer than
    /// four characters.
    /// </summary>
    public static string For(string? name)
    {
        var words = Words(name);
        if (words.Count == 0)
            return "";

        // Joining words carry no meaning on their own. A name made only of them still needs a sign.
        var carriers = words.Where(w => !Joiners.Contains(w)).ToList();
        if (carriers.Count == 0)
            carriers = words;

        // A name with one short word, such as NPO, BBC, KEXP or 538, already is the call sign. Taking
        // initials from it would throw away the part that listeners use. A number after that word is
        // kept with it, because it tells sister stations apart. For example, "BBC Radio 1" gives BBC1.
        // This applies only when there is no third word. With three or more words, the short first word
        // is a prefix and not a sign. For example, "Sky Radio Christmas Hits Nonstop" is not "Sky".
        var lead = carriers[0];
        if (carriers.Count <= 2 && lead.Length is >= 2 and <= MaxLength)
        {
            var number = carriers.Skip(1).FirstOrDefault(IsNumber);
            var joined = number is null ? lead : lead + number;
            if (joined.Length is >= 3 and <= MaxLength)
                return joined;
            if (lead.Length >= 3)
                return lead;
        }

        // Three or more words that carry meaning: take their initials, in order. This is how a long
        // name is shortened in speech. For example, Smooth Jazz Lounge gives SJL.
        if (carriers.Count > 2)
            return new string(carriers.Take(MaxLength).Select(w => w[0]).ToArray());

        // One word, or two short ones. Use the opening letters of the words run together, since that is
        // how a reader recognizes the name. For example, CLASSICAL MUSIC gives CLAS, and Q-MUSIC gives
        // QMUS rather than a lone Q.
        var sb = new StringBuilder();
        foreach (var word in carriers)
        {
            sb.Append(word);
            if (sb.Length >= MaxLength)
                break;
        }
        return sb.ToString()[..Math.Min(MaxLength, sb.Length)];
    }

    /// <summary>
    /// The name as words worth signing by: upper case, accents folded, punctuation gone, and the "this
    /// is a radio" words dropped, unless they were all there was.
    /// </summary>
    private static List<string> Words(string? name)
    {
        var text = Fold(name ?? "");
        var words = new List<string>();
        var word = new StringBuilder();

        foreach (var c in text)
        {
            // Letters and digits build words. Anything else ends a word, including dashes, dots and
            // symbols. A digit run is a word of its own, so "Radio 538" gives 538 rather than R5.
            if (char.IsLetterOrDigit(c))
            {
                if (word.Length > 0 && char.IsDigit(c) != char.IsDigit(word[^1]))
                {
                    words.Add(word.ToString());
                    word.Clear();
                }
                word.Append(c);
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
        if (word.Length > 0)
            words.Add(word.ToString());

        var carried = words.Where(w => !Noise.Contains(w)).ToList();
        return carried.Count > 0 ? carried : words;
    }

    /// <summary>Upper case with accents removed. So "Rádio Comercial" and "Radio Comercial" give the
    /// same sign, and a mark never carries a diacritic that the dial's letter spacing is not set
    /// for.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsNumber(string word) => word.Length > 0 && char.IsDigit(word[0]);
}
