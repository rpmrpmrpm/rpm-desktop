namespace RPM.Core.Analysis;

/// <summary>Where the audio is, in seconds from the start of the file.</summary>
/// <param name="StartSeconds">The first moment there is anything to hear.</param>
/// <param name="EndSeconds">The last moment. Equal to the duration for a track that runs to its
/// final sample, which most do.</param>
public readonly record struct AudioSpan(double StartSeconds, double EndSeconds)
{
    /// <summary>True when both values are meaningful. A track that is silent throughout gives
    /// neither.</summary>
    public bool IsKnown => EndSeconds > StartSeconds;
}

/// <summary>
/// Finds where a track's audio begins and ends, as opposed to where its file does.
///
/// <para>
/// The two differ, and the difference is audible. A file contains the gap before the track, the
/// encoder's own delay, and any tail left after the last note. A changeover timed against a length
/// that includes dead air arrives late. Playing from zero on a file with a second of silence at the
/// start begins too early.
/// </para>
///
/// <para>
/// The measurement uses an <b>absolute</b> floor, not the track's own level. <see cref="CuePointFinder"/>
/// reads its cues at a share of the track's median, so a loud, compressed master passes that
/// threshold almost everywhere and a quiet, dynamic one barely passes it: the answer depends on the
/// mastering. Silence is silence at any mastering level, and a number that means the same on every
/// track is the only kind that can be shared between people.
/// </para>
/// </summary>
public static class AudioEdges
{
    /// <summary>
    /// The level that counts as silence, in dBFS.
    ///
    /// <para>This is low on purpose. The code looks for the boundary between audio and no audio, not
    /// for where the track gets going (that is a cue point, a different question). A fade-in that
    /// starts at -55 dB is the track starting, and cutting it off would be a worse error than
    /// including a little of a noisy master's floor.</para>
    /// </summary>
    public const double SilenceFloorDb = -60;

    /// <summary>The amplitude that corresponds to <see cref="SilenceFloorDb"/>, as a fraction of
    /// full scale.</summary>
    private static readonly double Floor = Math.Pow(10, SilenceFloorDb / 20);

    /// <summary>
    /// The length of one measuring window: 20 ms. It is short enough that the result is accurate to
    /// well inside a beat, and long enough that a single stray sample (a click, a decoder's first
    /// frame) cannot pass for the start of the music.
    /// </summary>
    private const double WindowSeconds = 0.02;

    /// <summary>
    /// How many windows in a row must clear the floor before the audio counts as started. One window
    /// can be a click, and a real fade-in clears far more than two.
    /// </summary>
    private const int WindowsHeld = 2;

    /// <summary>
    /// Where the audio starts and stops. It uses RMS over short windows, not single samples, so a
    /// lone click at the start of a file is not mistaken for the track.
    /// </summary>
    public static AudioSpan Find(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (mono.Length == 0 || sampleRate <= 0)
            return default;

        int window = Math.Max(1, (int)Math.Round(WindowSeconds * sampleRate));
        int windows = mono.Length / window;
        if (windows < 1)
            return new AudioSpan(0, (double)mono.Length / sampleRate);

        // One pass, one flag per window. A whole track at the analysis rate gives a few thousand
        // windows, which is small compared with the FFTs this runs alongside.
        var loud = new bool[windows];
        for (int w = 0; w < windows; w++)
        {
            double sum = 0;
            int from = w * window;
            for (int i = from; i < from + window; i++)
                sum += (double)mono[i] * mono[i];

            loud[w] = Math.Sqrt(sum / window) >= Floor;
        }

        int first = FirstHeld(loud, forward: true);
        int last = FirstHeld(loud, forward: false);

        if (first < 0 || last < 0 || last <= first)
            return default;

        // The start is the beginning of its window and the end is the end of its own window.
        // Rounding both the same way would cut off part of the audio at one end.
        double duration = (double)mono.Length / sampleRate;
        return new AudioSpan(
            Math.Clamp((double)first * window / sampleRate, 0, duration),
            Math.Clamp((double)(last + 1) * window / sampleRate, 0, duration));
    }

    /// <summary>The first window, counting from one end, that clears the floor and stays above it for
    /// <see cref="WindowsHeld"/> windows. Minus one when none does.</summary>
    private static int FirstHeld(ReadOnlySpan<bool> loud, bool forward)
    {
        int step = forward ? 1 : -1;
        int from = forward ? 0 : loud.Length - 1;

        for (int w = from; w >= 0 && w < loud.Length; w += step)
        {
            if (!loud[w])
                continue;

            bool held = true;
            for (int k = 1; k < WindowsHeld; k++)
            {
                int at = w + k * step;
                // A run that reaches the end of the track counts: the last two windows of a track
                // are as much part of it as any other two.
                if (at < 0 || at >= loud.Length)
                    break;
                if (!loud[at]) { held = false; break; }
            }

            if (held)
                return w;
        }

        return -1;
    }
}
