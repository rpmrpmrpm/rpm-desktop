using RPM.Core.Audio;

namespace RPM.Core.Viz;

/// <summary>
/// A plate of sand, and the music that plays it. This is the physics, with nothing that draws.
///
/// <para>
/// Ernst Chladni's experiment: scatter sand on a metal plate and sound it. The grains bounce away from
/// everywhere the plate is moving, and they pile up along the lines where it stands still. The figure
/// that appears is not a picture of the sound. It is the sound, made of the only thing that holds still
/// in it.
/// </para>
///
/// <para>
/// A plate does not hold one note. It has a whole set of modes, and a real sound wakes several at once.
/// So the displacement is the sum of its modes, each weighted by how much music is sounding it. The sand
/// settles on the zeros of that sum. A bass line and a hi-hat wake different modes than a dense chord
/// does. So the figure belongs to the balance of this spectrum and no other.
/// </para>
///
/// <list type="bullet">
/// <item>a steady passage settles into a clean figure, because the sand has time to find the lines</item>
/// <item>a change of arrangement wakes different modes and the figure dissolves and re-forms</item>
/// <item>a loud passage throws the grains into a haze before they fall back</item>
/// <item>silence leaves the sand exactly where it lies. Nothing breathes, nothing pulses, nothing
/// fades in. The last figure the music drew stays there. That is what silence looks like.</item>
/// </list>
///
/// <para>
/// It lives in Core because both heads run it, and neither can share the other's drawing. The desktop
/// puts these grains in a vertex buffer and draws them with OpenGL. The phone has no OpenGL, so it
/// draws the same array as Skia points. The physics is the visualisation. The rest is a plotter.
/// </para>
/// </summary>
public sealed class ChladniPlate
{
    /// <summary>
    /// The plate's modes, as (n, m) pairs, ordered by n*n + m*m. A plate's own resonances order
    /// themselves this way. So the list runs from the lowest note the plate can hold to the highest.
    /// Each mode is given the band of the spectrum that would sound it.
    /// </summary>
    private static readonly (int N, int M)[] Modes =
    [
        (2, 3), (3, 4), (2, 5), (4, 5), (3, 6), (4, 7), (5, 8), (6, 9),
    ];

    // cos and sin of n*pi*x, sampled once over the plate. The modes are whole numbers, so these never
    // change. The per-frame cost of the field is table lookups and multiply-adds. There is no
    // trigonometry at all during a frame, which keeps eight modes cheap.
    private const int Table = 2048;

    // How the sand travels. This decides whether the figure arrives steadily or rushes and then crawls.
    //
    // A hop has a FIXED size. It does not scale with how hard the plate shakes a grain. If it did, grains
    // far from a node would race in while grains almost there would crawl. The whole figure would be
    // exponential: nearly finished in seconds, then minutes of invisible polishing. With a fixed hop, the
    // time a grain takes to arrive is simply how far it has to go.
    //
    // Rate is in plate widths per second. Aim is how much of each hop is pointed at the quiet. Scatter is
    // how much is thrown at random.
    private const float Rate = 0.0024f;
    private const float Aim = 0.55f;
    private const float Scatter = 1.30f;

    // What counts as arriving. It is a DISTANCE to the nodal line, not a displacement threshold. A grain
    // is home when it is within this distance of the line, which is |u| / |grad u| away from it. As a
    // displacement threshold, the same number would mean a different distance wherever the plate is
    // steeper or flatter. On a plate holding eight modes at once, most of the surface would then count
    // as still before anything has moved at all.
    private const float Capture = 0.008f;

    private readonly float[] _cos = new float[Modes.Length * 2 * Table];
    private readonly float[] _sin = new float[Modes.Length * 2 * Table];

    // How strongly each mode is sounded right now. This is the figure. The sand settles on the zeros of
    // the sum of the modes, so the shape is the balance of the whole spectrum.
    private readonly float[] _weight = new float[Modes.Length];

    private readonly ShaderAudioTexture _audio;
    private readonly float[] _spec = new float[ShaderAudioTexture.Width * ShaderAudioTexture.Height];

