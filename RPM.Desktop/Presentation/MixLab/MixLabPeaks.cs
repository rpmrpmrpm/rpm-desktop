#if DEBUG
#if RPM_SKIA
using System.Collections.Concurrent;
using RPM.Core.Audio;
using RPM.Core.Mixing;
using RPM.Core.Splitting;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>
/// The actual waveform of each record in the set, decoded once and kept for the session.
///
/// <para>The analyser stores its curves with one reading every half second. That is a good resolution
/// for choosing a record. It is too coarse for the question this window is for. At 128 BPM, half a
/// second is a beat and a bit, so a bar is only four points, and a downbeat cannot be seen at all. A
/// bar line drawn over that cannot be judged. It can only be believed.</para>
///
/// <para>So the window uses the real waveform, at <see cref="WavePeaks.DefaultBucketSeconds"/>, which
/// gives two hundred points a second. That is enough to see a kick begin. Building it needs one pass
/// over the whole file. So it runs on a background thread, once per record, and only in the lab. A
/// record that is still being decoded draws from the curves until its peaks arrive.</para>
///
/// <para>This is one pass, not a full load. The samples are folded into buckets as they come out of
/// ffmpeg, and they are never all in memory at once. See <see cref="PeakScanner"/> for why.</para>
/// </summary>
public sealed class MixLabPeaks
{
    /// <summary>Mono at a modest sample rate. The picture is made of peaks a few milliseconds wide, so
    /// no higher rate is ever drawn. Halving the rate halves both the decode time and the
    /// memory.</summary>
    private const int Rate = 22050;

    private readonly ConcurrentDictionary<string, WavePeaks?> _done = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.Ordinal);

    /// <summary>Raised on a background thread when a record's waveform has arrived.</summary>
    public event Action? Arrived;

    /// <summary>
    /// The waveform for this record, or null while it is being decoded (or if it could not be). Asking
    /// starts the decode, so the caller only has to ask again.
    /// </summary>
    public WavePeaks? For(MixTrack? track)
    {
        if (track is null || string.IsNullOrWhiteSpace(track.Source))
            return null;

        if (_done.TryGetValue(track.Source, out var peaks))
            return peaks;

        // One decode per record, however many times the timer asks between starting and finishing.
        if (!_running.TryAdd(track.Source, true))
            return null;

        string source = track.Source;
        _ = Task.Run(() =>
        {
            // The record is streamed and never held in memory. See PeakScanner. Holding the whole
            // decoded record takes about 26 MB of floats for a five-minute track. Those floats sit on
            // the large object heap, and the stream grows as it fills. A garbage collection suspends
            // every managed thread, including the audio thread, and the audio device then runs out of
            // sound. The waveform is made of buckets, and buckets can be folded from a stream, so the
            // samples never all exist at the same time.
            var built = PeakScanner.Scan(source, Rate);

            _done[source] = built;
            Arrived?.Invoke();
        });

        return null;
    }
}
#endif
#endif
