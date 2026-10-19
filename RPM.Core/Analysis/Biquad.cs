namespace RPM.Core.Analysis;

/// <summary>
/// A second-order low pass run twice, the filter that <see cref="EnergyProfile.ComputeLowBand"/> uses.
/// It is a value type so that several filters can run side by side over one pass of the samples.
///
/// <para>It is a separate type, not private to the profile, because two classes use it: the
/// profile, which has the whole track in memory, and <see cref="MoodMeter"/>, which sees only
/// about a second of audio at a time. The filter state is stored in the value, so a streaming
/// caller can carry it from one block to the next and get the same result as the whole-track
/// caller.</para>
/// </summary>
public struct Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;      // first pass
    private double _u1, _u2, _v1, _v2;      // second pass

    public Biquad(double cutoffHz, int sampleRate)
    {
        double w0 = 2 * Math.PI * cutoffHz / sampleRate;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * 0.70710678);
        double a0 = 1 + alpha;
        _b0 = (1 - cos) / 2 / a0;
        _b1 = (1 - cos) / a0;
        _b2 = _b0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
        _x1 = _x2 = _y1 = _y2 = _u1 = _u2 = _v1 = _v2 = 0;
    }

    public double Next(double x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;

        double v = _b0 * y + _b1 * _u1 + _b2 * _u2 - _a1 * _v1 - _a2 * _v2;
        _u2 = _u1; _u1 = y; _v2 = _v1; _v1 = v;
        return v;
    }
}
