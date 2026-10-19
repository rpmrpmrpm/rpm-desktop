namespace RPM.Core.Mixing;

/// <summary>How the next key rolls a record out, and the one after it in.</summary>
public enum RollStyle
{
    /// <summary>
    /// The last bar, round and round for the whole roll. This is the plainest and the least insistent.
    /// The record does not change. It simply stops going anywhere, and the filter closing over it says
    /// the rest.
    /// </summary>
    Bar,

    /// <summary>
    /// The beat roll. Half a bar round and round, then a beat, then half a beat. Each stage is shorter
    /// than the last, so the record is pulled tighter and tighter towards the moment it goes. It is the
    /// oldest trick in the booth, and a room still reacts to it.
    /// </summary>
    Beat,
}

/// <summary>
/// The shapes the two rolls are made of: which slice goes round, and for how long.
///
/// <para>This is kept apart from <see cref="Dj"/> because it is a decision about music, not about audio
/// plumbing. It is kept apart from <see cref="TransitionPlanner"/> because a roll is chosen by chance,
/// not worked out. The planner gives the same answer twice for the same two records. This class
/// deliberately does not.</para>
/// </summary>
public static class Rolls
{
    /// <summary>
    /// How often the beat roll is used rather than the plain one. The value is one half. Two moves
    /// chosen at random give variety. Any other weighting would be a preference that nothing has
    /// justified.
    /// </summary>
    public const double BeatRollChance = 0.5;

    /// <summary>Which roll this changeover gets, given a number from 0 to 1.</summary>
    public static RollStyle Choose(double luck) => luck < BeatRollChance ? RollStyle.Beat : RollStyle.Bar;

    /// <summary>
    /// The roll the outgoing record is played through, over <paramref name="bars"/> bars of
    /// <paramref name="barFrames"/> each.
    ///
    /// <para>The beat roll spends its first half on two beats. That is long enough to still sound like
    /// the record. Its next quarter is one beat. Its last quarter is half a beat. By then it is a
    /// stutter, and the room knows what is about to happen. Every slice is a prefix of the first one.
    /// That is what lets the deck capture once.</para>
    /// </summary>
    public static IReadOnlyList<RollStep> Out(RollStyle style, long barFrames, int bars)
    {
        long roll = barFrames * Math.Max(1, bars);

        if (style != RollStyle.Beat || barFrames < 8)
            return [new RollStep(barFrames, roll)];

        long beat = barFrames / 4;

        // Half the roll on two beats, a quarter on one, a quarter on half of one. The last of them
        // takes whatever is left over, so the three always add up to the roll exactly, however the
        // division rounded.
        long first = roll / 2, second = roll / 4;
        return
        [
            new RollStep(2 * beat, first),
            new RollStep(beat, second),
            new RollStep(Math.Max(1, beat / 2), roll - first - second),
        ];
    }

    /// <summary>
    /// The roll the incoming record arrives on, over <paramref name="bars"/> bars of its own
    /// <paramref name="barFrames"/>.
    ///
    /// <para>The beat roll halves twice on the way in, in the same way as the roll going out. First a
    /// beat, then half of that, then half of that again. By the last bar the kick is playing on its
    /// own, and it sounds far faster than the record it came from. The outgoing record gets shorter and
    /// tighter, and the incoming record answers at the same rate. By the end the two are one pulse, not
    /// two pieces of music.</para>
    ///
    /// <para>How much of the record a roll-in consumes is not decided here. The deck's capture length
    /// decides it. The caller sets that to a whole bar, so the record plays on from a bar line however
    /// short the slices got. See <see cref="Deck.RollIn"/>.</para>
    ///
    /// <para>The plain roll brings the record in a bar at a time, and lets the record do the
    /// talking.</para>
    /// </summary>
    public static IReadOnlyList<RollStep> In(RollStyle style, long barFrames, int bars)
    {
        long roll = barFrames * Math.Max(1, bars);

        if (style != RollStyle.Beat || barFrames < 16)
            return [new RollStep(barFrames, roll)];

        long beat = barFrames / 4;

        // Half the roll on the beat, a quarter on half of it, and the last quarter on half of that. The
        // last stage takes whatever is left over, so the three always add up to the roll exactly,
        // however the division rounded.
        long first = roll / 2, second = roll / 4;

        return
        [
            new RollStep(beat, first),
            new RollStep(Math.Max(1, beat / 2), second),
            new RollStep(Math.Max(1, beat / 4), roll - first - second),
        ];
    }
}
