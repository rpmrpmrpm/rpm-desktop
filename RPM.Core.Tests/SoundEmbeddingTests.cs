using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Fingerprint;

namespace RPM.Core.Tests;

public class SoundEmbeddingTests
{
    private static float[] Direction(int seed)
    {
        var random = new Random(seed);
        var v = new float[SoundEmbedding.Dimensions];
        for (int i = 0; i < v.Length; i++)
            v[i] = (float)(random.NextDouble() * 2 - 1);
        return v;
    }

    private static float[] Unit(float[] v)
    {
        double length = Math.Sqrt(v.Sum(x => (double)x * x));
        return [.. v.Select(x => (float)(x / length))];
    }

    [Fact]
    public void The_average_of_one_clip_is_that_clip_at_unit_length()
    {
        var clip = Direction(1);
        var average = SoundEmbedding.Average([clip]);

        Assert.NotNull(average);
        Assert.Equal(1.0, Math.Sqrt(average.Sum(x => (double)x * x)), 5);
        Assert.Equal(1.0, SoundEmbedding.Cosine(average, clip), 6);
    }

    [Fact]
    public void Clips_are_scaled_to_unit_length_before_they_are_averaged()
    {
        // A loud clip and a quiet one count the same: the model's output scale says nothing about the record.
        var a = Unit(Direction(1));
        var b = Unit(Direction(2));
        var big = a.Select(x => x * 50).ToArray();

        var average = SoundEmbedding.Average([big, b])!;
        var expected = Unit([.. a.Zip(b, (x, y) => x + y)]);

        Assert.Equal(1.0, SoundEmbedding.Cosine(average, expected), 6);
    }

    [Fact]
    public void A_clip_that_cannot_be_scaled_is_skipped()
    {
        var clip = Direction(3);
        var average = SoundEmbedding.Average([clip, new float[SoundEmbedding.Dimensions], new float[7]]);

        Assert.NotNull(average);
        Assert.Equal(1.0, SoundEmbedding.Cosine(average, clip), 6);
    }

    [Fact]
    public void No_usable_clip_is_no_embedding()
    {
        Assert.Null(SoundEmbedding.Average([]));
        Assert.Null(SoundEmbedding.Average([new float[SoundEmbedding.Dimensions]]));
    }

    [Fact]
    public void A_packed_embedding_reads_back_almost_exactly()
    {
        var embedding = Unit(Direction(4));
        var packed = SoundEmbedding.Pack(embedding);

        Assert.Equal(SoundEmbedding.PackedBytes, packed.Length);
        var back = SoundEmbedding.Unpack(packed);
        Assert.NotNull(back);
        Assert.True(SoundEmbedding.Cosine(embedding, back) > 0.99999);
    }

    [Fact]
    public void Packing_is_the_same_bytes_every_time()
    {
        var embedding = Unit(Direction(5));
        Assert.Equal(SoundEmbedding.Pack(embedding), SoundEmbedding.Pack([.. embedding]));

        // Plus and minus one are the ends of the range, and a number past them is held at the end.
        var ends = new float[SoundEmbedding.Dimensions];
        ends[0] = 1;
        ends[1] = -1;
        ends[2] = 3;
        var back = SoundEmbedding.Unpack(SoundEmbedding.Pack(ends))!;
        Assert.Equal(1f, back[0]);
        Assert.Equal(-1f, back[1]);
        Assert.Equal(1f, back[2]);
    }

    [Fact]
    public void Wrong_lengths_are_refused()
    {
        Assert.Throws<ArgumentException>(() => SoundEmbedding.Pack(new float[10]));
        Assert.Null(SoundEmbedding.Unpack(new byte[10]));
    }

    [Fact]
    public void Clips_are_centred_at_a_fifth_a_half_and_four_fifths()
    {
        Assert.Equal([35.0, 95.0, 155.0], SoundClips.Starts(200));

        // A clip that would start before the record starts at its beginning.
        Assert.Equal([0.0, 0.0, 1.4], SoundClips.Starts(8).Select(s => Math.Round(s, 6)));
        Assert.Equal([0.0, 0.0, 0.0], SoundClips.Starts(4));
        Assert.Equal([0.0, 0.0, 0.0], SoundClips.Starts(double.NaN));
        Assert.Equal(10.0, SoundClips.Seconds);
    }

    [Fact]
    public void Every_feeling_has_three_prompts()
    {
        Assert.Equal(18, SoundPrompts.Texts.Count);
        foreach (var feeling in Enum.GetValues<Feeling>())
            Assert.Equal(3, SoundPrompts.Texts.Count(p => p.Feeling == feeling));
    }

    [Fact]
    public void A_feelings_own_prompts_score_highest_for_that_feeling()
    {
        // The prompts' text embeddings are not audio, but a reading made of them must still point at its
        // own feeling first. A table written in the wrong order, or packed wrongly, fails this.
        foreach (var feeling in Enum.GetValues<Feeling>())
        {
            var own = SoundPrompts.Texts
                .Select((p, i) => (p.Feeling, i))
                .Where(p => p.Feeling == feeling)
                .Select(p => PromptVector(p.i))
                .ToList();
            var reading = SoundEmbedding.Average(own)!;

            var best = Enum.GetValues<Feeling>().MaxBy(f => SoundPrompts.Score(reading, f));
            Assert.Equal(feeling, best);
        }
    }

