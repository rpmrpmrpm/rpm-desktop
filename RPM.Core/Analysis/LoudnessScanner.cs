using RPM.Core.Audio;

namespace RPM.Core.Analysis;

/// <summary>What measuring a track found. <see cref="Known"/> is false for anything that would not
/// decode or was too short to measure. Store this result too: it stops the same unreadable file
/// being decoded again on every pass.</summary>
/// <param name="Lufs">Programme loudness. <see cref="double.NegativeInfinity"/> when unknown.</param>
/// <param name="TruePeak">Peak as a linear amplitude, 1.0 being full scale. Zero when unknown.</param>
public readonly record struct LoudnessReading(double Lufs, double TruePeak)
{
    public static readonly LoudnessReading Unknown = new(double.NegativeInfinity, 0);

    public bool Known => double.IsFinite(Lufs);

    /// <summary>The gain a player should apply for this reading, already limited so that the peak stays
    /// under the ceiling.</summary>
    public double GainDb(double targetLufs = ReplayGain.ReferenceLufs) =>
        Known ? ReplayGain.For(Lufs, TruePeak, targetLufs) : 0;
}

/// <summary>What one decode of a track produced. Either half can be unknown on its own: a
/// forty-second interlude is long enough to level and too short to describe, and a missing mood
/// must not discard the level.</summary>
public readonly record struct Scanned(LoudnessReading Loudness, MoodReading Mood)
{
    public static readonly Scanned Nothing = new(LoudnessReading.Unknown, MoodReading.Unknown);
}

/// <summary>
/// Measures a track by decoding it, a block at a time, through <see cref="LoudnessMeter"/> and
/// <see cref="MoodMeter"/>.
///
/// <para>The audio is streamed, not loaded: <see cref="PcmDecoder.DecodeMono"/> holds a whole track
/// in memory. That suits the DJ analyser but not this class, which runs over a whole library and
/// also in a Lambda function with a few hundred megabytes of memory.</para>
///
/// <para>The desktop and the cloud backfill use this same class on purpose. A phone playing a
/// cloud-only track and a computer playing the same record from its own disk must get the same
/// number. Otherwise the two are levelled differently.</para>
/// </summary>
public static class LoudnessScanner
{
    /// <summary>
    /// The sample rate used for the measurement. It is not the file's own rate: it is the rate
    /// BS.1770 specifies, so the true peak is measured the same way for every file. The loudness
    /// reading does not depend on the rate, but the true peak does, and the gain is limited
    /// against the true peak.
    /// </summary>
    public const int Rate = 48000;

    /// <summary>
    /// Measured in stereo, including for a surround file.
    ///
    /// <para>BS.1770 has weights for the surround channels. Asking ffmpeg to fold the file down to
    /// stereo ignores them. This is still correct here: the gain is applied to the stereo stream the
    /// player produces, so the measurement must match what the player outputs, not what is in the
    /// file.</para>
    /// </summary>
    private const int Channels = 2;

    /// <summary>
    /// How many frames are read at a time: 8,192, about a sixth of a second. The float block here and the
    /// PCM source's byte scratch are each 64 KB, under the 85,000 bytes where .NET puts an array on the
    /// large-object heap.
    ///
    /// <para>Larger blocks would put two arrays on the large-object heap for every track scanned. On a
    /// first pass over 30,000 tracks that caused gen-2 collections, which pause the UI thread. The
    /// meters give the same reading at any block size (MeterBlockSizeTests).</para>
    /// </summary>
    internal const int BlockFrames = 8192;

    /// <summary>
    /// Measures anything ffmpeg can open: a file path or a presigned URL.
    /// </summary>
    /// <param name="maxSeconds">Stop after this much of the track. Zero reads all of it. A long DJ
    /// set or a whole-side rip is measured on its opening part. This gives about the same programme
    /// loudness as a gated reading of the whole file, with a fraction of the decoding.</param>
    public static LoudnessReading Measure(string source, double maxSeconds = 0, CancellationToken ct = default)
        => Scan(source, maxSeconds, ct).Loudness;

    /// <summary>
    /// The same decode, which also measures the arousal numbers from the same samples.
    ///
    /// <para>Decoding is the expensive part and this pass already pays for it. <see cref="MoodMeter"/>
    /// only adds filters and counting on samples that have already been read, and it receives the same
    /// blocks as the loudness meter.</para>
    ///
    /// <para>There are two methods instead of one with a flag, because the callers differ: the cloud
    /// backfill needs a level and no mood, and <see cref="Measure"/> is its method. This one is for
    /// the desktop.</para>
    ///
    /// <para>Either half can be unknown on its own. A track long enough to level but too short to
    /// describe is normal (for example a forty-second interlude), and a missing mood must not discard
    /// the loudness reading.</para>
    /// </summary>
    public static Scanned Scan(string source, double maxSeconds = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        FfmpegPcmSource? pcm = null;
        try
        {
            pcm = new FfmpegPcmSource(new FfmpegPcmSource.Options
            {
                Source = source,
                SampleRate = Rate,
                Channels = Channels,
                LengthSeconds = maxSeconds > 0 ? maxSeconds : null,
                Label = "loudness",
            });

            if (pcm.Ended)
                return Scanned.Nothing;

            var meter = new LoudnessMeter(Rate, Channels);
            var mood = new MoodMeter(Rate, Channels);
            var buffer = new float[BlockFrames * Channels];

            while (!ct.IsCancellationRequested)
            {
                int frames = pcm.Read(buffer, BlockFrames);
                if (frames <= 0)
                    break;

                meter.Add(buffer, frames);
                mood.Add(buffer, frames);
            }

            if (ct.IsCancellationRequested || !meter.HasSignal)
                return Scanned.Nothing;

            double lufs = meter.IntegratedLufs;
            var reading = double.IsFinite(lufs)
                ? new LoudnessReading(lufs, meter.TruePeak)
                : LoudnessReading.Unknown;

            return new Scanned(reading, mood.Reading);
        }
        catch
        {
            // A file that will not decode gets no reading, the same as one that decodes to silence.
            // The caller cannot fix it, and a track without a gain still plays.
            return Scanned.Nothing;
        }
        finally
        {
            pcm?.Dispose();
        }
    }
}
