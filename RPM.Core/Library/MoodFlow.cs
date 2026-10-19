using RPM.Core.Analysis;

namespace RPM.Core.Library;

/// <summary>The order a generated list plays in, once its songs have been chosen.
///
/// <para><b>What this fixes.</b> A mood list is ranked by how far into its band each song sits. Read top to
/// bottom, it runs from one corner of the collection to the other. For example, the calmest fifty songs of
/// a rock shelf can open on a solo acoustic take and close on a full band. Every song in it is calm, as the
/// ranking promised, but the fifty songs do not play like a record.</para>
///
/// <para><b>It reorders and never reselects.</b> Which songs are in the list is settled before this runs,
/// by the narrowings, the window and the caps. None of that is changed here. This is the only step in the
/// whole path that changes an order. That is why it has its own file and runs last. Every step before it
/// keeps ranked order, as <see cref="MoodPool.Window"/> states.</para>
///
/// <para><b>Approximate, not optimal.</b> Ordering a playlist is a shortest-path walk over a complete
/// graph. That is the travelling salesman problem, which is NP-complete, so an exact answer is not
/// practical. Published work uses an approximation for this reason. This uses the usual pair: a
/// nearest-neighbour walk from a fixed start (<see cref="NearestFirst"/>), then 2-opt to remove the
/// crossings (<see cref="Straighten"/>). Both are O(n squared) over fifty songs. The second step is
/// required. See the note on it for what a listener would hear without it.</para>
///
/// <para><b>The first song does not move.</b> The list still opens on the strongest song the ranking found,
/// and flows from there. This keeps the main property that the ranking gives.</para>
/// </summary>
internal static class MoodFlow
{
    /// <summary>The fewest songs worth ordering. Two songs have only one arrangement, and the first is
    /// pinned, so there is nothing to decide below three.</summary>
    internal const int LeastToOrder = 3;

    /// <summary>How much a key clash is allowed to weigh against the sound.
    ///
    /// <para>Half. The reason is coverage, not taste. Brightness, onsets and dynamics are measured for
    /// every song in a list, because those three numbers are what put the song there. A key is measured
    /// only for the songs the analyser has reached, which on a real shelf is a few percent. A term that
    /// only some songs carry must not outweigh the one that they all carry, or a list would be arranged
    /// around its minority.</para>
    /// </summary>
    private const double KeyWeight = 0.5;

    /// <summary>What an unknown key contributes: neither a match nor a clash.
    ///
    /// <para>Not nought. Nought would make two songs that nobody has analysed look like a perfect harmonic
    /// match. They would beat two songs that were analysed and really do clash. The list would then be
    /// arranged to prefer songs with no data, and it would do this more the less the analyser had run. The
    /// midpoint says the honest thing: this pair is not known to fit, and not known to clash.</para>
    /// </summary>
    private const double UnknownKeyCost = 0.5;

    /// <summary>The furthest two known keys can be: six hours around the wheel, with a mode swap on top.
    /// See <see cref="MusicalKey.Distance"/>, where the scale is defined.</summary>
    private const double FurthestKeys = 7;

    /// <summary>
    /// The chosen songs, reordered so that each one sounds like the one before it.
    /// </summary>
    /// <param name="picked">The list as the window and the caps left it, in ranked order.</param>
    /// <param name="keys">The key of whichever of them the analyser has reached, by relative path.
    /// Songs absent from it are ordered on their sound alone. This is never null. Pass an empty
    /// dictionary to say the analyser has reached none of them, so a caller cannot mean that by
    /// accident.</param>
    internal static IReadOnlyList<MoodList.Song> Ordered(
        IReadOnlyList<MoodList.Song> picked, IReadOnlyDictionary<string, MusicalKey> keys)
    {
        ArgumentNullException.ThrowIfNull(picked);
        ArgumentNullException.ThrowIfNull(keys);

        if (picked.Count < LeastToOrder)
            return picked;

        // A song whose three numbers are missing cannot be placed by how it sounds. It is not dropped, and
        // it is not guessed at. It keeps its ranked order at the end, behind everything that could be
        // placed. On a real shelf there are none, because the numbers are what put a song in the list.
        var placeable = new List<MoodList.Song>(picked.Count);
        var unplaceable = new List<MoodList.Song>();
        foreach (var song in picked)
            (song.Sound.Known ? placeable : unplaceable).Add(song);

        if (placeable.Count < LeastToOrder)
            return picked;

        var spread = Spread.Over(placeable);
        double Cost(int a, int b) => Between(placeable[a], placeable[b], spread, keys);

        var order = NearestFirst(placeable.Count, Cost);
        Straighten(order, Cost);

        return [.. order.Select(i => placeable[i]), .. unplaceable];
    }

    /// <summary>Walks from the first song. Each time, it takes whichever of the songs left sounds most like
    /// the one just taken.</summary>
    private static List<int> NearestFirst(int count, Func<int, int, double> cost)
    {
        var left = new List<int>(Enumerable.Range(1, count - 1));
        var order = new List<int>(count) { 0 };
        var at = 0;

        while (left.Count > 0)
        {
            var best = 0;
            var bestCost = double.MaxValue;

            // Ascending order, and the comparison is strictly less than. So two songs that fit equally well
            // are taken in ranked order. Two reads of one list must come back in the same order.
            for (var i = 0; i < left.Count; i++)
            {
                var next = cost(at, left[i]);
                if (next < bestCost)
                {
                    bestCost = next;
                    best = i;
                }
            }

            at = left[best];
            order.Add(at);
            left.RemoveAt(best);
        }

        return order;
    }

