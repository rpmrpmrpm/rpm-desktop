namespace RPM.Core.Audio;

/// <summary>
/// A beat-synced echo on one deck's output, taken <em>after</em> the fader.
///
/// <para>It is used to leave a record that has no outro. On a bar line the track is sent into a
/// delay and the channel fader is closed. What remains is the last bar of the record, repeating and
/// fading, while the next record is brought in dry on the following downbeat. It also helps to move
/// between two records that have little in common.</para>
///
/// <para>The echo is taken after the fader. This is why it is separate from <see cref="SweepFilter"/>
/// and <see cref="DeckEq"/>, which both come before the fader. An echo taken before the fader would be
/// silenced when the fader closes, so the tail would go with the record. Taken after the fader, the
/// record is removed and the echo of it remains. This setting is called post-fader on a mixer.</para>
///
/// <para>There is one feedback delay line per channel pair, with no filtering in the loop. A DJ echo is
/// not a reverb: what comes back is the record, quieter, about four times. It works because the echo
/// still sounds like the record when the next record starts on top of it.</para>
///
/// <para>When idle it costs nothing, because <see cref="Process"/> returns on its first line. It stays
/// idle until <see cref="Throw"/> is called, at most once per changeover.</para>
/// </summary>
public sealed class EchoTail
{
    /// <summary>The longest delay this will hold: one bar of a very slow record. A longer delay is a
    /// loop, not an echo.</summary>
    public const double MaxDelaySeconds = 3;

    /// <summary>How quiet the tail must get before it is treated as finished: -60 dB, below the
    /// noise floor of any music it would play over.</summary>
    private const double Gone = 0.001;

    private readonly int _sampleRate;
    private readonly int _channels;

    /// <summary>The delay line: one delay length of interleaved frames. It is read and written at the
    /// same position, so each value read is exactly one delay old.</summary>
    private float[] _line = [];
    private int _delayFrames;
    private int _at;

    private double _feedback;
    private long _left;

    /// <summary>The loudest value the line has returned since it last wrapped round, and how many times
    /// it has wrapped. A tail can only be judged over a whole pass. The line holds a beat or two of
    /// record, so most of it is empty most of the time, and any single block is as likely to land in a
    /// gap as on a repeat.</summary>
    private double _peak;
    private long _passes;

    public EchoTail(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);
    }

    /// <summary>True while there is a tail to produce. It is false most of the time.</summary>
    public bool IsRunning => _left > 0;

    /// <summary>The length of the echo in seconds, normally one beat of the mix. Zero when idle.</summary>
    public double DelaySeconds => _delayFrames > 0 ? (double)_delayFrames / _sampleRate : 0;

    /// <summary>
    /// Starts the echo: from this block on, whatever the deck produces is fed to a delay of
    /// <paramref name="delaySeconds"/> that returns <paramref name="feedback"/> of itself each time
    /// round. Both the record and the tail are heard.
    /// </summary>
    /// <param name="delaySeconds">The delay. The caller has already worked it out in beats of the mix,
    /// so the repeats fall on the beat grid.</param>
    /// <param name="feedback">How much comes back each time round, 0..0.95. A value of 0.6 gives about
    /// four audible repeats.</param>
    /// <param name="forSeconds">The longest time the tail is kept. It normally fades out through its own
    /// feedback well before this. The limit stops a tail from outlasting the changeover that
    /// started it.</param>
    public void Throw(double delaySeconds, double feedback, double forSeconds)
    {
        int frames = (int)Math.Round(Math.Clamp(delaySeconds, 0.01, MaxDelaySeconds) * _sampleRate);
        if (frames <= 0 || forSeconds <= 0)
            return;

        int samples = frames * _channels;
        if (_line.Length < samples)
            _line = new float[samples];
        else
            Array.Clear(_line, 0, samples);

        _delayFrames = frames;
        _at = 0;
        _feedback = Math.Clamp(feedback, 0, 0.95);
        _left = (long)Math.Round(forSeconds * _sampleRate);
        _peak = 0;
        _passes = 0;
    }

    /// <summary>Silences the tail and clears it. Use it when a deck loads a new record or when a
    /// changeover ends while the echo is still sounding.</summary>
    public void Stop()
    {
        _left = 0;
        _peak = 0;
        _passes = 0;
        _delayFrames = 0;
        _at = 0;
        if (_line.Length > 0)
            Array.Clear(_line);
    }

    /// <summary>
    /// Adds the echo to one interleaved block in place, and takes the block into the delay line.
    ///
    /// <para>Runs on the audio thread, after the deck's fader. What goes in is what the listener
    /// would have heard. What comes out is that plus what was heard one delay ago, so closing the fader
    /// leaves the tail behind instead of removing it.</para>
    /// </summary>
    /// <param name="offset">Frames into <paramref name="buffer"/> where the block starts.</param>
    public void Process(float[] buffer, int offset, int frames)
    {
        if (_left <= 0 || _delayFrames <= 0 || frames <= 0)
            return;

        ArgumentNullException.ThrowIfNull(buffer);

        bool dead = false;

        for (int f = 0; f < frames; f++)
        {
            int at = _at * _channels;
            int i = (offset + f) * _channels;

            for (int c = 0; c < _channels; c++)
            {
                float delayed = _line[at + c];
                float dry = buffer[i + c];

                // What goes round is the dry signal plus what came back, so the delay repeats more than
                // once. The value is clamped when it enters the line: a feedback line is the one place
                // in a mixer where a loud record can build on itself, and a clipping tail sounds like a
                // broken effect.
                _line[at + c] = Math.Clamp(dry + delayed * (float)_feedback, -1f, 1f);
                buffer[i + c] = Math.Clamp(dry + delayed, -1f, 1f);

                float size = Math.Abs(delayed);
                if (size > _peak)
                    _peak = size;
            }

            if (++_at < _delayFrames)
                continue;

            // The line wraps round. A whole pass that returned nothing means the tail has faded out
            // through its own feedback, which is the normal way for it to end. This is judged over a
            // whole pass and not a block: the first pass reads the silence the line was cleared to,
            // and every later pass has a gap wherever the record was quiet.
            _at = 0;
            if (_passes > 0 && _peak < Gone)
                dead = true;
            _peak = 0;
            _passes++;
        }

        _left -= frames;

        // Stop as soon as the tail is inaudible, so the deck does not keep processing a tail nobody
        // can hear.
        if (dead || _left <= 0)
            Stop();
    }
}
