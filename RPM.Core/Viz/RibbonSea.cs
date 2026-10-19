using RPM.Core.Audio;

namespace RPM.Core.Viz;

/// <summary>
/// The ribbon sea: fifteen contour lines of one rolling surface. They are seen nearly edge-on through a
/// real perspective camera. Wrinkles are born at the left from the music of the moment, and they travel
/// right along each line, like a wave sent down a rope.
///
/// <para>
/// This is the desktop's <c>RibbonStage</c> vertex shader, moved off the GPU. The desktop can keep it
/// there, since it has <c>GLCanvasElement</c> and real geometry. But Uno has no iOS build of that, and a
/// phone's answer is Skia. Fifteen lines of 150 points is 2,250 projections per frame. That is cheap on a
/// CPU. The expensive option would be a fragment shader that solves the same projection for every pixel.
/// </para>
///
/// <para>
/// So the arithmetic below is the shader's, line for line. The head strokes the polylines that it hands
/// back. Keeping it here, rather than in the phone head, lets it be tested without a surface. It is the
/// same split that <see cref="ChladniPlate"/> uses.
/// </para>
/// </summary>
public sealed class RibbonSea
{
    /// <summary>Contour lines, low band at the front.</summary>
    public const int Lines = 15;

    /// <summary>Points along each line's length.</summary>
    public const int Cols = 150;

    /// <summary>Floats a <see cref="Trace"/> buffer needs: an x and a y per point of every line.</summary>
    public const int TraceLength = Lines * Cols * 2;

    // The scrolling record of recent band energy. This is what makes the wrinkles travel. Columns are
    // time, with NOW at column 0. Rows are frequency, from bass at row 0 to treble at the top. A point at
    // u reads column u, so its amplitude is the music from one travel-time ago. A crest born at the left
    // edge flows right over the length of the history.
    private const int HistoryColumns = 100;
    private const int HistoryBands = 16;

    private readonly ShaderAudioTexture _audio;
    private readonly float[] _spec = new float[ShaderAudioTexture.Width * ShaderAudioTexture.Height];
    private readonly float[] _history = new float[HistoryBands * HistoryColumns];

    private float _swell;
    private double _swellHold;

    /// <param name="frameSize">Samples per analysis frame, in the form that
    /// <see cref="ShaderAudioTexture"/> expects.</param>
    public RibbonSea(int frameSize = 2048) => _audio = new ShaderAudioTexture(frameSize);

    /// <summary>
    /// The sea's own dynamics: how high the whole swell is riding, 0 at rest. Fast attack, a short hold
    /// at the peak, then a slow settle. It is deliberately sluggish, because this is the motion of an
    /// ocean, and an ocean does not twitch. The wrinkles carry everything quick.
    /// </summary>
    public float Swell => _swell;

    /// <summary>
    /// Takes a frame of played samples and moves the sea on. The spectrum is smoothed per bin. The
    /// broadband swell gets an envelope. Every band pushes this moment's energy into the left column of
    /// its own history row. Called at about 40 Hz. A sea does not change within 25 milliseconds, and the
    /// picture animates off the clock in between.
    /// </summary>
    /// <param name="frame">Played samples, or empty for silence.</param>
    /// <param name="now">Seconds on the stage's own clock; only the swell's hold reads it.</param>
    public void Listen(ReadOnlySpan<float> frame, double now)
    {
        _audio.Update(frame);

        var data = _audio.Data;
        for (int i = 0; i < data.Length; i++)
        {
            float target = data[i];
            float k = target > _spec[i] ? 0.35f : 0.10f;   // rise fast, fall slow
            _spec[i] += (target - _spec[i]) * k;
        }

        // The overall swell, from a wide band that is already smoothed.
        const int lo = 2, hi = 220;
        float sum = 0f;
        for (int i = lo; i < hi; i++) sum += _spec[i];
        float level = sum / ((hi - lo) * 255f);

        if (level > _swell)
        {
            _swell += (level - _swell) * 0.35f;
            _swellHold = now + 0.35;
        }
        else if (now >= _swellHold)
        {
            _swell += (level - _swell) * 0.010f;
        }

        Scroll();
    }

    /// <summary>Every band's row shifts one column to the right, and takes this moment's energy in the
    /// left column. Bands are log-spaced, with bass at row 0.</summary>
    private void Scroll()
    {
        for (int band = 0; band < HistoryBands; band++)
        {
            int lo = (int)(2.0 * Math.Pow(200.0, band / (double)HistoryBands));
            int hi = (int)(2.0 * Math.Pow(200.0, (band + 1) / (double)HistoryBands));
            if (hi <= lo) hi = lo + 1;
            if (hi > 500) hi = 500;

            float sum = 0f;
            for (int i = lo; i < hi; i++) sum += _spec[i];

            int row = band * HistoryColumns;
            Array.Copy(_history, row, _history, row + 1, HistoryColumns - 1);
            _history[row] = Math.Clamp(sum / ((hi - lo) * 255f), 0f, 1f);
        }
    }

