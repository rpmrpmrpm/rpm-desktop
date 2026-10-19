namespace RPM.Core.Analysis;

/// <summary>
/// What a track sounds like on the arousal axis, as three numbers, or nothing when the track was too
/// short to measure.
///
/// <para>These are the RAW measurements, not a band or a mood. The rule that turns them into "Calm"
/// or "Energetic" can change: it is arithmetic over the whole collection, weighted against the other
/// tracks. A stored band would become out of date when the rule changes. The raw numbers are facts
/// about the audio and stay valid whatever the rule does with them.</para>
/// </summary>
/// <param name="Brightness">How much of the track's energy sits above <see cref="EnergyProfile.TopHz"/>,
/// 0..1. It compares hats and air with kick and body. It is cheap to compute, it does not depend on
/// the tempo octave, and unlike loudness it is mostly not a result of the mastering.</param>
/// <param name="Onsets">Events per second: how often the level jumps, whatever the bar length. This
/// gives the arousal signal that tempo cannot give, because nothing here depends on choosing
/// between 70 and 140 BPM (see <see cref="TempoOctave"/>).</param>
/// <param name="Dynamics">The spread between the track's loud and quiet passages, in decibels. A
/// compressed dance record is near zero, and an orchestral movement is much higher.</param>
public readonly record struct MoodReading(double Brightness, double Onsets, double Dynamics)
{
    public static readonly MoodReading Unknown = new(-1, -1, -1);

    /// <summary>Whether anything was measured. The unknown case uses negative values, not zero,
    /// because zero is a real answer for all three: a sine wave has no brightness, no onsets and no
    /// dynamics.</summary>
    public bool Known => Brightness >= 0;
}

/// <summary>
/// Measures the three arousal numbers off a stream, a block at a time.
///
/// <para><b>It reuses a decode that is already running.</b> The library's loudness pass reads every
/// track once, one at a time, in the background (see <c>LoudnessService</c>). The decode is the
/// expensive part, and this class only adds filters and counting. It has the same shape as
/// <see cref="LoudnessMeter"/> on purpose: give it the same interleaved blocks and read the result at
/// the end, so the two meters run side by side over one pass of the samples.</para>
///
/// <para><b>There is no FFT.</b> Brightness uses two biquad filters, onsets are the rises of a level
/// that is already computed, and dynamics is a spread over those same levels. Chroma, which a KEY
/// reading (and so the valence half of a mood) would need, requires an FFT per hop and is not
/// included. For this reason the class measures one axis and not two.</para>
/// </summary>
public sealed class MoodMeter
{
    /// <summary>How long a level frame is. It is short enough that a snare and the kick after it
    /// are two frames, and long enough that a few hundred frames describe a track.</summary>
    private const double FrameSeconds = 0.02;

    /// <summary>
    /// How much louder a frame has to be than the one before it to count as an event, as a ratio.
    ///
    /// <para>A quarter above the previous frame. It is low enough to detect a soft hat, and high
    /// enough that the normal small variation of a sustained note is not counted as an onset.
    /// Otherwise a string section would count as the busiest record in the collection.</para>
    /// </summary>
    private const double OnsetRise = 1.25;

    /// <summary>The number of frames after an event before another can be counted. Two frames: a
    /// kick and its own decay are one event, and no music has fifty separate attacks a second.</summary>
    private const int OnsetHold = 2;

    /// <summary>Frames below this level are not counted: silence, a fade, or a gap between tracks
    /// on a rip. Without it the noise floor of a quiet passage would trigger the onset counter
    /// constantly, and a silent track would measure as the most eventful one.</summary>
    private const double SilenceFloor = 1e-4;

    /// <summary>The shortest track that can be described, in seconds. Below this there are too few
    /// frames for the spread to mean anything.</summary>
    private const double LeastSeconds = 5;

    private readonly int _rate;
    private readonly int _channels;
    private readonly int _frameSamples;

    private Biquad _bass;
    private Biquad _top;

    // The frame being filled.
    private double _sumFull, _sumUnderTop;
    private int _inFrame;

    // The results of the frames so far. The levels are kept as decibels in a histogram, not as a
    // list: a ten-minute track is thirty thousand frames, this runs on a machine where someone is
    // listening to music, and the only questions asked of the levels are two percentiles.
    private readonly int[] _levelBins = new int[DbBins];
    private int _framesCounted;

    private double _energyFull, _energyTop;
    private double _previousLevel;
    private int _sinceOnset;
    private int _onsets;
    private long _samples;

    /// <summary>One bin per decibel from <see cref="DbFloor"/> up to zero. A frame louder than full
    /// scale cannot occur, and a frame quieter than the floor is silence.</summary>
    private const int DbBins = 100;
    private const double DbFloor = -100;

