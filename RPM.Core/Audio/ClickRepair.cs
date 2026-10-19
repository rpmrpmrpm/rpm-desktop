namespace RPM.Core.Audio;

/// <summary>How the click repair decides what is a click and how far it will go to mend one.</summary>
/// <param name="Threshold">
/// How many robust standard deviations a sample must depart from what the signal predicts before it
/// counts as damage. Lower finds more.
/// </param>
/// <param name="FusionSamples">
/// How far apart two damaged samples may be and still be mended as one click.
///
/// <para>ffmpeg's <c>adeclick</c> cannot go this low: its <c>burst</c> setting is in milliseconds and
/// has a minimum of 10, which is 441 samples at 44.1 kHz. Most clicks are much shorter. A typical click
/// is about 26 samples (0.59 ms at 44.1 kHz). Fusing at 441 samples would rebuild about seventeen times
/// more of the record than the damage needs, and the extra samples would be changed for no reason.</para>
/// </param>
/// <param name="MaxGapSamples">
/// The longest run that will be treated as a click. A longer run is music, not damage, and
/// interpolating across it would invent a musical phrase.
/// </param>
/// <param name="Order">
/// Order of the autoregressive model used both to predict and to interpolate. It must be high enough
/// to describe the detail of music and not only a smooth curve: an order-2 model would read every
/// sharp sound as damage and rebuild it dull.
/// </param>
public sealed record ClickRepairSettings(
    double Threshold = 8,
    int FusionSamples = 8,
    int MaxGapSamples = 220,
    int Order = 24);

/// <summary>
/// Finds clicks and mends them, one short stretch at a time.
///
/// <para><b>Why this exists next to ffmpeg's <c>adeclick</c>.</b> That filter finds clicks well, but it
/// repairs them bluntly. It joins every detection into a burst of up to 10 ms and rebuilds the whole
/// burst. On worn records this can rebuild a large share of the samples. The rebuilt audio is smoothed,
/// which cuts the treble of everything it touches. This class rebuilds only the damaged samples, so it
/// changes far less of the music.</para>
///
/// <para><b>Detection.</b> A short-term linear predictor is fitted to the music around each point.
/// Music is predictable and a click is not. So the prediction error is small almost everywhere, and it
/// jumps where a click is. The jump is measured against a median-based scale and not a mean. That way a
/// few very large outliers cannot raise the limit that is meant to detect them.</para>
///
/// <para><b>Repair.</b> The damaged samples are dropped and re-derived from the music either side, by
/// the least-squares interpolation of Janssen, Veldhuis and Vries (1986): choose the missing samples
/// that make the whole stretch as predictable as the music around it. It is written from the
/// published method, like <see cref="NoiseReducer"/>.</para>
///
/// <para>It works on a stream and does not depend on block size: the same audio gives the same
/// samples whether it arrives 50 ms at a time or all at once. The output runs
/// <see cref="LatencyFrames"/> behind the input, because a click cannot be mended until the music
/// after it has arrived.</para>
/// </summary>
public sealed class ClickRepair
{
    /// <summary>Samples of music kept on each side of a repair. They are used to fit the predictor and
    /// to anchor the interpolation at both ends.</summary>
    private const int Pad = 512;

    /// <summary>How many samples are finished per pass. This is only a working size: the result does
    /// not depend on it, because every repair is decided with <see cref="Pad"/> samples of real music
    /// on each side, wherever the block boundaries are.</summary>
    private const int Chunk = 4096;

    private readonly ClickRepairSettings _settings;
    private readonly int _channels;

    private readonly List<float>[] _input;
    private readonly List<float>[] _output;
    private readonly long[] _inputBase;
    private readonly long[] _next;

    /// <summary>How many repairs have been made since the last <see cref="Reset"/>, and how many
    /// samples they cover. The UI reports these, and they show how much of the record was
    /// rebuilt.</summary>
    public int RepairCount { get; private set; }
    public long RepairedSamples { get; private set; }

    public ClickRepair(ClickRepairSettings settings, int channels)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (settings.Order < 2) throw new ArgumentOutOfRangeException(nameof(settings));