    [Fact]
    public void A_score_needs_a_whole_embedding()
    {
        Assert.Throws<ArgumentException>(() => SoundPrompts.Score(new float[3], Feeling.Happy));
    }

    /// <summary>A prompt's own vector, read through the scoring: the one direction that scores highest
    /// against that prompt alone.</summary>
    private static float[] PromptVector(int index)
    {
        var field = typeof(SoundPrompts).GetField("Prompts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var prompts = ((Feeling Feeling, string Text, float[] Vector)[])field.GetValue(null)!;
        return prompts[index].Vector;
    }

    [Fact]
    public void A_sound_fact_reads_back_as_the_same_embedding()
    {
        var embedding = Unit(Direction(6));
        var fact = SoundFact.From(embedding);

        Assert.NotNull(fact);
        Assert.Equal(RecordingFact.Sound, fact.Field);
        Assert.Equal(SoundFact.Generation, fact.Generation);
        Assert.True(fact.Modelled);
        Assert.True(new RecordingFact(fact.Field, fact.Generation, fact.Value, fact.Modelled, fact.Confidence).IsWellFormed);

        var back = SoundFact.Read(fact);
        Assert.NotNull(back);
        Assert.True(SoundEmbedding.Cosine(embedding, back) > 0.99999);
    }

    [Fact]
    public void A_sound_fact_from_another_generation_or_model_is_not_read()
    {
        var fact = SoundFact.From(Unit(Direction(7)))!;

        Assert.Null(SoundFact.Read(fact with { Generation = SoundFact.Generation + 1 }));
        Assert.Null(SoundFact.Read(fact with { Field = RecordingFact.Level }));
        Assert.Null(SoundFact.Read(fact with { Value = fact.Value.Replace(SoundFact.Model, "laion/clap-htsat-unfused") }));
        Assert.Null(SoundFact.Read(fact with { Value = "{}" }));
        Assert.Null(SoundFact.Read(fact with { Value = "not json" }));
        Assert.Null(SoundFact.Read(null));
    }

    [Fact]
    public void An_embedding_that_is_not_one_makes_no_fact()
    {
        var broken = Unit(Direction(8));
        broken[3] = float.NaN;

        Assert.Null(SoundFact.From(broken));
        Assert.Null(SoundFact.From(new float[3]));
    }

    [Fact]
    public void Two_machines_with_the_same_embedding_write_the_same_fact()
    {
        var embedding = Unit(Direction(9));
        Assert.Equal(SoundFact.From(embedding)!.Value, SoundFact.From([.. embedding])!.Value);
    }

    private sealed class FakeModel(Func<IReadOnlyList<float[]>, float[][]?> answer) : ISoundModel
    {
        public List<IReadOnlyList<float[]>> Asked { get; } = [];
        public bool IsAvailable => true;

        public float[][]? Embed(IReadOnlyList<float[]> features, CancellationToken ct)
        {
            Asked.Add(features);
            return answer(features);
        }
    }

    [Fact]
    public void The_reader_drops_clips_too_short_to_hear_and_averages_the_rest()
    {
        var a = Unit(Direction(10));
        var b = Unit(Direction(11));
        var model = new FakeModel(features => features.Count == 2 ? [a, b] : null);

        var clips = new[]
        {
            new float[ClapFeatures.ClipSamples],
            new float[SoundClips.LeastSamples - 1],
            new float[SoundClips.LeastSamples],
        };
        var reading = SoundReader.Read(model, clips, CancellationToken.None);

        Assert.Equal(SoundOutcome.Measured, reading.Outcome);
        var sent = Assert.Single(model.Asked);
        Assert.Equal(2, sent.Count);
        Assert.All(sent, f => Assert.Equal(ClapFeatures.ValuesPerClip, f.Length));
        Assert.Equal(1.0, SoundEmbedding.Cosine(reading.Embedding!, Unit([.. a.Zip(b, (x, y) => x + y)])), 6);
    }

    [Fact]
    public void A_model_that_does_not_answer_for_every_clip_has_not_answered()
    {
        // Not "nothing to hear": that would be stored as an answer about the file, and a busy cloud would
        // keep the record out of every feeling list until its file changed.
        var model = new FakeModel(_ => [Unit(Direction(12))]);
        var clips = new[] { new float[ClapFeatures.ClipSamples], new float[ClapFeatures.ClipSamples] };

        Assert.Equal(SoundResult.ModelDidNotAnswer, SoundReader.Read(model, clips, CancellationToken.None));
        Assert.Equal(SoundResult.ModelDidNotAnswer,
            SoundReader.Read(new FakeModel(_ => null), clips, CancellationToken.None));
    }

    [Fact]
    public void An_answer_with_no_direction_is_nothing_to_hear()
    {
        var model = new FakeModel(_ => [new float[SoundEmbedding.Dimensions]]);
        Assert.Equal(SoundResult.NothingToHear,
            SoundReader.Read(model, [new float[ClapFeatures.ClipSamples]], CancellationToken.None));
    }

    [Fact]
    public void The_reader_does_not_ask_the_model_about_nothing()
    {
        var model = new FakeModel(_ => throw new InvalidOperationException("asked"));
        Assert.Equal(SoundResult.NothingToHear, SoundReader.Read(model, [new float[100]], CancellationToken.None));
        Assert.Empty(model.Asked);
    }
}
