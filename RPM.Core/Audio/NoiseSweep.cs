namespace RPM.Core.Audio;

/// <summary>The white noise a changeover is built on. It climbs under the record that is going out, and
/// falls away over the record that has just arrived.
///
/// <para>No record makes this sound, so it is added to the booth's output rather than played by a deck. A
/// rise tells the room how much time is left. What makes it work is not the rise but the <em>stop</em>: it
/// ends on the same sample where the new record reaches the floor, and the silence after it is the drop. A
/// fall is the other half. It comes down over the record that arrived, and has disappeared within a couple
/// of bars.</para>
///
/// <para>Two things change together in both cases: the level, and the same low pass filter that the records
/// are swept with. A rise starts as a rumble with its middle and top cut away, and turns into air as it
/// climbs. A fall does the reverse: it arrives whole and loses its middle and top over the bars it takes to
/// go. A rise that only gets louder sounds like a fault. A fall that only gets quieter sounds like someone
/// turned it down. The filter is what makes either one work.</para>
/// </summary>
public sealed class NoiseSweep
{
    /// <summary>The gain a rise reaches at the top, against a record at full scale. It is loud enough to
    /// be part of the music, and quiet enough that the loop under it is still the main sound.
    ///
    /// <para>This is the gain, not the loudest sample it produces. White noise through a filter overshoots
    /// its gain on some samples, by about a third. The booth clamps what it sums. By the time a rise
    /// reaches this level, the record under it is mostly gone.</para>
    /// </summary>
    public const double RisePeak = 0.2;

    /// <summary>The gain a fall starts at. It has its own value, not the rise's, because the two are heard
    /// against different things. A rise climbs over a loop that is ending. A fall comes down over a
    /// full-level record that the room has been waiting for.
    /// </summary>
    public const double FallPeak = 0.25;


    /// <summary>How long a cut takes: 30 milliseconds. This is too short to hear as a fade, and long enough
    /// to turn a step in the waveform into a slope.</summary>
    public const double ReleaseSeconds = 0.03;

    private readonly int _sampleRate;
    private readonly int _channels;

    /// <summary>The same filter that a record is rolled out under. See <see cref="SweepFilter"/>. Using one
    /// sweep for the whole booth means one thing to tune and one thing to get right.</summary>
    private readonly SweepFilter _sweep;

    /// <summary>The noise, before the filter has processed it. Filtering happens in place over a block, so
    /// it cannot be done on the caller's buffer. What is being summed into that buffer is the
    /// record.</summary>
    private float[] _scratch = [];

    private long _delay, _length, _at;
    private long _releaseFrames, _releaseLeft;
    private double _releaseFrom;
    private bool _running, _releasing, _rising;

    /// <summary>The noise itself. It has its own generator, seeded the same way every time. The noise is
    /// random in character, so one run is as good as another. A set that sounds the same twice can be
    /// tested.</summary>
    private uint _seed = 0x9E3779B9;