        _settings = settings;
        _channels = channels;
        _input = new List<float>[channels];
        _output = new List<float>[channels];
        _inputBase = new long[channels];
        _next = new long[channels];
        for (int c = 0; c < channels; c++) { _input[c] = new List<float>(); _output[c] = new List<float>(); }
        Reset();
    }

    /// <summary>How far the output runs behind the input: the music a repair needs after the damage
    /// before it can be mended.</summary>
    public int LatencyFrames => Pad + _settings.MaxGapSamples;

    public void Reset()
    {
        for (int c = 0; c < _channels; c++)
        {
            _input[c].Clear();
            _output[c].Clear();
            // Silence at the start, so the first real samples have a full pad behind them and every
            // Process call returns as many frames as it was given.
            for (int i = 0; i < LatencyFrames; i++) _input[c].Add(0);
            _inputBase[c] = 0;
            _next[c] = LatencyFrames;
        }
        RepairCount = 0;
        RepairedSamples = 0;
    }

    /// <summary>Mends <paramref name="frameCount"/> interleaved frames in place.</summary>
    public void Process(float[] interleaved, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if (frameCount <= 0) return;
        if (frameCount * _channels > interleaved.Length)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        for (int c = 0; c < _channels; c++)
        {
            var input = _input[c];
            for (int f = 0; f < frameCount; f++)
                input.Add(interleaved[f * _channels + c]);

            // A chunk is finished once a full pad of music after its end has arrived.
            while (_inputBase[c] + input.Count - (_next[c] + Chunk) >= Pad)
                RepairChunk(c);

            var output = _output[c];
            while (output.Count < frameCount) output.Add(0);
            for (int f = 0; f < frameCount; f++)
                interleaved[f * _channels + c] = output[f];
            output.RemoveRange(0, frameCount);
        }
    }

    /// <summary>
    /// Mends every click that starts inside one chunk, then passes the chunk to the output.
    ///
    /// <para>Repairs are written back into the input buffer, not into a copy. A click that begins near
    /// the end of a chunk and continues past it is mended once and output in two pieces, so it is not
    /// half-mended at the boundary.</para>
    /// </summary>
    private void RepairChunk(int channel)
    {
        var input = _input[channel];
        long start = _next[channel];
        int from = (int)(start - _inputBase[channel]) - Pad;
        int to = Math.Min(input.Count, (int)(start - _inputBase[channel]) + Chunk + Pad);
        int len = to - from;

        if (from >= 0 && len > _settings.Order * 4)
        {
            var work = new double[len];
            for (int i = 0; i < len; i++) work[i] = input[from + i];

            // Only clicks that begin in this chunk. A click that begins earlier belongs to the
            // previous chunk, which has already mended it.
            foreach (var (gapStart, gapEnd) in Detect(work, Pad, Pad + Chunk))
            {
                if (!Interpolate(work, gapStart, gapEnd))
                    continue;
                RepairCount++;
                RepairedSamples += gapEnd - gapStart + 1;
                for (int i = gapStart; i <= gapEnd && from + i < input.Count; i++)
                    input[from + i] = (float)work[i];
            }
        }

        int emit = Chunk;
        var output = _output[channel];
        int at = (int)(start - _inputBase[channel]);
        for (int i = 0; i < emit && at + i < input.Count; i++)
            output.Add(input[at + i]);

        _next[channel] += emit;

        // Everything more than a pad behind the next chunk can no longer be used by any repair.
        int drop = (int)(_next[channel] - _inputBase[channel]) - Pad;
        if (drop > 0)
        {
            input.RemoveRange(0, drop);
            _inputBase[channel] += drop;
        }
    }

    /// <summary>
    /// The damaged runs in <paramref name="x"/> that begin between <paramref name="lo"/> and
    /// <paramref name="hi"/>.
    /// </summary>
    private List<(int Start, int End)> Detect(double[] x, int lo, int hi)
    {
        var found = new List<(int, int)>();
        int p = _settings.Order;
        var a = FitAr(x, 0, x.Length, p);
        if (a is null) return found;

        // The prediction residual, with a scale taken from its median so that the clicks themselves
        // cannot raise the limit that is meant to catch them.
        var residual = new double[x.Length];
        for (int n = p; n < x.Length; n++)
        {
            double pred = 0;
            for (int k = 0; k < p; k++) pred += a[k] * x[n - 1 - k];
            residual[n] = x[n] - pred;
        }
        double scale = 1.4826 * MedianAbs(residual, p, x.Length);
        if (scale <= 0) return found;

        double limit = _settings.Threshold * scale;
        int n2 = p;
        while (n2 < x.Length)
        {
            if (Math.Abs(residual[n2]) <= limit) { n2++; continue; }

            int s = n2, e = n2;
            // Extend the run while another damaged sample is within the fusion distance.
            int quiet = 0;
            for (int m = n2 + 1; m < x.Length && quiet <= _settings.FusionSamples; m++)
            {
                if (Math.Abs(residual[m]) > limit) { e = m; quiet = 0; }
                else quiet++;
            }

            // The residual reacts only after the predictor has seen the damage, so the run is widened by
            // two samples on each side. The jump in the residual comes later than the first damaged sample.
            s = Math.Max(1, s - 2);
            e = Math.Min(x.Length - 2, e + 2);

            if (e - s + 1 <= _settings.MaxGapSamples && s >= lo && s < hi)
                found.Add((s, e));

            // Always move forward. Widening clamps e to the end of the array, so for a damaged sample
            // in the last two positions e can be below n2, and "n2 = e + 1" would never advance
            // (an endless loop).
            n2 = Math.Max(n2 + 1, e + 1);
        }
        return found;
    }

    /// <summary>
    /// Replaces <paramref name="x"/>[start..end] with the samples that make the stretch as
    /// predictable as the music on each side of it. Returns false when there is not enough clean music
    /// around the damage to do this.
    /// </summary>
    private bool Interpolate(double[] x, int start, int end)
    {
        int p = _settings.Order;
        int m = end - start + 1;
        int lo = Math.Max(0, start - Pad), hi = Math.Min(x.Length, end + 1 + Pad);
        if (m <= 0 || m > _settings.MaxGapSamples || hi - lo < p * 4 + m) return false;

        // The predictor is fitted on the music around the damage, not on the damage itself.
        var a = FitArSkipping(x, lo, hi, start, end, p);
        if (a is null) return false;

        // Each row is one prediction error and the unknowns are the missing samples. Only rows that
        // include a missing sample matter, and the normal equations are built directly from them.
        var normal = new double[m, m];
        var rhs = new double[m];
        for (int n = Math.Max(lo + p, start - p); n < hi && n <= end + p; n++)
        {
            if (n - p < lo) continue;

            // coefficient of x[n-k] in this row: 1 for k=0, -a[k-1] otherwise
            double known = 0;
            for (int k = 0; k <= p; k++)
            {
                int idx = n - k;
                double coef = k == 0 ? 1 : -a[k - 1];
                if (idx < start || idx > end) known += coef * x[idx];
            }
            for (int r = 0; r < m; r++)
            {
                int idxR = start + r;
                int kR = n - idxR;
                if (kR < 0 || kR > p) continue;
                double cR = kR == 0 ? 1 : -a[kR - 1];
                rhs[r] -= cR * known;
                for (int q = 0; q < m; q++)
                {
                    int kQ = n - (start + q);
                    if (kQ < 0 || kQ > p) continue;
                    double cQ = kQ == 0 ? 1 : -a[kQ - 1];
                    normal[r, q] += cR * cQ;
                }
            }
        }

        var solved = Solve(normal, rhs, m);
        if (solved is null) return false;
        for (int r = 0; r < m; r++)
        {
            if (double.IsNaN(solved[r]) || double.IsInfinity(solved[r])) return false;
            x[start + r] = Math.Clamp(solved[r], -4, 4);
        }
        return true;
    }

    /// <summary>Autoregressive coefficients over a span, found by the Levinson-Durbin method from its
    /// autocorrelation.</summary>
    private static double[]? FitAr(double[] x, int from, int to, int order)
    {
        var r = new double[order + 1];
        for (int k = 0; k <= order; k++)
        {
            double s = 0;
            for (int n = from + k; n < to; n++) s += x[n] * x[n - k];
            r[k] = s;
        }
        return Levinson(r, order);
    }

    /// <summary>The same, with one stretch left out of the sums: the damage must not shape the model
    /// that will replace it.</summary>
    private static double[]? FitArSkipping(double[] x, int from, int to, int skipFrom, int skipTo, int order)
    {
        var r = new double[order + 1];
        for (int k = 0; k <= order; k++)
        {
            double s = 0;
            for (int n = from + k; n < to; n++)
            {
                if ((n >= skipFrom && n <= skipTo) || (n - k >= skipFrom && n - k <= skipTo)) continue;
                s += x[n] * x[n - k];
            }
            r[k] = s;
        }
        return Levinson(r, order);
    }

    private static double[]? Levinson(double[] r, int order)
    {
        if (r[0] <= 0) return null;
        // A small amount is added to the diagonal. A perfectly predictable stretch (silence, a held
        // tone) would make the system singular, and records contain both.
        double err = r[0] * 1.0001;
        var a = new double[order];
        for (int i = 0; i < order; i++)
        {
            double acc = r[i + 1];
            for (int j = 0; j < i; j++) acc -= a[j] * r[i - j];
            double k = acc / err;
            if (double.IsNaN(k) || double.IsInfinity(k)) return null;

            var prev = (double[])a.Clone();
            a[i] = k;
            for (int j = 0; j < i; j++) a[j] = prev[j] - k * prev[i - 1 - j];
            err *= 1 - k * k;
            if (err <= 0) return null;
        }
        return a;
    }

    /// <summary>Gaussian elimination with partial pivoting. The systems are the size of one
    /// click.</summary>
    private static double[]? Solve(double[,] m, double[] b, int n)
    {
        var a = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) a[i, j] = m[i, j];
            a[i, n] = b[i];
            a[i, i] *= 1.0000001;
        }
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (Math.Abs(a[pivot, col]) < 1e-18) return null;
            if (pivot != col)
                for (int j = col; j <= n; j++) (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);

            for (int row = col + 1; row < n; row++)
            {
                double f = a[row, col] / a[col, col];
                if (f == 0) continue;
                for (int j = col; j <= n; j++) a[row, j] -= f * a[col, j];
            }
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = a[i, n];
            for (int j = i + 1; j < n; j++) s -= a[i, j] * x[j];
            x[i] = s / a[i, i];
        }
        return x;
    }

    private static double MedianAbs(double[] v, int from, int to)
    {
        int n = to - from;
        if (n <= 0) return 0;
        var copy = new double[n];
        for (int i = 0; i < n; i++) copy[i] = Math.Abs(v[from + i]);
        Array.Sort(copy);
        return copy[n / 2];
    }
}
