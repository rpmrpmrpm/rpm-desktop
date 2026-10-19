using RPM.Core.Analysis;

namespace RPM.Core.Mixing;

/// <summary>
/// What the two records are singing over the whole of one candidate changeover, and what that is worth.
///
/// <para><b>A cost, not a threshold, because "nowhere is clean" is the normal answer.</b> A rule that
/// asks whether a stretch is quiet enough gives up when nowhere is quiet. On a record that sings from
/// end to end, the changeover then happens wherever it was already going to, over the last chorus.
/// Scoring every candidate and taking the cheapest gives that record its least bad phrase instead.
/// There is always an answer. It is simply worse on some records than on others.</para>
///
/// <para><b>Ordered, not added up, because the rule is a priority and not a trade-off.</b> Avoid laying
/// two voices over each other. Where that is impossible, prefer the place where only one of them is
/// singing. This is a strict order. Fewer seconds of two voices ALWAYS wins. How much solo singing
/// there is only decides between candidates that tie on it.</para>
///
/// <para>A weighted sum does not work here. At any weight small enough to make a solo vocal cheap, a
/// long changeover full of one voice scored worse than a short one with the two records singing over
/// each other. That is the exact swap this rule exists to prevent. A sum can express a priority only by
/// making one weight so much larger that it becomes a comparison in disguise. Then the number has to be
/// re-tuned each time a fade gets longer. So it is a comparison.</para>
///
/// <para>The changeover has its own clock. Zero is where the blend opens. Both records are somewhere
/// else in themselves. Everything here is worked out after both records are placed on that
/// clock.</para>
/// </summary>
/// <param name="Both">Seconds of the changeover with somebody singing on both records.</param>
/// <param name="Leaving">Seconds with only the outgoing record singing.</param>
/// <param name="Arriving">Seconds with only the incoming record singing. Counted apart from
/// <paramref name="Leaving"/> because it is worse. See <see cref="Mixing.VoicePlacement"/>.</param>
/// <param name="Neither">Seconds with nobody singing on either record. This is what a DJ is looking
/// for.</param>
public readonly record struct MixVoices(double Both, double Leaving, double Arriving, double Neither)
{
    /// <summary>Seconds with exactly one of them singing, either way round.</summary>
    public double One => Leaving + Arriving;

    /// <summary>Two readings this close together are the same reading. Below this, the numbers are
    /// floating point noise from the timestamps of two different records. Letting noise decide would
    /// move a changeover by a phrase for nothing.</summary>
    public const double Same = 0.05;

    /// <summary>Whether the two records sing over each other at any point of the changeover. This is
    /// the fault the whole rule exists to avoid.</summary>
    public bool Clash => Both > Same;

    // Deliberately not comparable. Ranking two candidate changeovers needs more than what is sung over
    // them. It also needs how much of each record a changeover gives up to be where it is. An ordering
    // here could not see that. It lives in VoicePlacement.Better, which has both halves, and there is
    // exactly one of it.

    /// <summary>How this reads in a trace: the two numbers that decided it, in the order they decided
    /// it.</summary>
    public override string ToString() =>
        Both > Same
            ? $"{Both:F1}s of two voices, {Arriving:F1}s of the new one alone, {Leaving:F1}s of the old"
            : One > Same
                ? $"no two voices, {Arriving:F1}s of the new one alone, {Leaving:F1}s of the old"
                : "nobody singing";

    /// <summary>
    /// The voices over one candidate changeover.
    /// </summary>
    /// <param name="from">The record being left. <paramref name="fromAt"/> is where in it the blend
    /// opens.</param>
    /// <param name="to">The record coming in. <paramref name="toAt"/> is the point it is cued to. The
    /// two are heard together from there, each on its own clock.</param>
    /// <param name="seconds">How long the two are laid over each other.</param>
    public static MixVoices Over(
        VocalMap from, double fromAt, VocalMap to, double toAt, double seconds)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (seconds <= 0)
            return new MixVoices(0, 0, 0, 0);

        var leaving = from.Within(fromAt, seconds);
        var arriving = to.Within(toAt, seconds);

        double sungLeaving = Total(leaving), sungArriving = Total(arriving);
        var both = Overlap(leaving, arriving);

        // Inclusion and exclusion, once each list is on the changeover's own clock. What each record
        // sings alone is its own singing, less the part that the other record is singing over too.
        var neither = Math.Max(0, seconds - (sungLeaving + sungArriving - both));

        return new MixVoices(both, sungLeaving - both, sungArriving - both, neither);
    }

    private static double Total(List<(double From, double To)> spans)
    {
        double sum = 0;
        foreach (var (from, to) in spans)
            sum += to - from;
        return sum;
    }

    /// <summary>How much of two lists of stretches falls on the same seconds. Both lists come from
    /// <see cref="VocalMap.Within"/>, in order and merged. So this is one pass, not a comparison of
    /// every pair.</summary>
    private static double Overlap(List<(double From, double To)> a, List<(double From, double To)> b)
    {
        double shared = 0;
        int i = 0, j = 0;

        while (i < a.Count && j < b.Count)
        {
            var start = Math.Max(a[i].From, b[j].From);
            var end = Math.Min(a[i].To, b[j].To);
            if (end > start)
                shared += end - start;

            // Step whichever finishes first. The other may still meet the one after it.
            if (a[i].To < b[j].To)
                i++;
            else
                j++;
        }

        return shared;
    }
}
