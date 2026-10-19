using System.Security.Cryptography;
using System.Text;
using RPM.Core.Analysis;

namespace TempoBench;

/// <summary>
/// The model's output for each track of a corpus, kept on disk so the model runs only once per
/// track.
///
/// <para>Everything this benchmark tunes reads only two curves of per-frame scores: the peak picker,
/// both searches, the tempo fit, the octave reconciliation. Producing those curves needs a decode
/// and nine passes through a transformer, which is about ninety-five percent of the forty-five
/// minutes a run over GiantSteps takes. Reading them from disk is fast.</para>
///
/// <para>So one run fills this, and every later run is one pass over 664 pairs of arrays. That makes
/// it possible to sweep a threshold instead of testing one idea per hour, which matters because
/// several of the tuning parameters are continuous.</para>
///
/// <para><b>What invalidates it.</b> The curves depend only on the weights and on the spectrogram
/// passed to them. Both are part of the stamp in each file's header, so a different checkpoint or a
/// changed <see cref="MelSpectrogram"/> gives a cache miss, not wrong numbers from another
/// configuration with no error.</para>
/// </summary>
internal sealed class LogitCache
{
    /// <summary>Increase when the file layout changes, so an old cache is skipped instead of read
    /// wrongly.</summary>
    private const int Layout = 1;

    private const uint Magic = 0x4C475431;   // "LGT1"

    private readonly string _root;
    private readonly string _stamp;

    private int _hits, _misses;

    public int Hits => _hits;
    public int Misses => _misses;

    /// <param name="root">Where the files go. One folder per corpus underneath.</param>
    /// <param name="modelDirectory">The weights that produced the curves, which are half the stamp.</param>
    public LogitCache(string root, string modelDirectory)
    {
        _root = root;
        _stamp = StampOf(modelDirectory);
    }

    /// <summary>
    /// What identifies the numbers in a file: the weights that produced them and the spectrogram
    /// they were produced from.
    ///
    /// <para>The weights are identified by name, length and write time, not by hash: hashing eighty
    /// megabytes on every run would cost time the cache is meant to save, and a checkpoint replaced
    /// in place gets a new write time.</para>
    /// </summary>
    private static string StampOf(string modelDirectory)
    {
        var parts = new StringBuilder();
        parts.Append(Layout).Append('|');
        parts.Append(MelSpectrogram.SampleRate).Append('|')
             .Append(MelSpectrogram.FrameRate).Append('|')
             .Append(BeatModelDecoder.ChunkFrames).Append('|')
             .Append(BeatModelDecoder.BorderFrames).Append('|');

        // Every candidate, not only the one that will be opened: which one runs is decided per
        // machine, and a cache that ignored this would give int8 curves to a float32 run.
        var weights = Directory.Exists(modelDirectory)
            ? Directory.EnumerateFiles(modelDirectory, "*.onnx").Order(StringComparer.Ordinal)
            : Enumerable.Empty<string>();

        foreach (var file in weights)
        {
            var info = new FileInfo(file);
            parts.Append(info.Name).Append(':').Append(info.Length).Append(':')
                 .Append(info.LastWriteTimeUtc.Ticks).Append('|');
        }

        parts.Append(Environment.GetEnvironmentVariable("RPM_BEAT_MODEL") ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts.ToString())))[..16];
    }

    private string PathFor(string corpus, string stem)
    {
        // A track's name comes from a file name and can contain any character. Hashed, not
        // sanitised, because two names that sanitise to the same text would share a file.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stem)))[..24];
        return Path.Combine(_root, corpus, $"{key}.lgt");
    }

    /// <summary>The curves for a track, or null when they have not been stored yet under this
    /// stamp.</summary>
    public (float[] Beat, float[] Downbeat)? Get(string corpus, string stem)
    {
        var path = PathFor(corpus, stem);
        if (!File.Exists(path))
        {
            Interlocked.Increment(ref _misses);
            return null;
        }

        try
        {
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file);

            if (reader.ReadUInt32() != Magic || reader.ReadString() != _stamp)
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            int frames = reader.ReadInt32();
            if (frames is < 0 or > 50_000_000)
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            var beat = new float[frames];
            var downbeat = new float[frames];
            for (int i = 0; i < frames; i++)
                beat[i] = (float)reader.ReadHalf();
            for (int i = 0; i < frames; i++)
                downbeat[i] = (float)reader.ReadHalf();

            Interlocked.Increment(ref _hits);
            return (beat, downbeat);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            // A file partly written by a run that was stopped. Running the model again fixes it.
            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    /// <summary>
    /// Keeps the curves for a track.
    ///
    /// <para>Half precision, which is what the model outputs and is enough for the curves: they go
    /// through a sigmoid and then into logarithms, and a logit is meaningful to about three decimal
    /// places, not seven. It also halves the size: 100 KB per track instead of 200, and 35 MB for
    /// GiantSteps instead of 70.</para>
    /// </summary>
    public void Put(string corpus, string stem, float[] beat, float[] downbeat)
    {
        var path = PathFor(corpus, stem);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written to a separate file and then moved into place, so a run that is stopped halfway
        // leaves no file that a later run would read as valid.
        var temporary = path + $".{Environment.CurrentManagedThreadId}.part";
        try
        {
            using (var file = File.Create(temporary))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(Magic);
                writer.Write(_stamp);
                writer.Write(beat.Length);
                foreach (float v in beat)
                    writer.Write((Half)v);
                foreach (float v in downbeat)
                    writer.Write((Half)v);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            try { File.Delete(temporary); } catch { /* nothing left to do about it */ }
        }
    }
}
