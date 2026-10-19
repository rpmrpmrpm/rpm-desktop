using RPM.Core.Audio;
using RPM.Desktop.Services;

namespace RPM.Desktop.Presentation.RPaMp;

/// <summary>
/// Turns the audio that is playing now into visualiser bands.
///
/// <para>The samples come straight from the output tap of <see cref="AudioEngine"/>, the same block
/// that is sent to the sound device. The bands are therefore in sync with what you hear, after EQ.</para>
/// <para>Do not decode, buffer or seek in this class. A second decoder would drift from the audio that
/// is playing, and seeks land on keyframes, so the spectrum would jump at each seek.</para>
/// </summary>
public sealed class SpectrumAnalyzer
{
    /// <summary>Frame size the caller must supply to <see cref="Compute"/>.</summary>
    public const int FrameSize = 2048;

    private readonly float[] _hann = Spectrum.BuildHann(FrameSize);
    private readonly double[] _re = new double[FrameSize];
    private readonly double[] _im = new double[FrameSize];

    /// <summary>Band levels from 0 to 1 for one frame of played samples. The caller owns the frame, so
    /// the oscilloscope can draw the same waveform the bars were computed from.</summary>
    public float[] Compute(float[] frame, int bandCount)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Length != FrameSize) throw new ArgumentException($"frame must be {FrameSize} samples", nameof(frame));

        for (int i = 0; i < FrameSize; i++) { _re[i] = frame[i] * _hann[i]; _im[i] = 0; }
        Spectrum.Transform(_re, _im);
        return Spectrum.MapBands(_re, _im, bandCount, AudioEngine.Rate);
    }
}
