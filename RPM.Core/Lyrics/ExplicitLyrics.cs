using System.Linq;

namespace RPM.Core.Lyrics;

/// <summary>
/// Picks the uncensored sheet for an explicit record from the sheets LRCLIB offers.
///
/// <para>LRCLIB often holds two sheets for one song: the explicit one and the clean (edited) one. They have the
/// same names and nearly the same length, so the exact lookup can return either. For a record marked explicit,
/// the explicit sheet is the right one. This class gives each candidate a score and keeps the best.</para>
///
/// <para><b>The score.</b> Each masked word (a word with <c>*</c> in it, a <c>#</c> after a letter or another
/// <c>#</c>, or a <c>[censored]</c> marker) costs <see cref="MaskPenalty"/> points. Each word from
/// <see cref="Words"/> adds one point. A clean sheet with masks scores below zero, and the explicit sheet scores
/// above it. A clean sheet that drops the words without masking them scores zero, and the explicit sheet still
/// scores more.</para>
///
/// <para><b>The same rules on every device.</b> The iPhone app (RPMKit <c>ExplicitLyrics.swift</c>) and the
/// Android apps (core <c>ExplicitLyrics.kt</c>) use the same word list, the same split into words and the same
/// score. Change all three together.</para>
///
/// <para>The display masking of <see cref="LyricCensor"/> is a separate thing. It is the listener's own setting
/// and is applied when a line is drawn. This class only chooses which sheet is fetched.</para>
/// </summary>
public static class ExplicitLyrics
{
    /// <summary>What one masked word costs. It is large, so that one mask outweighs several explicit
    /// words.</summary>
    public const int MaskPenalty = 10;

    /// <summary>
    /// Common explicit words, in lower case and letters only. They come from the swearing list of
    /// <see cref="LyricCensor"/>, without the words that also have an innocent meaning (such as "hell" or
    /// "cock"). A word is counted when the letters of a word in the sheet are exactly one of these.
    /// </summary>
    public static readonly IReadOnlySet<string> Words = new HashSet<string>(StringComparer.Ordinal)
    {
        "arse", "arsehole", "ass", "asshole", "assholes",
        "bastard", "bastards", "bitch", "bitches", "bitchin", "bitching", "bollocks", "bullshit",
        "cunt", "cunts", "damn", "dammit",
        "fuck", "fucked", "fucker", "fuckers", "fuckin", "fucking", "fucks", "goddamn",
        "motherfucker", "motherfuckers", "motherfuckin", "motherfucking",
        "piss", "pissed", "pussies", "pussy",
        "shit", "shits", "shittin", "shitting", "shitty", "slut", "sluts",
        "tits", "titties", "twat", "wanker", "whore", "whores",
    };

    /// <summary>
    /// The sheet's score: one point for each explicit word, minus <see cref="MaskPenalty"/> for each masked
    /// word. See the class remarks.
    /// </summary>
    public static int Score(LyricSheet sheet)
    {
        var score = 0;
        foreach (var line in sheet.Lines)
        {
            foreach (var raw in line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.ToLowerInvariant();
                if (IsMasked(token))
                    score -= MaskPenalty;
                else if (Words.Contains(LettersOf(token)))
                    score++;
            }
        }
        return score;
    }

    /// <summary>
    /// The best sheet for an explicit record, or <see cref="LyricSheet.Empty"/> when there is no candidate.
    ///
    /// <para>The highest <see cref="Score"/> wins. On a tie, <paramref name="exact"/> wins if it is one of the
    /// best. Otherwise the first of the best in <paramref name="found"/> wins, so LRCLIB's own order decides.</para>
    /// </summary>
    /// <param name="exact">The exact lookup's answer, or <see cref="LyricSheet.Empty"/> when it had none.</param>
    /// <param name="found">The search's candidates, in LRCLIB's order. Sheets without timings are ignored.</param>
    public static LyricSheet Pick(LyricSheet exact, IReadOnlyList<LyricSheet> found)
    {
        var best = LyricSheet.Empty;
        var bestScore = int.MinValue;

        // The exact answer is looked at first, and a later candidate replaces it only with a higher score.
        // That is what makes a tie keep the exact answer.
        if (exact.IsSynced)
        {
            best = exact;
            bestScore = Score(exact);
        }

        foreach (var sheet in found)
        {
            if (!sheet.IsSynced)
                continue;

            var score = Score(sheet);
            if (score > bestScore)
            {
                best = sheet;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a word (already in lower case) is masked: it has a <c>*</c>, a <c>#</c> after a letter or after
    /// another <c>#</c> ("f#ck", "###"), or it is a "[censored]" or "(censored)" marker. A <c>#</c> at the
    /// start of a word, as in "#1", is not a mask.
    /// </summary>
    private static bool IsMasked(string token)
    {
        if (token.Contains('*') || token.Contains("[censored", StringComparison.Ordinal)
                                || token.Contains("(censored", StringComparison.Ordinal))
            return true;

        for (var i = 1; i < token.Length; i++)
        {
            if (token[i] == '#' && (char.IsLetter(token[i - 1]) || token[i - 1] == '#'))
                return true;
        }
        return false;
    }

    /// <summary>The letters of a word, without punctuation or digits. "Fuckin'," gives "fuckin".</summary>
    private static string LettersOf(string token)
        => string.Concat(token.Where(char.IsLetter));
}
