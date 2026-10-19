namespace RPM.Core.Audio;

/// <summary>Builds the audio texture that the visualisers read: a 512-wide, two-row, single-byte image.
/// The top row is the frequency spectrum. The bottom row is the raw waveform. <c>texture(channel, vec2(x,
/// 0.25)).x</c> gives the spectrum, and <c>vec2(x, 0.75)</c> gives the wave. Those coordinates are the
/// centre of each row, not its edge. At the edge, filtering would also read part of the other row.
///
/// Pure state, with no GL. It turns a played sample frame into the byte rows that the control uploads. It
/// is kept here so the FFT-to-texel mapping and the temporal smoothing can be tested without a surface.
/// </summary>
public sealed class ShaderAudioTexture
{
    /// <summary>Texels across each row. ShaderToy's audio texture is 512 wide.</summary>
    public const int Width = 512;

    /// <summary>Two rows: 0 = spectrum, 1 = waveform.</summary>
    public const int Height = 2;

    // The dB window mapped onto 0..255 for the spectrum row. Quiet bins sit near the floor, and a
    // full-scale tone sits near the top. This uses loudness, not raw magnitude, because loudness is what
    // the eye reads as a level.
    private const double DbFloor = -84.0;
    private const double DbCeil = -20.0;

    // How much of the previous spectrum frame carries over, so the bars settle instead of flickering.
    private const float Smoothing = 0.5f;

    private readonly int _frameSize;
    private readonly float[] _hann;
    private readonly double[] _re;
    private readonly double[] _im;
    private readonly float[] _mag = new float[Width];   // smoothed 0..1 spectrum, held between frames

    /// <summary>The texture bytes, row-major: <c>[0 .. Width)</c> is the spectrum row, and <c>[Width ..
    /// 2*Width)</c> is the waveform row. Single channel (R8). Reused from frame to frame.</summary>
    public byte[] Data { get; } = new byte[Width * Height];

    /// <param name="frameSize">Samples per analysis frame. It must be a power of two, and at least
    /// <c>2*Width</c>, so the spectrum has a texel per column.</param>
    public ShaderAudioTexture(int frameSize = 2048)
    {
        if (frameSize < Width * 2 || (frameSize & (frameSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(frameSize), "must be a power of two ≥ 1024");
        _frameSize = frameSize;
        _hann = Spectrum.BuildHann(frameSize);
        _re = new double[frameSize];
        _im = new double[frameSize];
    }

    /// <summary>Fills <see cref="Data"/> from a frame of played samples. A short or silent frame gives a
    /// resting texture: the spectrum at the floor, and the waveform centred.</summary>
    public void Update(ReadOnlySpan<float> frame)
    {
        FillSpectrumRow(frame);
        FillWaveRow(frame);
    }

    private void FillSpectrumRow(ReadOnlySpan<float> frame)
    {
        // No samples means every bin is at the floor, and the floor needs no FFT. The resting picture is
        // the held spectrum easing down, which is what the smoothing below computes for level 0. Skipping
        // the transform matters, because the stages feed an empty frame on every tick while paused or
        // buffering. That would be a 2048-point FFT of zeros, sixty times a second, on the UI thread.
        if (frame.IsEmpty)
        {
            for (int x = 0; x < Width; x++)
            {
                _mag[x] *= Smoothing;
                Data[x] = (byte)(_mag[x] * 255f + 0.5f);
            }

            return;
        }

        int n = _frameSize;
        int take = Math.Min(frame.Length, n);
        for (int i = 0; i < take; i++) { _re[i] = frame[i] * _hann[i]; _im[i] = 0; }
        for (int i = take; i < n; i++) { _re[i] = 0; _im[i] = 0; }

        Spectrum.Transform(_re, _im);

        // Linear bins, one texel each: bin k spans k × Rate / n Hz. The first 512 of the n/2 bins reach
        // about 11 kHz at 44.1 kHz, which covers most of the range that music uses.
        for (int x = 0; x < Width; x++)
        {
            double re = _re[x], im = _im[x];
            double power = (re * re + im * im) / (n * (double)n);
            double db = 10.0 * Math.Log10(power + 1e-12);
            float level = (float)Math.Clamp((db - DbFloor) / (DbCeil - DbFloor), 0.0, 1.0);

            // Peak-fast, fall-slow: a rising bin jumps up at once, and a falling bin eases down.
            _mag[x] = level > _mag[x] ? level : _mag[x] * Smoothing + level * (1 - Smoothing);
            Data[x] = (byte)(_mag[x] * 255f + 0.5f);
        }
    }

    private void FillWaveRow(ReadOnlySpan<float> frame)
    {
        int take = Math.Min(frame.Length, _frameSize);
        for (int x = 0; x < Width; x++)
        {
            // Take one sample per column across the row. Silence, or no samples, sits at mid-grey (128).
            int idx = take == 0 ? -1 : (int)((long)x * take / Width);
            float s = idx < 0 ? 0f : Math.Clamp(frame[idx], -1f, 1f);
            Data[Width + x] = (byte)((s * 0.5f + 0.5f) * 255f + 0.5f);
        }
    }
}