    public MoodMeter(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _rate = sampleRate;
        _channels = channels;
        _frameSamples = Math.Max(1, (int)(sampleRate * FrameSeconds));
        _bass = new Biquad(EnergyProfile.BassHz, sampleRate);
        _top = new Biquad(EnergyProfile.TopHz, sampleRate);
    }

    /// <summary>
    /// Takes one block of interleaved samples, the same block that the loudness meter receives.
    /// </summary>
    /// <param name="interleaved">Samples, channel-interleaved, in -1..1.</param>
    /// <param name="frames">How many frames of <c>channels</c> samples the buffer holds.</param>
    public void Add(ReadOnlySpan<float> interleaved, int frames)
    {
        if (frames <= 0)
            return;

        int wanted = frames * _channels;
        if (wanted > interleaved.Length)
            frames = interleaved.Length / _channels;

        for (int f = 0; f < frames; f++)
        {
            // Fold to mono first. Every number here describes the record, not its stereo image, and
            // filtering each channel would double the work for a result that nothing uses.
            double x = 0;
            int at = f * _channels;
            for (int c = 0; c < _channels; c++)
                x += interleaved[at + c];
            x /= _channels;

            // One pass over the samples, two filters. The top band is what remains when everything
            // under the cutoff is removed. The subtraction is done on squared values, as the energy
            // profile does it.
            double underTop = _top.Next(x);
            _ = _bass.Next(x);

            _sumFull += x * x;
            _sumUnderTop += underTop * underTop;
            _samples++;

            if (++_inFrame >= _frameSamples)
                CloseFrame();
        }
    }

    private void CloseFrame()
    {
        double full = Math.Sqrt(_sumFull / _inFrame);
        double top = Math.Sqrt(Math.Max(0, _sumFull - _sumUnderTop) / _inFrame);

        _sumFull = _sumUnderTop = 0;
        _inFrame = 0;

        // Energy, not level: brightness is a share of the whole track's energy, so the frames are
        // added, not averaged. A loud chorus should count for more than a quiet intro.
        _energyFull += full * full;
        _energyTop += top * top;

        if (full > SilenceFloor)
        {
            _framesCounted++;
            int bin = (int)Math.Clamp(20 * Math.Log10(full) - DbFloor, 0, DbBins - 1);
            _levelBins[bin]++;

            // An event is a rise, and only after the hold from the last event has ended. The rise is
            // measured against the previous frame, not the track's average, so a record that gets
            // louder is not read as a record that gets busier.
            if (_sinceOnset >= OnsetHold && _previousLevel > SilenceFloor && full > _previousLevel * OnsetRise)
            {
                _onsets++;
                _sinceOnset = 0;
            }
            else
            {
                _sinceOnset++;
            }
        }
        else
        {
            _sinceOnset++;
        }

        _previousLevel = full;
    }

    /// <summary>The measured result, or <see cref="MoodReading.Unknown"/> for a track too short or
    /// too quiet to describe.</summary>
    public MoodReading Reading
    {
        get
        {
            double seconds = (double)_samples / _rate;
            if (seconds < LeastSeconds || _framesCounted == 0 || _energyFull <= 0)
                return MoodReading.Unknown;

            // Brightness is a share of energy, so it describes the arrangement and not the level that
            // the record was mastered at.
            double brightness = Math.Clamp(_energyTop / _energyFull, 0, 1);

            // Events per second of the part that was not silence. It is divided by the frames that
            // counted, not by the track's length, so a song with two minutes of silence added at the
            // end gives the same result as the same song without it.
            double heard = _framesCounted * FrameSeconds;
            double onsets = heard > 0 ? _onsets / heard : 0;

            return new MoodReading(brightness, onsets, Spread());
        }
    }

    /// <summary>
    /// The distance in decibels between the track's loud passages and its quiet ones: the 90th
    /// percentile of the frames against the 10th.
    ///
    /// <para>Percentiles are used instead of the maximum and minimum. One clipped sample and one
    /// moment of near-silence would otherwise decide the number for the whole record, and almost
    /// every rip has both.</para>
    /// </summary>
    private double Spread()
    {
        double loud = Percentile(0.90);
        double quiet = Percentile(0.10);
        return Math.Max(0, loud - quiet);
    }

    private double Percentile(double share)
    {
        int target = (int)Math.Clamp(Math.Round(_framesCounted * share), 1, _framesCounted);
        int seen = 0;
        for (int bin = 0; bin < DbBins; bin++)
        {
            seen += _levelBins[bin];
            if (seen >= target)
                return DbFloor + bin;
        }

        return 0;
    }
}
