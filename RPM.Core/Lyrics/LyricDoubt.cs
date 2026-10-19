namespace RPM.Core.Lyrics;

/// <summary>
/// Whether a measured word placement can be trusted, or whether the line should keep the sheet's own
/// even spread instead.
///
/// <para><b>What this detects.</b> Forced alignment must use every frame it is given. Where the
/// recording gives it nothing clear to hold on to (a vowel held over a chord, two voices in harmony, a
/// line sung under a horn section), the best path is to stay on one letter for the length of the held
/// note. The rest of the words are then fitted into the frames that are left. The result is one word
/// of about a second and a half, followed by five words of 60 ms each. The sheet then stays on the
/// first word while the singer is four words further on. This was seen on the chorus of ABBA's "SOS",
/// in each of the four times the line comes round.</para>
///
/// <para><b>Why it can be detected without the correct answer.</b> There is no true timing to compare
/// with. The shape of the result contradicts itself. No singer holds one word for a second and a
/// quarter and then sings the next four words in a fifteenth of a second each. So the placement is
/// checked against its own claim, and that claim cannot be true.</para>
///
/// <para><b>What is not detected.</b> A long word at the end of a line is normal singing. The last
/// held note is most of what a chorus is, and such lines place well. So a hold counts against a
/// placement only when words follow it. It is the crushed words after the hold that make the result a
/// contradiction.</para>
///
/// <para>Pure, and in Core, so tests can check the rule without a window. The benchmark uses the same
/// judgement as the app.</para>
/// </summary>
public static class LyricDoubt
{
    /// <summary>
    /// A word this short was not really sung. It was squeezed in. 60 to 100 ms is less than one syllable
    /// at any tempo. The benchmark uses the same threshold to mark a crushed word.
    /// </summary>
    public const double Crushed = 0.15;

    /// <summary>How long a word must last before it can count as a hold at all, whatever the rest of the
    /// line looks like. A word under a second is never a park. A line whose longest word is under a
    /// second has nothing in it that could be a park.</summary>
    public const double Held = 1.0;

    /// <summary>How many times the line's median word length a hold must be. A slow line, where every
    /// word lasts about a second, is slow but not wrong. The check looks for one word far out of step
    /// with the rest. The median is used rather than the mean, because the held word itself would pull
    /// the mean up.</summary>
    public const double HeldTimes = 4.0;

    /// <summary>How many crushed words must follow the hold for the pair to count as a contradiction. One
    /// crushed word can be a short word sung quickly. Two in a row after a hold four times the line's
    /// median pace means the aligner has run out of room.</summary>
    public const int CrushedAfter = 2;

    /// <summary>The fewest words a line needs before this check runs. A three-word line has no pace of
    /// its own to compare with, and its median is one word.</summary>
    public const int LeastWords = 5;

    /// <summary>
    /// True when the placement contradicts itself, so the line should keep the sheet's own timing.
    /// </summary>
    /// <param name="placed">The words as the model placed them, in singing order. This is the raw answer,
    /// checked before any tail is shared out. A rescue reads these same placements to decide what to
    /// distrust. If the tail had been shared out first, the check would judge the rescue's output instead
    /// of the model's mistake.</param>
    public static bool Parked(IReadOnlyList<WordSpan> placed)
    {
        ArgumentNullException.ThrowIfNull(placed);

        if (placed.Count < LeastWords)
            return false;

        var middling = Median(placed);
        if (middling <= 0)
            return false;

        // Every word but the last. A hold at the end of a line is a singer holding a note, with nothing
        // after it to be crushed.
        for (var i = 0; i < placed.Count - 1; i++)
        {
            var length = placed[i].To - placed[i].From;
            if (length < Held || length < middling * HeldTimes)
                continue;

            var crushed = 0;
            for (var after = i + 1; after < placed.Count; after++)
                if (placed[after].To - placed[after].From <= Crushed)
                    crushed++;

            if (crushed >= CrushedAfter)
                return true;
        }

        return false;
    }

    /// <summary>The line's median word length. The median is used rather than the mean, for the reason
    /// above. It is taken from a copy, because the caller's list is the answer being judged and must not
    /// be reordered.</summary>
    private static double Median(IReadOnlyList<WordSpan> placed)
    {
        var lengths = new double[placed.Count];
        for (var i = 0; i < placed.Count; i++)
            lengths[i] = placed[i].To - placed[i].From;

        Array.Sort(lengths);
        return lengths.Length % 2 == 1
            ? lengths[lengths.Length / 2]
            : (lengths[(lengths.Length / 2) - 1] + lengths[lengths.Length / 2]) / 2;
    }
}
