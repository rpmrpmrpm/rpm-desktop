using RPM.Core.Analysis;
using RPM.Onnx;

namespace TempoBench;

/// <summary>
/// The beat model with its expensive half cached: the weights run once per track per corpus, and
/// after that every decoding of their output costs one pass over two arrays.
///
/// <para>The split is exactly at <see cref="OnnxBeatModel.ReadLogits"/>. Before it are the decode
/// and nine passes through a transformer, which depend only on the audio and the weights, not on
/// anything this benchmark changes. After it is everything being measured (which peaks are beats,
/// which path a search takes, how a tempo is fitted to them), and none of it reads the audio
/// again.</para>
///
/// <para>This makes a parameter sweep possible. Filling the cache over GiantSteps takes about
/// forty-five minutes; every run after that takes under a second, so a threshold can be swept
/// instead of guessed.</para>
/// </summary>
internal sealed class CachedBeatModel(
    OnnxBeatModel inner, LogitCache cache, string corpus, string stem, BeatModelDecoder.Reader reader)
    : IBeatModel
{
    public BeatReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default)
    {
        var curves = cache.Get(corpus, stem);
        if (curves is null)
        {
            curves = inner.ReadLogits(mono, sampleRate, ct);
            if (curves is null)
                return null;

            cache.Put(corpus, stem, curves.Value.Beat, curves.Value.Downbeat);
        }

        return reader(curves.Value.Beat, curves.Value.Downbeat);
    }
}