    public NoiseSweep(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);
        _sweep = new SweepFilter(sampleRate, channels);
    }

    /// <summary>True while there is any of it left to hear.</summary>
    public bool IsRunning => _running;

    /// <summary>True while it is climbing rather than going away.</summary>
    public bool IsRising => _running && _rising;

    /// <summary>How loud it is right now, for tests and for anything that draws it.</summary>
    public double Level
    {
        get
        {
            if (!_running || _delay > 0)
                return 0;
            if (_releasing)
                return _releaseFrames > 0 ? _releaseFrom * _releaseLeft / _releaseFrames : 0;

            double t = Through;

            // A rise follows a squared curve, and a fall follows a straight line. They are not the same
            // shape because they do different jobs. A rise must be barely there early, or it is only a wash
            // of sound. A fall must still be there late, or the filter closing over it is a change nobody
            // hears. A squared decay is used up within the first bar of four.
            return _rising ? RisePeak * t * t : FallPeak * (1 - t);
        }
    }

    /// <summary>How far through its length it is, 0..1.</summary>
    private double Through => _length > 0 ? Math.Clamp((double)_at / _length, 0, 1) : 1;

    /// <summary>Starts a rise: silence for <paramref name="delayFrames"/> (the wait for the bar line the
    /// changeover is attached to), then <paramref name="lengthFrames"/> of climbing. It holds at the top
    /// until it is cut off. A rise that fades out on its own has not risen to anything.
    /// </summary>
    public void Rise(long delayFrames, long lengthFrames)
    {
        Reset();
        _delay = Math.Max(0, delayFrames);
        _length = Math.Max(1, lengthFrames);
        _running = true;
        _rising = true;
    }

    /// <summary>Starts a fall: full at once, and gone by the end of <paramref name="lengthFrames"/>.
    /// Nothing cuts it off. It is already leaving, and the record it is leaving over is the one the room
    /// came for.
    /// </summary>
    public void Fall(long lengthFrames)
    {
        Reset();
        _length = Math.Max(1, lengthFrames);
        _running = true;
        _rising = false;
    }

    /// <summary>Cuts it off. This is not a fade. It takes <see cref="ReleaseSeconds"/>, the shortest time
    /// a waveform can be stopped in without the stop itself being the loudest thing in the bar.
    /// </summary>
    public void Release()
    {
        if (!_running || _releasing)
            return;

        _releaseFrom = Level;
        _releaseFrames = Math.Max(1, (long)(ReleaseSeconds * _sampleRate));
        _releaseLeft = _releaseFrames;
        _releasing = true;
    }

    /// <summary>Takes it away at once, for a set that has been stopped rather than handed over.</summary>
    public void Stop() => Reset();

    private void Reset()
    {
        _running = false;
        _releasing = false;
        _rising = false;
        _delay = _length = _at = 0;
        _releaseFrames = _releaseLeft = 0;
        _sweep.Reset();
    }

    /// <summary>Sums the sweep into one interleaved block. Nothing is written while a rise is waiting for
    /// its bar line, and nothing at all once it is over.
    /// </summary>
    /// <param name="offset">Frames into <paramref name="buffer"/> where the block starts.</param>
    public void Add(float[] buffer, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!_running || frames <= 0)
            return;

        int done = 0;
        while (done < frames)
        {
            if (_delay > 0)
            {
                int skip = (int)Math.Min(frames - done, _delay);
                _delay -= skip;
                done += skip;
                continue;
            }

            int take = frames - done;

            // A release has its own length, not the length of the block. Without this, the cut would last
            // as long as whichever block it landed in. That would be 50 ms when 30 ms was asked for, and a
            // different result on a machine with a different buffer size.
            if (_releasing)
                take = (int)Math.Min(take, Math.Max(1, _releaseLeft));

            // Read the level at both ends of the part about to be written, so the gain is ramped across the
            // block instead of stepping at its edge.
            double from = Level;
            _at += take;
            if (_releasing)
                _releaseLeft = Math.Max(0, _releaseLeft - take);
            double to = Level;

            // The filter opens across a rise and closes across a fall. The noise starts with its middle and
            // top cut away, and gets them back as it climbs. That is what turns a rumble into air, rather
            // than just turning it up. The cutoff is set once per block, like every other filter here. A
            // cutoff that moves every 50 ms is a sweep. A cutoff that moves every sample costs time and
            // cannot be heard.
            double open = _rising ? Through : 1 - Through;
            _sweep.SetCutoff(SweepFilter.ClosedHz
                * Math.Pow(SweepFilter.OpenHz / SweepFilter.ClosedHz, open));

            if (_scratch.Length < take * _channels)
                _scratch = new float[take * _channels];

            for (int i = 0; i < take * _channels; i++)
                _scratch[i] = (float)Noise();

            _sweep.Process(_scratch, 0, take);

            for (int f = 0; f < take; f++)
            {
                double gain = from + (to - from) * (f + 1) / take;
                int at = (offset + done + f) * _channels, mine = f * _channels;
                for (int c = 0; c < _channels; c++)
                    buffer[at + c] += (float)(_scratch[mine + c] * gain);
            }

            done += take;

            // Stop here if a cut has landed, or if a fall has reached the floor. In both cases the rest of
            // the block gets nothing.
            if ((_releasing && _releaseLeft <= 0) || (!_rising && Through >= 1))
            {
                Reset();
                return;
            }
        }
    }

    /// <summary>One sample of white noise, -1..1. It uses xorshift. A sweep needs noise, not statistical
    /// randomness, and xorshift takes only a few instructions with no virtual call.</summary>
    private double Noise()
    {
        _seed ^= _seed << 13;
        _seed ^= _seed >> 17;
        _seed ^= _seed << 5;
        return _seed / 2147483648.0 - 1.0;
    }
}
