namespace RPM.Core.Viz;

/// <summary>
/// Where the figures of <see cref="RoomStyle.Yang"/>, <see cref="RoomStyle.Lava"/> and
/// <see cref="RoomStyle.Alchemy"/> are this frame, and how large and bright they are.
///
/// <para>These numbers are the same for every pixel, so they are worked out once per frame here, on the
/// CPU, and handed to the shader as arrays of vec4 (<see cref="RoomStyleSource"/>). The shader spends
/// nothing per pixel on them. Nothing here allocates per frame.</para>
///
/// <para>Each style's main motion runs on a clock of its own (<see cref="Spin"/>, <see cref="Flow"/>,
/// <see cref="Drift"/>). <see cref="Advance"/> moves each clock on faster while its level is high. So the
/// music speeds the figures up without making them jump. A level that scaled the time itself would throw
/// every figure forward the moment the bass came in. A room that is not moving is advanced by nothing and
/// keeps its pose. Slow drifts that the music does not touch read the room's own clock instead.</para>
///
/// <para>The Android TV app draws the same figures with the same numbers (its RoomFigures.kt). Change
/// both together, so the two apps draw one picture.</para>
/// </summary>
public sealed class RoomFigures
{
    /// <summary>Echoes behind each of Yang's big balls.</summary>
    public const int BigEchoes = 4;

    /// <summary>Echoes behind each of Yang's small balls.</summary>
    public const int SmallEchoes = 8;

    /// <summary>Yang's big balls and their echoes: two of 1 + <see cref="BigEchoes"/>.</summary>
    public const int YangBig = 2 * (BigEchoes + 1);

    /// <summary>Yang's small balls and their echoes: two of 1 + <see cref="SmallEchoes"/>.</summary>
    public const int YangSmall = 2 * (SmallEchoes + 1);

    /// <summary>Lava's blobs.</summary>
    public const int LavaBlobs = 7;

    /// <summary>Alchemy's ribbons.</summary>
    public const int Ribbons = 5;

    /// <summary>How fast Yang turns with no music, in radians a second: one turn in about 20 seconds.</summary>
    public const double YangSpin = 0.32;

    /// <summary>How much faster the bass at full turns Yang.</summary>
    public const double YangSpinBass = 0.9;

    /// <summary>How fast Yang turns while music plays, as a share of the speed the levels give it. At the
    /// full speed the balls race.</summary>
    public const double YangMusicPace = 0.4;

    /// <summary>How much faster Yang's small balls turn than the big ones, so they keep passing them.</summary>
    public const double SmallRate = 1.6;

    /// <summary>How much wider than high Yang's orbits are, so they use the width of the screen.</summary>
    public const double Wide = 1.3;

    /// <summary>Yang's big balls, each followed by its echoes: x, y, radius, weight.</summary>
    public float[] Big { get; } = new float[YangBig * 4];

    /// <summary>Yang's small balls, each followed by its echoes, as <see cref="Big"/>.</summary>
    public float[] Small { get; } = new float[YangSmall * 4];

    /// <summary>Yang's blurred middle ball: x, y, radius, and one number that is not used.</summary>
    public float[] Centre { get; } = new float[4];

    /// <summary>Lava's blobs: x, y, radius, and how far its colour is from the tint toward the glow.</summary>
    public float[] Blobs { get; } = new float[LavaBlobs * 4];

    /// <summary>Lava's two pools: the floor's line, its thickness, the top's line, its thickness.</summary>
    public float[] Pools { get; } = new float[4];

    /// <summary>Lava's background ball: x, y, radius, and the phase of the pools' ripple.</summary>
    public float[] Back { get; } = new float[4];

    /// <summary>Alchemy's ribbons, each a curve across the screen: where it crosses the left edge, its
    /// tilt, how far its sine moves it, and 2 pi times its sine's cycles across the screen.</summary>
    public float[] RibbonA { get; } = new float[Ribbons * 4];

    /// <summary>Alchemy's ribbons: the phase of the sine, the phase of its second sine, the
    /// half-thickness, and the swell.</summary>
    public float[] RibbonB { get; } = new float[Ribbons * 4];

    /// <summary>Alchemy's ribbons: the phase of the swell, the weight, the filament's weight, and the
    /// colour (0 the tint, 1 the glow, 2 the shade).</summary>
    public float[] RibbonC { get; } = new float[Ribbons * 4];

    /// <summary>Yang's turn, in radians.</summary>
    public double Spin { get; private set; }

    /// <summary>Lava's clock, in seconds of lava.</summary>
    public double Flow { get; private set; }

    /// <summary>Alchemy's clock, in seconds of drifting.</summary>
    public double Drift { get; private set; }

