using RPM.Core.Splitting;

namespace RPM.Core.Audio;

/// <summary>The shape of a record, read a block at a time.
///
/// <para>It is built the same way as <see cref="RPM.Core.Analysis.LoudnessScanner"/>, for the same reason.
/// The waveform is made of buckets, and buckets can be folded in from a stream. So the samples never all
/// have to exist at once.</para>
///
/// <para>The mix lab draws its waveforms from <see cref="PcmDecoder.DecodeMono"/>, which holds a whole
/// track in memory. That is the right choice for the analyser, which reads every sample several times. It
/// is the wrong choice here. A five-minute record is about 26 MB of floats, read through a MemoryStream
/// that grows as it fills, and each of those buffers goes straight onto the large object heap. Two decks'
/// worth of that, on the frame the lab opens, causes a gen2 collection. A gen2 collection suspends every
/// managed thread in the process, including the one feeding the sound device, and that thread runs dry for
/// as long as the collection takes. The listener hears a burst of stutter when the workbench appears, and
/// the audio path is not at fault. See the underrun message in <c>OpenAlSink</c>, which says how long the
/// pump was away.</para>
/// </summary>
public static class PeakScanner
{
    /// <summary>A second at a time: big enough that the pipe is not the bottleneck, and small enough that
    /// nothing here holds a whole track.</summary>
    private const double BlockSeconds = 1;

    /// <summary>Reads whatever ffmpeg can open (a path or a signed URL) and returns its waveform, or null
    /// when it will not decode. A record that cannot be drawn is not worth interrupting a set for.
    /// </summary>
    /// <param name="sampleRate">What to decode at. The picture is peaks a few milliseconds wide, so a
    /// modest rate costs nothing in the drawing and halves the decode work.</param>
    public static WavePeaks? Scan(
        string source, int sampleRate, double bucketSeconds = WavePeaks.DefaultBucketSeconds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        FfmpegPcmSource? pcm = null;
        try
        {
            pcm = new FfmpegPcmSource(new FfmpegPcmSource.Options
            {
                Source = source,
                SampleRate = sampleRate,
                Channels = 1,
                Label = "peaks",
            });

            if (pcm.Ended)
                return null;

            var peaks = new WavePeaks.Accumulator(sampleRate, bucketSeconds);
            int block = Math.Max(1, (int)(sampleRate * BlockSeconds));
            var buffer = new float[block];

            bool any = false;
            while (!ct.IsCancellationRequested)
            {
                int frames = pcm.Read(buffer, block);
                if (frames <= 0)
                    break;

                peaks.Add(buffer.AsSpan(0, frames));
                any = true;
            }

            return ct.IsCancellationRequested || !any ? null : peaks.ToPeaks();
        }
        catch
        {
            return null;
        }
        finally
        {
            pcm?.Dispose();
        }
    }
}
