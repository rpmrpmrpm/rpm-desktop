using RPM.Core.Audio;

namespace RPM.Core.Mixing;

/// <summary>One point of a volume arc: how far through the fade (0..1 of its length) and the level
/// there (0 silent, 1 full).</summary>
public readonly record struct ArcPoint(double T, double Level);

/// <summary>Which record's curve a point belongs to.</summary>
public enum ArcSide
{
    Outgoing,
    Incoming,
}

/// <summary>
/// The volume arc of a changeover: the level curve each record follows through the fade, and the point
/// where the bass hands over. Straight lines join a few points. A hand on a screen can draw that, and a
/// journal line can record it.
///
/// <para>Until someone draws an arc, no plan has one. The decks follow <see cref="Crossfader"/> exactly
/// as they always have. <see cref="EqualPower"/> is that same curve set onto handles. It is where the
/// editor starts, so the first drag starts from the sound the app would have made anyway.</para>
///
/// <para>The levels are linear amplitude, the same unit the deck faders use. Equal power is meant for
/// two records that do not correlate and sum flat. That is a property of the default shape, not of this
/// class. A DJ who draws a dip in the middle of a changeover gets that dip.</para>
/// </summary>
public sealed record FadeArc
{
    /// <summary>The record leaving, usually from full to silence.</summary>
    public required IReadOnlyList<ArcPoint> Outgoing { get; init; }

    /// <summary>The record arriving, usually from silence to full.</summary>
    public required IReadOnlyList<ArcPoint> Incoming { get; init; }

    /// <summary>Where the bass handover begins, as a position in the fade.</summary>
    public double BassSwapStart { get; init; } = DefaultBassSwapStart;

    /// <summary>Where the bass handover is complete.</summary>
    public double BassSwapEnd { get; init; } = DefaultBassSwapEnd;

    /// <summary>The usual swap window, on the fade's own clock: the middle third of the crossing. The
    /// crossing itself ends at <see cref="Crossfader.OutBy"/>.</summary>
    public const double DefaultBassSwapStart = 0.33 * Crossfader.OutBy;
    public const double DefaultBassSwapEnd = 0.67 * Crossfader.OutBy;

    /// <summary>How many handles the default arc is drawn with. There are enough that the straight
    /// lines between them sound almost the same as the true curve, and few enough that each one can be
    /// grabbed.</summary>
    private const int Handles = 9;

    /// <summary>A point dragged along the clock stops this far short of its neighbour. Two handles
    /// never sit on the same pixel, and the curve never turns back on itself.</summary>
    private const double MinGap = 0.01;

    /// <summary>The crossfade the app has always made, set onto handles so a user can start editing
    /// from it.</summary>
    public static FadeArc EqualPower()
    {
        var outgoing = new ArcPoint[Handles + 1];
        var incoming = new ArcPoint[Handles + 1];

        for (int i = 0; i < Handles; i++)
        {
            // The crossing is over at OutBy; the tail past it is flat and needs only its end point.
            double t = Crossfader.OutBy * i / (Handles - 1);
            var levels = Crossfader.At(t);
            outgoing[i] = new ArcPoint(t, levels.Outgoing);
            incoming[i] = new ArcPoint(t, levels.Incoming);
        }

        outgoing[Handles] = new ArcPoint(1, 0);
        incoming[Handles] = new ArcPoint(1, 1);

        return new FadeArc { Outgoing = outgoing, Incoming = incoming };
    }

    /// <summary>
    /// The same crossfade, but the incoming record comes up over the first <paramref name="upBy"/> of
    /// the fade instead of over the whole of it. It is at full from there on.
    ///
    /// <para><b>What it is for.</b> The incoming record is cued so that its drop lands at the moment it
    /// reaches full. That is the reason its intro is played under the outgoing record. A record whose
    /// intro is shorter than the changeover cannot be cued far enough back. There is no audio before
    /// its first sample. The record then starts at its very top, the whole intro plays under the old
    /// record, and the drop arrives part way up the fade while the record is still quiet. The drop is
    /// the moment the room listens for, and it is heard too late.</para>
    ///
    /// <para>So the record comes up over its intro rather than over the whole fade. The outgoing record
    /// is unchanged and still fades across the whole fade. That keeps this a changeover, not a cut,
    /// even though the two curves are not mirror images. When <paramref name="upBy"/> is zero the
    /// incoming record arrives at full with no ramp at all. That is right for a record with no intro,
    /// because there is nothing to fade up under.</para>
    /// </summary>
    /// <param name="upBy">Where in the fade the incoming record reaches full, from 0 to 1. A value of 1
    /// returns the arc unchanged. That is the case for every record with an intro longer than the
    /// changeover.</param>
    public FadeArc IncomingUpBy(double upBy)
    {
        upBy = Math.Clamp(upBy, 0, 1);
        if (upBy >= 1 - 1e-6)
            return this;

        // The handles are squeezed, not redrawn. Each handle keeps its level, and only its position in
        // time is compressed. After the squeeze the record is at full, and one point records that.
        var pulled = new List<ArcPoint>(Incoming.Count + 1);
        foreach (var point in Incoming)
        {
            double t = point.T * upBy;
            if (pulled.Count > 0 && t - pulled[^1].T < 1e-9)
                pulled[^1] = new ArcPoint(pulled[^1].T, Math.Max(pulled[^1].Level, point.Level));
            else
                pulled.Add(new ArcPoint(t, point.Level));
        }

        // Full from the drop to the end of the changeover. Two points rather than one, because the last
        // squeezed handle may not itself be at full. The equal-power curve reaches it at OutBy and then
        // coasts. A line drawn from wherever it got to straight to the end would show a record still
        // climbing after its drop.
        pulled.Add(new ArcPoint(Math.Min(1, upBy + 1e-6), 1));
        pulled.Add(new ArcPoint(1, 1));

        return this with { Incoming = pulled };
    }