    /// <summary>Takes the crossings out of the walk, by reversing any run of it that is cheaper backwards.
    ///
    /// <para><b>Nearest-neighbour on its own has one weakness, and this step fixes it.</b> Always taking
    /// the closest song leaves behind the songs it skipped over. It pays for them later, with a long detour
    /// back across everything it has already covered. Reversing a run undoes this. Where the walk crosses
    /// itself, one of the two crossing steps is cheaper when it is turned round.</para>
    ///
    /// <para>This is 2-opt, the standard improvement on a nearest-neighbour tour. It is not an optimal
    /// walk, and it does not need to be.</para>
    ///
    /// <para><b>One turn is not a crossing, and it cannot be removed.</b> The first song is pinned. So a
    /// list that opens in the middle of its own range has to cover both sides of where it started, and one
    /// jump back is the best any arrangement can do. That is the floor, not a fault. It is also the normal
    /// case: the window moves through the ranked pool on every opening, so a list rarely starts at either
    /// extreme.</para>
    ///
    /// <para>The first song is never moved: <c>i</c> starts at 1, so every reversal keeps whatever is in
    /// front of it.</para>
    /// </summary>
    private static void Straighten(List<int> order, Func<int, int, double> cost)
    {
        // A cheaper walk by less than this is floating-point noise. Taking it could undo itself on the next
        // pass, and the loop would never end.
        const double Worthwhile = 1e-9;

        // Bounded, not run to exhaustion. This runs while somebody waits for a list, and the passes that
        // matter are the first few. Each pass is a walk over every pair.
        const int MostPasses = 8;

        for (var pass = 0; pass < MostPasses; pass++)
        {
            var improved = false;

            for (var i = 1; i < order.Count - 1; i++)
            {
                for (var j = i + 1; j < order.Count; j++)
                {
                    var tail = j + 1 < order.Count;

                    var before = cost(order[i - 1], order[i])
                               + (tail ? cost(order[j], order[j + 1]) : 0);
                    var after = cost(order[i - 1], order[j])
                              + (tail ? cost(order[i], order[j + 1]) : 0);

                    if (after >= before - Worthwhile)
                        continue;

                    order.Reverse(i, j - i + 1);
                    improved = true;
                }
            }

            if (!improved)
                return;
        }
    }

    /// <summary>How unlike each other two songs are: their sound, plus what their keys say where both are
    /// known.</summary>
    private static double Between(
        MoodList.Song a, MoodList.Song b, Spread spread, IReadOnlyDictionary<string, MusicalKey> keys)
    {
        var brightness = spread.Brightness.Apart(a.Sound.Brightness, b.Sound.Brightness);
        var onsets = spread.Onsets.Apart(a.Sound.Onsets, b.Sound.Onsets);
        var dynamics = spread.Dynamics.Apart(a.Sound.Dynamics, b.Sound.Dynamics);

        // Divided by the square root of three, so that a pair at opposite corners of all three axes scores
        // 1. This puts the sound term on the same scale as the key term beside it.
        var sound = Math.Sqrt((brightness * brightness) + (onsets * onsets) + (dynamics * dynamics))
                  / Math.Sqrt(3);

        return sound + (KeyWeight * KeysApart(a.Path, b.Path, keys));
    }

    /// <summary>What the two keys say, from nought for the same key to one for the furthest apart.
    /// Returns <see cref="UnknownKeyCost"/> where either key is unknown.</summary>
    private static double KeysApart(string a, string b, IReadOnlyDictionary<string, MusicalKey> keys)
    {
        if (!keys.TryGetValue(a, out var one) || !keys.TryGetValue(b, out var other))
            return UnknownKeyCost;

        var distance = one.Distance(other);
        return distance == int.MaxValue ? UnknownKeyCost : Math.Min(distance / FurthestKeys, 1);
    }

    /// <summary>The range one axis covers across the songs being ordered. This is what makes the three
    /// axes comparable.
    ///
    /// <para>The range is taken over THIS list, not over the collection. The question is which of these
    /// fifty songs is nearest to this one. Measured over the whole shelf, a list drawn from one narrow band
    /// would have every pair a hundredth apart, and the ordering would be noise.</para>
    ///
    /// <para>The three axes are on different scales to begin with. Brightness is a share of energy, onsets
    /// are events a second, and dynamics are decibels. Without normalising, decibels would decide
    /// everything.</para>
    /// </summary>
    private readonly record struct Axis(double Low, double High)
    {
        /// <summary>How far apart two readings are on this axis, from nought to one. Nought where the axis
        /// has no range at all. That is the honest answer: these songs do not differ on this axis, so the
        /// axis has no opinion about their order.</summary>
        internal double Apart(double a, double b)
            => High - Low is var range && range > 0 ? Math.Abs(a - b) / range : 0;

        internal static Axis Over(IReadOnlyList<MoodList.Song> songs, Func<MoodList.Song, double> of)
        {
            var low = double.MaxValue;
            var high = double.MinValue;
            foreach (var song in songs)
            {
                var value = of(song);
                low = Math.Min(low, value);
                high = Math.Max(high, value);
            }

            return new Axis(low, high);
        }
    }

    private readonly record struct Spread(Axis Brightness, Axis Onsets, Axis Dynamics)
    {
        internal static Spread Over(IReadOnlyList<MoodList.Song> songs) => new(
            Axis.Over(songs, s => s.Sound.Brightness),
            Axis.Over(songs, s => s.Sound.Onsets),
            Axis.Over(songs, s => s.Sound.Dynamics));
    }
}