    /// <summary>
    /// Moves the three clocks on by <paramref name="dt"/> seconds, at the speed the levels give them.
    /// Pass 0 for a room that is held still.
    ///
    /// <para>While music plays, Yang turns at <see cref="YangMusicPace"/> of the speed the levels give
    /// it. The pace eases in with the music's level, so silence keeps the plain drift and nothing
    /// jumps.</para>
    /// </summary>
    public void Advance(double dt, float bass, float mid)
    {
        double music = Math.Clamp((bass + mid) * 5.0, 0.0, 1.0);
        Spin += dt * (YangSpin + bass * YangSpinBass) * (1.0 - (1.0 - YangMusicPace) * music);
        Flow += dt * (1.0 + bass * 0.6);
        Drift += dt * (1.0 + mid * 0.8);
    }

    /// <summary>
    /// Yang at <paramref name="t"/> seconds of the room's clock.
    ///
    /// <para>The big pair stands opposite each other on an orbit 0.24 of the height out. The small pair
    /// stands on one 0.39 out, turning <see cref="SmallRate"/> times as fast and a quarter turn apart, so
    /// the small ones keep passing the big ones. Both orbits are stretched <see cref="Wide"/> across. The
    /// big balls swell with the bass, as the original's figures do (1 + bass x 0.62). The small ones swell
    /// with the air (1 + air x 0.85), and the middle ball with the middle.</para>
    ///
    /// <para>The tail is not a picture kept from frame to frame. That would be a second full pass and a
    /// read of the last frame. It is more balls per ball, each further back along the orbit, smaller and
    /// lighter, summed into the same field, so they melt into one tail. The echoes are spaced by length
    /// along the orbit, not by time, so a ball that turns faster does not spread its tail into beads. A
    /// small ball's echoes are 0.045 of the height apart against its radius of 0.07, close enough to fill
    /// one smooth streak. A big ball's are 0.09 apart against 0.15. The louder a ball's level, the longer
    /// its tail: the big ones' spacing times (1 + bass x 0.6), the small ones' times (1 + air x 0.4). Even
    /// at full air that stays below the radius of the last, smallest echo.</para>
    /// </summary>
    public void Yang(double t, float bass, float mid, float air)
    {
        double bigOrbit = 0.24 + 0.03 * Math.Sin(t * 0.23);
        double smallOrbit = 0.39 + 0.04 * Math.Sin(t * 0.31 + 1.0);
        double bigR = 0.15 * (1.0 + bass * 0.62);
        double smallR = 0.07 * (1.0 + air * 0.85);
        for (int ball = 0; ball < 2; ball++)
        {
            Trail(Big, ball * (BigEchoes + 1), Spin + ball * Math.PI, bigOrbit, bigR,
                0.09 * (1.0 + bass * 0.6), BigEchoes, 0.12, 0.6);
            Trail(Small, ball * (SmallEchoes + 1), Spin * SmallRate + Math.PI / 2 + ball * Math.PI, smallOrbit, smallR,
                0.045 * (1.0 + air * 0.4), SmallEchoes, 0.06, 0.8);
        }
        Put(Centre, 0, 0.0, 0.0, 0.30 * (1.0 + mid * 0.35), 0.0);
    }

    /// <summary>One ball at <paramref name="angle"/> on an orbit <paramref name="distance"/> out, written
    /// from <paramref name="first"/>, and its <paramref name="echoes"/> behind it. Each echo is
    /// <paramref name="spacing"/> further back along the stretched orbit, <paramref name="shrink"/> of the
    /// radius smaller, and <paramref name="fade"/> times as heavy as the one before. The step in angle is
    /// the spacing divided by how fast the orbit's length grows with the angle where the echo stands.</summary>
    private static void Trail(float[] into, int first, double angle, double distance, double radius,
        double spacing, int echoes, double shrink, double fade)
    {
        double a = angle;
        for (int k = 0; k <= echoes; k++)
        {
            Put(into, first + k, Math.Cos(a) * distance * Wide, Math.Sin(a) * distance,
                radius * (1.0 - shrink * k), Math.Pow(fade, k));
            double s = Math.Sin(a) * Wide;
            double c = Math.Cos(a);
            a -= spacing / (distance * Math.Sqrt(s * s + c * c));
        }
    }

    /// <summary>
    /// Lava. Seven blobs spread across the width. Each rises and sinks on a sine of its own period (24 to
    /// 66 seconds), bent so it stays longer at the floor and the top, as a lamp's wax does, and drifts a
    /// little sideways. Blobs of different periods pass each other, melt into one and part again. At the
    /// ends they melt into the pools. They swell with the bass, as the original's figures do. The
    /// background ball wanders slowly and swells with the middle (1 + mid x 0.45).
    /// </summary>
    public void Lava(float bass, float mid)
    {
        double f = Flow;
        for (int i = 0; i < LavaBlobs; i++)
        {
            double period = 24.0 + 7.0 * (i * 3 % LavaBlobs);
            double s = Math.Sin(f * 2 * Math.PI / period + i * 2.39996);
            double y = Math.Sign(s) * Math.Pow(Math.Abs(s), 0.85) * 0.44;
            double x = -0.70 + i * (1.40 / (LavaBlobs - 1)) + 0.12 * Math.Sin(f * 0.071 + i * 1.7);
            double r = (0.09 + 0.03 * Math.Sin(f * 0.13 + i * 2.3)) * (1.0 + bass * 0.62);
            Put(Blobs, i, x, y, r, (i * 0.41 + 0.15) % 1.0);
        }
        Put(Pools, 0, -0.565, 0.11 * (1.0 + bass * 0.3), 0.575, 0.07 * (1.0 + bass * 0.2));
        Put(Back, 0, 0.20 * Math.Sin(f * 0.043), -0.04 + 0.07 * Math.Sin(f * 0.031 + 1.0),
            0.40 * (1.0 + mid * 0.45), f * 0.21);
    }

