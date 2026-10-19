using RPM.Core.Audio;

namespace RPM.Core.Mixing;

/// <summary>
/// The fader between the two decks, and the bass swap that goes with it. Given how far through a transition
/// the mix is (0 is the outgoing record alone, 1 is the incoming record alone), it returns the level and
/// the low shelf for each deck.
///
/// <para>Equal power, not equal amplitude. Two unrelated records at half level each add up to about 0.7 of
/// one full level. So a linear fade dips in the middle, and the mix sags exactly where it should be at its
/// strongest. Sine and cosine curves keep the sum of the powers constant.</para>
///
/// <para>The crossing finishes before the changeover does. See <see cref="OutBy"/>. The outgoing record is
/// silent for the last quarter of the changeover, so the incoming record has the room to itself while it
/// arrives.</para>
/// </summary>
public static class Crossfader
{
    /// <summary>
    /// How far through a changeover the crossing is finished: the outgoing record silent, the incoming one
    /// at full.
    ///
    /// <para>Not all of it. A record that is still playing a bar before the changeover ends has outstayed
    /// its welcome. The new record has been playing for most of the handover by then, and listeners have
    /// moved on to it. Three quarters leaves the last quarter of every changeover to the incoming record
    /// alone. That is four bars of a sixteen-bar mix, two bars of an eight-bar roll-out, and on the bar
    /// line either way.</para>
    /// </summary>
    public const double OutBy = 0.75;

    /// <summary>Half way through the crossing, where the two records are level with each other. Past this
    /// point the incoming record is the louder of the two, so this is where what the room hears
    /// changes.</summary>
    public const double Balance = OutBy / 2;

    /// <summary>The pair of levels at position <paramref name="t"/> through the fade.</summary>
    public readonly record struct Levels(double Outgoing, double Incoming);

    public static Levels At(double t)
    {
        double angle = Crossed(t) * Math.PI / 2;
        return new Levels(Math.Cos(angle), Math.Sin(angle));
    }

    /// <summary>How far through the crossing a changeover is. The crossing finishes before the changeover
    /// does. See <see cref="OutBy"/>.</summary>
    private static double Crossed(double t) => Math.Clamp(t, 0, 1) / OutBy is var u && u > 1 ? 1 : u;

    /// <summary>
    /// The bass shelves, in dB. The bottom end belongs to one record at a time. The incoming deck starts
    /// with its bass cut. The swap happens over the middle third of the fade. This is early enough that the
    /// new kick is established before the old one leaves, and late enough that the two never share the
    /// bottom.
    /// </summary>
    public static Levels BassDb(double t)
    {
        // 0 to 1 across the middle third of the crossing, and flat on either side.
        double swap = Math.Clamp((Crossed(t) - 0.33) / 0.34, 0, 1);
        return new Levels(DeckEq.KillDb * swap, DeckEq.KillDb * (1 - swap));
    }
}
