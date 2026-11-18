namespace RPM.Core.Analysis;

/// <summary>
/// The CLAP audio model, wherever it runs: on this machine (RPM.Onnx) or in the cloud, for a machine that
/// cannot run it.
///
/// <para>The split is the same as for the beat model. The features (<see cref="ClapFeatures"/>) are
/// built by the caller, because that is an FFT and every machine can do it. Only the features cross to
/// the model. What comes back is the model's own output, one embedding per clip, and
/// <see cref="SoundEmbedding.Average"/> turns it into the record's embedding on every machine in the
/// same way. So a record measured locally and a record measured in the cloud are directly
/// comparable.</para>
/// </summary>
public interface ISoundModel
{
    /// <summary>Whether there is any point in asking: the weights are there, or somebody is signed in,
    /// and nothing has failed for good in this session.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The model's output for each clip, in the order given, or null when it could not run.
    /// </summary>
    /// <param name="features">One array of <see cref="ClapFeatures.ValuesPerClip"/> values per clip, as
    /// <see cref="ClapFeatures.Compute"/> builds it.</param>
    float[][]? Embed(IReadOnlyList<float[]> features, CancellationToken ct);
}

/// <summary>What happened when a record's clips were read.</summary>
public enum SoundOutcome
{
    /// <summary>The record has an embedding.</summary>
    Measured,

    /// <summary>No clip was long enough to hear (see <see cref="SoundClips.LeastSamples"/>): the file is
    /// too short, silent, or will not decode. This is an answer about the file, and is stored, so the file
    /// is not tried again until it changes.</summary>
    NothingToHear,

    /// <summary>The model did not answer: no weights, a failure, or a cloud that was busy or out of reach.
    /// This says nothing about the file, and must not be stored as an answer about it. The record is
    /// read again later.</summary>
    ModelDidNotAnswer,
}

/// <summary>The outcome of one reading, with the embedding when there is one.</summary>
public readonly record struct SoundResult(SoundOutcome Outcome, float[]? Embedding)
{
    public static SoundResult NothingToHear { get; } = new(SoundOutcome.NothingToHear, null);
    public static SoundResult ModelDidNotAnswer { get; } = new(SoundOutcome.ModelDidNotAnswer, null);
}

/// <summary>Reads a record's embedding from its decoded clips, through whichever model the caller
/// has.</summary>
public static class SoundReader
{
    /// <summary>
    /// The record's embedding, or why there is none.
    /// </summary>
    /// <param name="clips">Mono audio at <see cref="ClapFeatures.SampleRate"/>, one array per clip, as
    /// decoded at <see cref="SoundClips.Starts"/>.</param>
    public static SoundResult Read(ISoundModel model, IReadOnlyList<float[]> clips, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(clips);

        var features = clips
            .Where(clip => clip.Length >= SoundClips.LeastSamples)
            .Select(clip => ClapFeatures.Compute(clip))
            .ToArray();
        if (features.Length == 0)
            return SoundResult.NothingToHear;

        ct.ThrowIfCancellationRequested();
        var embeddings = model.Embed(features, ct);
        if (embeddings is null || embeddings.Length != features.Length)
            return SoundResult.ModelDidNotAnswer;

        // The model answered, but with nothing that has a direction: every clip came back as zeros or as
        // numbers that are not numbers. That is the model's answer about this audio, not a failure to reach
        // it.
        return SoundEmbedding.Average(embeddings) is { } embedding
            ? new SoundResult(SoundOutcome.Measured, embedding)
            : SoundResult.NothingToHear;
    }
}