    /// <summary>
    /// Alchemy: the five ribbons of the RPaMp skin's background picture (background-rpamp.jpg, drawn by
    /// make_rpamp_ground.py), with the script's numbers. So a room that is not moving is that picture.
    ///
    /// <para>Here they move. Each ribbon's phase swings slowly back and forth along it (1.1 radians, over
    /// 70 to 40 seconds of <see cref="Drift"/>), so its curve flows. Where it crosses the left edge rises
    /// and falls by 0.025 of the height, its sine deepens and flattens by a quarter, and its swell slides
    /// along it. The ribbons swing and do not run on, so the picture stays the skin's composition and two
    /// ribbons never lie on each other for long. At a <see cref="Drift"/> of 0 every swing is at its
    /// middle, so a new room is the picture itself.</para>
    ///
    /// <para>The music moves them gently. The bright main ribbon (the script's second, which enters from
    /// the left at about 40% of the height and rises to the right) takes the bass in its weight
    /// (1 + bass x 0.25) and its thickness (1 + bass x 0.15). Its filament flares with the air, which has
    /// the quickest attack of the three levels, so it catches the transients. The ribbon that crosses it
    /// hears half the bass, the two below it the middle, and the faint one along the top the air. The
    /// middle also hurries <see cref="Drift"/>. The two bright ribbons wear the tint, and the others the
    /// glow, the shade and the glow.</para>
    /// </summary>
    public void Alchemy(float bass, float mid, float air)
    {
        double d = Drift;
        for (int i = 0; i < Ribbons; i++)
        {
            double level = i switch
            {
                0 => bass * 0.5f,
                1 => bass,
                2 or 3 => mid,
                _ => air,
            };
            int k = i * 6;
            double y0 = Bands[k];
            double tilt = Bands[k + 1];
            double amp = Bands[k + 2];
            double freq = Bands[k + 3];
            double phase0 = Bands[k + 4];
            double width = Bands[k + 5];
            double phase = phase0 - 1.1 * Math.Sin(d * (0.09 + 0.02 * i));
            Put(RibbonA, i,
                y0 + 0.025 * Math.Sin(d * (0.031 + 0.006 * i)),
                tilt,
                amp * (1.0 + 0.25 * Math.Sin(d * (0.043 + 0.007 * i))),
                2 * Math.PI * freq);
            Put(RibbonB, i, phase, phase * 1.7, width * (1.0 + level * 0.15), Swell[i]);
            Put(RibbonC, i,
                phase0 * 0.6 + 0.5 * Math.Sin(d * (0.037 + 0.009 * i)),
                Weight[i] * (1.0 + level * 0.25),
                0.22 + (i == 1 ? air * 0.5 : 0.0),
                Colour[i]);
        }
    }

    private static void Put(float[] into, int i, double a, double b, double c, double d)
    {
        into[i * 4] = (float)a;
        into[i * 4 + 1] = (float)b;
        into[i * 4 + 2] = (float)c;
        into[i * 4 + 3] = (float)d;
    }

    /// <summary>make_rpamp_ground.py's bands, six numbers to a ribbon: y0 (from the top, as a share of the
    /// height), tilt, amp, freq, phase and width. Its swell and weight are in <see cref="Swell"/> and
    /// <see cref="Weight"/>.</summary>
    private static readonly double[] Bands =
    [
        0.18, 0.30, 0.10, 0.9, 0.0, 0.20,
        0.44, -0.22, 0.13, 0.6, 2.1, 0.16,
        0.72, 0.18, 0.09, 1.2, 4.0, 0.22,
        1.02, -0.26, 0.11, 0.8, 1.2, 0.18,
        -0.06, 0.16, 0.08, 1.5, 3.3, 0.14,
    ];

    private static readonly double[] Swell = [0.55, 0.70, 0.45, 0.60, 0.50];
    private static readonly double[] Weight = [1.00, 0.90, 0.85, 0.70, 0.55];

    /// <summary>Each ribbon's colour: 0 the tint, 1 the glow, 2 the shade.</summary>
    private static readonly double[] Colour = [0.0, 0.0, 1.0, 2.0, 1.0];
}