    /// <summary>
    /// Projects every line into normalised device coordinates (x and y in -1..1, y up) for a frame of
    /// the given shape.
    /// </summary>
    /// <param name="time">Seconds since the stage came up.</param>
    /// <param name="aspect">The frame's height over its width. The projection is scaled by the height on
    /// both axes. So a wider frame is a wider window onto the same sea, not a stretched one.</param>
    /// <param name="xy">Filled with <see cref="TraceLength"/> floats: line by line, point by point, x then
    /// y. The caller reuses this array from frame to frame.</param>
    public void Trace(double time, float aspect, float[] xy)
    {
        ArgumentNullException.ThrowIfNull(xy);
        if (xy.Length < TraceLength)
            throw new ArgumentException($"needs {TraceLength} floats", nameof(xy));

        float t = (float)time;

        // Breathing: the resting amplitude swells and relaxes on two slow sine waves with unrelated
        // periods. The roll's own pace eases with a third, so the sea never travels at a constant rate.
        float breath = 0.60f + 0.25f * MathF.Sin(t * 0.42f) + 0.15f * MathF.Sin(t * 0.27f + 1.3f);
        float seaTime = t * 0.2f + 0.18f * MathF.Sin(t * 0.33f);
        float amplitude = 0.16f * breath + _swell * 1.25f;

        // The camera is a pinhole. It is raised above the sea and pitched down 20 degrees. So the swell
        // reads as height, and the bands recede toward a horizon.
        const float focal = 2.6f;
        float pitch = 20f * MathF.PI / 180f;
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        const float dist = 3.2f;
        float camY = dist * sp, camZ = -dist * cp;

        // How much ocean a frame of this shape gets. The desktop draws into a square and lets a wide
        // window crop the sides. So its lines always leave the frame. Scaling by height would keep the
        // perspective correct, but the line ends would land a few per cent inside the edges of a landscape
        // phone. A line that tapers away in mid-air looks like the sea has run out. So a wider window is
        // given a longer stretch of sea, not a stretched one.
        float reach = Math.Max(1f, 0.62f / Math.Max(aspect, 0.01f));

        int k = 0;
        for (int line = 0; line < Lines; line++)
        {
            float v = line / (float)(Lines - 1);
            float wz = (v - 0.5f) * 2.2f;

            // Wider toward the back. Otherwise perspective pulls the far lines' ends inside the frame, and
            // the end fade shows up as a black gap where the sea should carry on.
            float span = (3.0f + v * 3.4f) * reach;

            float fcoord = 1.0f - v;                     // front reads treble, back reads bass
            float ramp = 0.06f + (0.15f - 0.06f) * v;    // treble small at the front, bass big at the back

            for (int c = 0; c < Cols; c++)
            {
                float u = c / (float)(Cols - 1);
                float wx = (u - 0.5f) * span;

                float wy = Sea(wx, wz, seaTime) * amplitude;

                // One coherent travelling field is used for every row. All rows have the same wavelength,
                // with a small per-row phase shift. So neighbouring crests line up into a wavefront instead
                // of crossing. Only the amplitude differs per row. It is that row's band, delayed by
                // how far along the line the point is.
                float born = History(u, fcoord);
                float carrier = MathF.Sin(wx * 13.0f + wz * 1.6f - t * 6.0f)
                              + MathF.Sin(wx * 20.0f + wz * 2.4f - t * 8.4f) * 0.5f;
                wy += carrier * (0.003f + born * ramp);

                float dy = wy - camY;
                float dz = wz - camZ;                    // always positive: the sea is in front
                float cy = dy * cp + dz * sp;
                float cz = -dy * sp + dz * cp;
                if (cz < 0.05f) cz = 0.05f;              // never behind the lens

                xy[k++] = wx * focal / cz * aspect;
                xy[k++] = cy * focal / cz;
            }
        }
    }

    /// <summary>
    /// How bright a line is drawn. The front lines lead, and everything lifts when the music does. The
    /// fade at the ends of a line belongs to the head. It is a stroke's business, not the sea's.
    /// </summary>
    public float Brightness(int line)
    {
        float v = Math.Clamp(line, 0, Lines - 1) / (float)(Lines - 1);
        return (0.45f + 0.55f * (1.0f - v)) * (0.8f + 0.5f * _swell);
    }

    /// <summary>An open sea: three long directional rollers, summed at low wavenumbers. So a line carries
    /// a couple of flowing curves, not a field of small bumps. The mean height is zero.</summary>
    private static float Sea(float x, float z, float t)
    {
        float h = 0f;
        h += 0.50f * MathF.Sin(Dot(1.00f, 0.30f, x, z) * 1.1f - t * 0.55f);
        h += 0.28f * MathF.Sin(Dot(0.60f, -0.80f, x, z) * 1.9f - t * 0.85f);
        h += 0.16f * MathF.Sin(Dot(-0.35f, 1.00f, x, z) * 3.2f - t * 1.15f);
        return h;
    }

    /// <summary>The dot product of a point with a normalised direction. This is the shader's
    /// <c>dot(normalize(d), p)</c>.</summary>
    private static float Dot(float dx, float dz, float x, float z)
    {
        float len = MathF.Sqrt(dx * dx + dz * dz);
        return (dx / len) * x + (dz / len) * z;
    }

    /// <summary>Reads the band history the way the shader samples its texture. It is normalised on both
    /// axes, linearly filtered, and clamped at the edges.</summary>
    private float History(float u, float band)
    {
        float fx = Math.Clamp(u, 0f, 1f) * (HistoryColumns - 1);
        float fy = Math.Clamp(band, 0f, 1f) * (HistoryBands - 1);

        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, HistoryColumns - 1);
        int y1 = Math.Min(y0 + 1, HistoryBands - 1);
        float tx = fx - x0, ty = fy - y0;

        float a = _history[y0 * HistoryColumns + x0];
        float b = _history[y0 * HistoryColumns + x1];
        float c = _history[y1 * HistoryColumns + x0];
        float d = _history[y1 * HistoryColumns + x1];

        return (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
    }
}