    // Per grain: how well the plate can aim it. Spread around 1, with a light tail that never settles.
    private readonly float[] _light;
    private readonly Random _rng;

    private int _phase;   // which half of the sand moves this frame

    /// <param name="grains">How much sand. The desktop throws sixty thousand grains. A phone runs the
    /// same arithmetic on a smaller budget, and the figure is the same figure with less sand.</param>
    /// <param name="frameSize">Samples per analysis frame, matching whatever supplies them.</param>
    /// <param name="seed">Fixed by default, so the same plate is thrown the same way every run.</param>
    public ChladniPlate(int grains = 60000, int frameSize = 2048, int seed = 20260726)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(grains, 1);

        Grains = grains;
        Positions = new float[grains * 2];
        _light = new float[grains];
        _rng = new Random(seed);
        _audio = new ShaderAudioTexture(frameSize);

        BuildTables();
        Seed();
    }

    /// <summary>How many grains are on the plate.</summary>
    public int Grains { get; }

    /// <summary>Grain positions in plate coordinates: -1 to 1 on both axes, with x and y interleaved. A
    /// caller draws this array. The desktop uploads it as a vertex buffer. The phone walks it and draws
    /// Skia points.</summary>
    public float[] Positions { get; }

    /// <summary>How hard the plate is being sounded, 0 at silence. At zero the sand does not move at all.
    /// This is intended, and it is not an optimisation.</summary>
    public float Drive { get; private set; }

    /// <summary>Samples each mode's cos and sin across the plate, once. The modes are fixed whole
    /// numbers, so this is the only trigonometry the plate does.</summary>
    private void BuildTables()
    {
        for (int m = 0; m < Modes.Length; m++)
        {
            for (int axis = 0; axis < 2; axis++)
            {
                float k = (axis == 0 ? Modes[m].N : Modes[m].M) * MathF.PI;
                int at = (m * 2 + axis) * Table;
                for (int i = 0; i < Table; i++)
                {
                    float x = i / (float)(Table - 1) * 2f - 1f;
                    (float sin, float cos) = MathF.SinCos(k * x);
                    _cos[at + i] = cos;
                    _sin[at + i] = sin;
                }
            }
        }
    }

    /// <summary>Throws the sand on the plate once, by hand. It is never thrown again. Wherever the music
    /// leaves the grains is where the next passage starts.</summary>
    private void Seed()
    {
        // The sand is scattered by hand, not spread by a rule. It lands thicker in some places than
        // others. That unevenness never fully washes out. It is why one arm of a figure ends up heavier
        // than the arm that mirrors it. A perfectly uniform pour gives a plate that looks printed.
        double tiltX = _rng.NextDouble() * 2.0 - 1.0;
        double tiltY = _rng.NextDouble() * 2.0 - 1.0;
        double swirl = _rng.NextDouble() * Math.Tau;

        for (int i = 0; i < Grains; i++)
        {
            double x, y;
            while (true)
            {
                x = _rng.NextDouble() * 2.0 - 1.0;
                y = _rng.NextDouble() * 2.0 - 1.0;
                // Rejection sampling against a gentle, low-frequency density: a slope across the plate
                // and one soft heap somewhere on it.
                double d = 0.62 + 0.20 * (x * tiltX + y * tiltY)
                         + 0.18 * Math.Cos(x * 2.1 + swirl) * Math.Cos(y * 1.7 - swirl);
                if (_rng.NextDouble() < d) break;
            }
            Positions[i * 2] = (float)x;
            Positions[i * 2 + 1] = (float)y;
            // Grains are not identical. Most differ a little in how well the plate can aim them. That
            // makes a ridge pile unevenly instead of drawing a smooth curve. Two grains in a hundred are
            // light enough never to settle. Those are the dust that keeps moving.
            _light[i] = _rng.NextDouble() < 0.02 ? 0.16f : 0.75f + (float)_rng.NextDouble() * 0.55f;
        }
    }

    /// <summary>Takes the plate's note from the music: which modes it is sounding, and how hard.</summary>
    /// <param name="frame">The samples being heard. Pass an empty span for silence.</param>
    public void Listen(ReadOnlySpan<float> frame)
    {
        _audio.Update(frame);

        var d = _audio.Data;
        for (int i = 0; i < d.Length; i++)
        {
            float target = d[i];
            float k = target > _spec[i] ? 0.40f : 0.06f;   // rise with the note, fall like a plate ringing
            _spec[i] += (target - _spec[i]) * k;
        }

        // Every mode is weighted by the band of the spectrum that would sound it. A song with a bass
        // line and a hi-hat sounds two modes and settles on the zeros of their sum. A dense chord sounds
        // several modes and draws a figure that no other chord draws.
        float total = 0f;
        for (int mi = 0; mi < Modes.Length; mi++)
        {
            // Bands are spaced on a log scale up the spectrum, in the same order as the modes. So the
            // lowest mode answers to the lowest music, and the busiest mode answers to the top end.
            int lo = (int)(2.0 * Math.Pow(150.0, mi / (double)Modes.Length));
            int hi = (int)(2.0 * Math.Pow(150.0, (mi + 1) / (double)Modes.Length));
            if (hi <= lo) hi = lo + 1;
            if (hi > 320) hi = 320;

            float band = 0f;
            for (int i = lo; i < hi; i++) band += _spec[i];
            band /= (hi - lo) * 255f;
            band = MathF.Pow(band, 0.70f);   // A curve, so a quiet mode still shapes the figure

            // Eased, not snapped. A plate cannot change which modes it holds in one frame. This is why
            // one figure dissolves into the next, rather than cutting to it.
            float k = band > _weight[mi] ? 0.10f : 0.035f;
            _weight[mi] += (band - _weight[mi]) * k;
            total += _weight[mi];
        }

        // Normalised. The figure is the balance between the modes. How loud the passage is sets how hard
        // the sand is thrown. It never sets what shape the sand makes, just as on a real plate.
        if (total > 0.0001f)
            for (int mi = 0; mi < Modes.Length; mi++)
                _weight[mi] /= total;

        // Drive: how hard the plate is being hit. Fast attack, slow release. It reaches zero in silence,
        // so the sand really stops instead of simmering forever.
        float level = 0f;
        for (int i = 2; i < 300; i++) level += _spec[i];
        level /= 298 * 255f;

        float want = MathF.Min(1f, MathF.Pow(level * 3.2f, 0.75f));
        Drive += (want - Drive) * (want > Drive ? 0.30f : 0.04f);
        if (Drive < 0.004f) Drive = 0f;
    }

    /// <summary>
    /// One step of the sand. Every grain runs downhill on the square of the plate's displacement. That
    /// means it moves away from wherever the plate is moving, and it stops where the plate is still. The
    /// kick it gets on the way is proportional to how hard the plate is driven. So loud music scatters
    /// the figure before it re-forms, and quiet music lets it sharpen.
    /// </summary>
    public void Settle(float dt)
    {
        if (Drive <= 0f || dt <= 0f)
            return;   // silence: the sand is left exactly where the last note put it

        // Half the sand moves each frame, alternating, at twice the step. Sixty thousand grains is a lot
        // of work to do sixty times a second on the thread that also owns the window. A grain that takes
        // one step of 2dt ends up in the same place as after two steps of dt. The motion is overdamped,
        // and the throw is random anyway. So nothing about the figure changes, and the cost halves.
        _phase ^= 1;
        dt *= 2f;

        for (int i = _phase; i < Grains; i += 2)
        {
            int k = i * 2;
            float x = Positions[k], y = Positions[k + 1];

            // The plate's displacement is the SUM of every mode it is holding, each weighted by how
            // much music is sounding it:
            //   u = sum_k w_k [ cos(n_k pi x) cos(m_k pi y) - cos(m_k pi x) cos(n_k pi y) ]
            // The sand collects on the zeros of that sum. So the figure belongs to this music, not to a
            // mode number. Change the balance of the spectrum and every line moves.
            float fx = (x + 1f) * 0.5f * (Table - 1);
            float fy = (y + 1f) * 0.5f * (Table - 1);
            int ix = (int)fx, iy = (int)fy;
            if (ix < 0) ix = 0; else if (ix > Table - 2) ix = Table - 2;
            if (iy < 0) iy = 0; else if (iy > Table - 2) iy = Table - 2;
            float tx = fx - ix, ty = fy - iy;

            float u = 0f, dux = 0f, duy = 0f;
            for (int m = 0; m < Modes.Length; m++)
            {
                float w = _weight[m];
                if (w < 0.004f)
                    continue;   // a mode nothing is sounding costs nothing

                int ax = m * 2 * Table, bx = (m * 2 + 1) * Table;
                float can = Lerp(_cos, ax + ix, tx), sanv = Lerp(_sin, ax + ix, tx);   // cos/sin(n pi x)
                float cam = Lerp(_cos, ax + iy, ty), samv = Lerp(_sin, ax + iy, ty);   // and the same at y
                float cbn = Lerp(_cos, bx + ix, tx), sbnv = Lerp(_sin, bx + ix, tx);   // cos/sin(m pi x)
                float cbm = Lerp(_cos, bx + iy, ty), sbmv = Lerp(_sin, bx + iy, ty);

                float n = Modes[m].N * MathF.PI, mm = Modes[m].M * MathF.PI;
                u += w * (can * cbm - cbn * cam);
                dux += w * (-n * sanv * cbm + mm * sbnv * cam);
                duy += w * (-mm * can * sbmv + n * cbn * samv);
            }

            // A grain is thrown while the plate is moving under it. It stops when it lands somewhere the
            // plate is not moving. Both cases matter:
            //
            //   still far from a nodal line: throw it again, a fixed distance, mostly at random,
            //                                with a little of the hop aimed at the quiet
            //   within a ridge of one:       the plate barely moves here. It has arrived, and stays.
            //
            // Nothing sorts the grains, and nothing draws the figure. The sand builds up because arriving
            // is the only state a grain stays in. It arrives at a steady rate because a hop has a fixed
            // size. A grain twice as far from a nodal line takes twice as long.
            float light = _light[i];

            float slope = MathF.Sqrt(dux * dux + duy * duy) + 1e-5f;
            if (MathF.Abs(u) > Capture * slope)
            {
                float gx = u * dux, gy = u * duy;
                float g = MathF.Sqrt(gx * gx + gy * gy) + 1e-5f;
                float hop = Rate * dt * Drive;

                // Aimed at the quieter plate. This is only a bias on a throw that is mostly random. That
                // is why the ridges come out soft rather than sharp.
                x -= gx / g * hop * Aim * light;
                y -= gy / g * hop * Aim * light;

                x += (float)(_rng.NextDouble() - 0.5) * hop * Scatter;
                y += (float)(_rng.NextDouble() - 0.5) * hop * Scatter;
            }

            // A hard-driven plate has grains hopping over the whole of it, settled ridges included. So a
            // loud passage looks restless, and it can knock arrived sand back into play. The term is
            // squared, so it only matters for loud passages. It is never enough to stop the figure forming.
            float restless = Drive * Drive * 0.006f * dt;
            x += (float)(_rng.NextDouble() - 0.5) * restless;
            y += (float)(_rng.NextDouble() - 0.5) * restless;

            // The plate has edges. Sand thrown off one edge is poured back on somewhere at random.
            // Bouncing it off the rim would build a line of grains around the border. The physics never
            // puts that line there.
            if (x < -1f || x > 1f || y < -1f || y > 1f)
            {
                x = (float)(_rng.NextDouble() * 2.0 - 1.0);
                y = (float)(_rng.NextDouble() * 2.0 - 1.0);
            }

            Positions[k] = x;
            Positions[k + 1] = y;
        }
    }

    /// <summary>One linear sample out of a mode table.</summary>
    private static float Lerp(float[] table, int i, float t) => table[i] + (table[i + 1] - table[i]) * t;
}
