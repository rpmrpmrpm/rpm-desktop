namespace RPM.Core.Analysis;

/// <summary>A feeling the CLAP model is asked about.</summary>
public enum Feeling
{
    Melancholy,
    Happy,
    Angry,
    Romantic,
    Zen,
    Festive,
}

/// <summary>
/// How strongly a record's embedding matches each feeling, from fixed text prompts.
///
/// <para>Each feeling has three prompts ("sad melancholic music", and so on). Their text embeddings were
/// worked out once, by the model's text half, and are stored in <c>SoundPrompts.Data.cs</c>, so the app
/// needs only the audio half. A record's score for a feeling is its mean cosine with that feeling's
/// prompts minus its mean cosine with every other prompt. The subtraction removes what all music has in
/// common, so the score says how much more the record is like this feeling than like the others.</para>
///
/// <para>The prompts were fixed before the blind listening test and must not be changed to fit a
/// result. A change needs a new listening test, and the data file must be written again by
/// scripts/export_clap_onnx.py, which works out the text embeddings with the same model.</para>
/// </summary>
public static partial class SoundPrompts
{
    private static readonly (Feeling Feeling, string Text, float[] Vector)[] Prompts =
        [.. Data().Select(d => (d.Feeling, d.Text, SoundEmbedding.Unpack(Convert.FromBase64String(d.Packed))
                                                 ?? throw new InvalidOperationException($"Prompt \"{d.Text}\" is not an embedding.")))];

    /// <summary>The prompts, for a test or a diagnostic.</summary>
    public static IReadOnlyList<(Feeling Feeling, string Text)> Texts { get; } =
        [.. Prompts.Select(p => (p.Feeling, p.Text))];

    /// <summary>
    /// How much more <paramref name="embedding"/> is like <paramref name="feeling"/> than like the other
    /// feelings. Only useful for ranking records against each other: the numbers are small, and their
    /// range depends on the model.
    /// </summary>
    /// <exception cref="ArgumentException">The embedding is not <see cref="SoundEmbedding.Dimensions"/>
    /// numbers.</exception>
    public static double Score(ReadOnlySpan<float> embedding, Feeling feeling)
    {
        if (embedding.Length != SoundEmbedding.Dimensions)
            throw new ArgumentException($"An embedding has {SoundEmbedding.Dimensions} numbers.", nameof(embedding));

        double own = 0, other = 0;
        int owns = 0, others = 0;
        foreach (var prompt in Prompts)
        {
            double cosine = SoundEmbedding.Cosine(embedding, prompt.Vector);
            if (prompt.Feeling == feeling)
            {
                own += cosine;
                owns++;
            }
            else
            {
                other += cosine;
                others++;
            }
        }

        return owns == 0 || others == 0 ? 0 : own / owns - other / others;
    }

    /// <summary>
    /// <see cref="Score"/> for every feeling at once: the same numbers, from one pass over the prompts.
    ///
    /// <para>A shelf scores every record in it when the session's mood cut is taken. Asked one feeling at
    /// a time, each record would be compared with each prompt six times. Here each comparison is made
    /// once, and the six scores are taken from the same eighteen cosines.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The embedding is not <see cref="SoundEmbedding.Dimensions"/>
    /// numbers.</exception>
    /// <remarks>Faster than <see cref="Score"/> as well as fewer calls: the prompts are held at unit length,
    /// so a cosine is one dot product over the embedding's own length, taken in single precision with the
    /// processor's vector instructions. On 19,803 records that is the difference between half a second and
    /// a few tens of milliseconds in the session's mood cut. The scores agree with <see cref="Score"/> to
    /// about six decimal places, far finer than any two records' scores differ.</remarks>
    public static FeelingScores ScoreAll(ReadOnlySpan<float> embedding)
    {
        if (embedding.Length != SoundEmbedding.Dimensions)
            throw new ArgumentException($"An embedding has {SoundEmbedding.Dimensions} numbers.", nameof(embedding));

        double length = Math.Sqrt(Dot(embedding, embedding));
        if (length == 0)
            return default;

        Span<double> sum = stackalloc double[FeelingCount];
        Span<int> count = stackalloc int[FeelingCount];
        double all = 0;
        foreach (var (feeling, unit) in UnitPrompts)
        {
            double cosine = Dot(embedding, unit) / length;
            sum[(int)feeling] += cosine;
            count[(int)feeling]++;
            all += cosine;
        }

        Span<double> scores = stackalloc double[FeelingCount];
        for (int f = 0; f < FeelingCount; f++)
        {
            int others = Prompts.Length - count[f];
            scores[f] = count[f] == 0 || others == 0 ? 0 : sum[f] / count[f] - (all - sum[f]) / others;
        }

        return new FeelingScores(scores[0], scores[1], scores[2], scores[3], scores[4], scores[5]);
    }

    private static readonly int FeelingCount = Enum.GetValues<Feeling>().Length;

    /// <summary>The prompts at unit length, for <see cref="ScoreAll"/>. Declared after
    /// <see cref="Prompts"/> in this file, which is the order the two are set up in.</summary>
    private static readonly (Feeling Feeling, float[] Unit)[] UnitPrompts =
        [.. Prompts.Select(p => (p.Feeling, AtUnitLength(p.Vector)))];

    private static float[] AtUnitLength(float[] v)
    {
        var length = Math.Sqrt(Dot(v, v));
        return length > 0 ? [.. v.Select(x => (float)(x / length))] : v;
    }

    /// <summary>The dot product of two equally long spans, with the processor's vector instructions.</summary>
    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var lanes = System.Numerics.Vector<float>.Count;
        var total = System.Numerics.Vector<float>.Zero;
        int i = 0;
        for (; i <= a.Length - lanes; i += lanes)
            total += new System.Numerics.Vector<float>(a[i..]) * new System.Numerics.Vector<float>(b[i..]);

        double dot = System.Numerics.Vector.Sum(total);
        for (; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }
}

/// <summary>A record's score for each feeling, as <see cref="SoundPrompts.Score"/> gives them.</summary>
public readonly record struct FeelingScores(
    double Melancholy, double Happy, double Angry, double Romantic, double Zen, double Festive)
{
    /// <summary>The score for one feeling.</summary>
    public double For(Feeling feeling) => feeling switch
    {
        Feeling.Melancholy => Melancholy,
        Feeling.Happy => Happy,
        Feeling.Angry => Angry,
        Feeling.Romantic => Romantic,
        Feeling.Zen => Zen,
        Feeling.Festive => Festive,
    };
}