    /// <summary>Both levels at <paramref name="t"/> of the fade, in the unit the deck faders
    /// use.</summary>
    public Crossfader.Levels LevelsAt(double t) => new(Along(Outgoing, t), Along(Incoming, t));

    /// <summary>Both bass shelves at <paramref name="t"/> of the fade, in dB. The bottom end belongs to
    /// one record at a time. This is where it changes hands.</summary>
    public Crossfader.Levels BassDbAt(double t)
    {
        double width = Math.Max(1e-6, BassSwapEnd - BassSwapStart);
        double swap = Math.Clamp((t - BassSwapStart) / width, 0, 1);
        return new Crossfader.Levels(DeckEq.KillDb * swap, DeckEq.KillDb * (1 - swap));
    }

    /// <summary>
    /// The point where the incoming record becomes the louder of the two. The "now playing" display
    /// follows this, as <see cref="Crossfader.Balance"/> does for the classic curve. It is found by
    /// scanning the drawn lines. If the new record never overtakes the old one, the point is the end of
    /// the fade.
    /// </summary>
    public double BalanceT
    {
        get
        {
            const int Steps = 512;
            for (int i = 0; i <= Steps; i++)
            {
                double t = (double)i / Steps;
                if (Along(Incoming, t) >= Along(Outgoing, t))
                    return t;
            }

            return 1;
        }
    }

    /// <summary>
    /// One handle is moved. Along the clock it moves only as far as its neighbours allow. The end
    /// points do not move in time at all, so the arc always covers the whole fade. The level is kept
    /// between 0 and 1.
    /// </summary>
    public FadeArc MovePoint(ArcSide side, int index, double t, double level)
    {
        var points = side == ArcSide.Outgoing ? Outgoing : Incoming;
        if (index < 0 || index >= points.Count)
            return this;

        double left = index == 0 ? points[0].T : points[index - 1].T + MinGap;
        double right = index == points.Count - 1 ? points[^1].T : points[index + 1].T - MinGap;
        if (index == 0 || index == points.Count - 1)
            left = right = points[index].T;

        var moved = points.ToArray();
        moved[index] = new ArcPoint(Math.Clamp(t, left, Math.Max(left, right)), Math.Clamp(level, 0, 1));

        return side == ArcSide.Outgoing
            ? this with { Outgoing = moved }
            : this with { Incoming = moved };
    }

    /// <summary>The bass handover is moved. It stays inside the fade and keeps a minimum
    /// width.</summary>
    public FadeArc WithBassSwap(double start, double end)
    {
        start = Math.Clamp(start, 0, 1 - MinGap);
        end = Math.Clamp(end, start + MinGap, 1);
        return this with { BassSwapStart = start, BassSwapEnd = end };
    }

    /// <summary>The arc as one line for the journal: every point of both curves and the swap window. A
    /// session file therefore records exactly what was drawn.</summary>
    public override string ToString() =>
        $"out {Written(Outgoing)}; in {Written(Incoming)}; bass {BassSwapStart:F2}-{BassSwapEnd:F2}";

    private static string Written(IReadOnlyList<ArcPoint> points) =>
        string.Join(" ", points.Select(p => $"{p.T:F2}:{p.Level:F2}"));

    /// <summary>The drawn level at <paramref name="t"/>: on the straight line between the two points
    /// either side of it. Flat beyond the ends.</summary>
    private static double Along(IReadOnlyList<ArcPoint> points, double t)
    {
        if (points.Count == 0)
            return 0;
        if (t <= points[0].T)
            return Math.Clamp(points[0].Level, 0, 1);

        for (int i = 1; i < points.Count; i++)
        {
            if (t > points[i].T)
                continue;

            var from = points[i - 1];
            var to = points[i];
            double span = to.T - from.T;
            double share = span <= 0 ? 1 : (t - from.T) / span;
            return Math.Clamp(from.Level + (to.Level - from.Level) * share, 0, 1);
        }

        return Math.Clamp(points[^1].Level, 0, 1);
    }
}
