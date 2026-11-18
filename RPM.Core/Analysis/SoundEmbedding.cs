using System.Buffers.Binary;

namespace RPM.Core.Analysis;

/// <summary>
/// One record's CLAP embedding: what the audio model hears in it, as <see cref="Dimensions"/> numbers of
/// unit length. The feeling lists compare it with fixed text prompts (see <see cref="SoundPrompts"/>).
///
/// <para>A record is heard as three ten-second clips (see <see cref="SoundClips"/>). The model gives one
/// embedding per clip. Each is scaled to unit length, the three are averaged, and the average is scaled
/// to unit length again. This is the recipe the feeling lists were tested with, and changing it changes
/// which songs land in which list.</para>
/// </summary>
public static class SoundEmbedding
{
    /// <summary>Numbers in one embedding.</summary>
    public const int Dimensions = 512;

    /// <summary>Bytes in a packed embedding (see <see cref="Pack"/>).</summary>
    public const int PackedBytes = Dimensions * sizeof(short);

    /// <summary>
    /// The record's embedding from its clips' embeddings, or null when there is no usable clip.
    /// </summary>
    /// <param name="clips">The model's output for each clip, in any scale. A clip of the wrong length, or
    /// one with no length at all, is skipped, because it cannot be scaled to unit length.</param>
    public static float[]? Average(IReadOnlyList<float[]> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        var sum = new double[Dimensions];
        int used = 0;
        foreach (var clip in clips)
        {
            if (clip is not { Length: Dimensions })
                continue;

            double length = Length(clip);
            if (!(length > 0) || !double.IsFinite(length))
                continue;

            for (int i = 0; i < Dimensions; i++)
                sum[i] += clip[i] / length;
            used++;
        }

        if (used == 0)
            return null;

        double total = 0;
        foreach (var v in sum)
            total += v * v;
        total = Math.Sqrt(total);
        if (!(total > 0))
            return null;

        var result = new float[Dimensions];
        for (int i = 0; i < Dimensions; i++)
            result[i] = (float)(sum[i] / total);
        return result;
    }

    /// <summary>
    /// The embedding as <see cref="PackedBytes"/> bytes: each number as a signed 16-bit fraction of one,
    /// little-endian. This is what the library stores and what the recording pool carries.
    ///
    /// <para>Every number of a unit-length vector is between -1 and 1, so a 16-bit fraction loses nothing
    /// that matters. The step is about 3e-5, and an embedding packed and read back has a cosine above
    /// 0.99999 with the original. The same embedding always packs to the same bytes, on every
    /// machine.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The embedding is not <see cref="Dimensions"/> numbers.</exception>
    public static byte[] Pack(ReadOnlySpan<float> embedding)
    {
        if (embedding.Length != Dimensions)
            throw new ArgumentException($"An embedding has {Dimensions} numbers.", nameof(embedding));

        var packed = new byte[PackedBytes];
        for (int i = 0; i < Dimensions; i++)
        {
            double scaled = Math.Round(Math.Clamp(embedding[i], -1f, 1f) * short.MaxValue, MidpointRounding.AwayFromZero);
            BinaryPrimitives.WriteInt16LittleEndian(packed.AsSpan(i * sizeof(short)), (short)scaled);
        }
        return packed;
    }

    /// <summary>A packed embedding read back, or null when the bytes are not one.</summary>
    public static float[]? Unpack(ReadOnlySpan<byte> packed)
    {
        if (packed.Length != PackedBytes)
            return null;

        var embedding = new float[Dimensions];
        for (int i = 0; i < Dimensions; i++)
            embedding[i] = BinaryPrimitives.ReadInt16LittleEndian(packed[(i * sizeof(short))..]) / (float)short.MaxValue;
        return embedding;
    }

    /// <summary>The cosine of two embeddings: 1 for the same direction, 0 for unrelated ones.</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.IsEmpty)
            return 0;

        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            aa += a[i] * a[i];
            bb += b[i] * b[i];
        }
        return aa > 0 && bb > 0 ? dot / Math.Sqrt(aa * bb) : 0;
    }

    private static double Length(float[] v)
    {
        double total = 0;
        foreach (var x in v)
            total += x * x;
        return Math.Sqrt(total);
    }
}
