namespace RPM.Core.Analysis;

/// <summary>
/// A beat grid read from a track by a trained model, as the model gives it: a time for every beat
/// and a time for every bar line, both in seconds from the start of the file.
///
/// <para>This is not yet a grid that a mix can run against. The model works in frames of twenty
/// milliseconds, so consecutive beats are a frame or two out of step even on a track with a steady
/// tempo. That is more drift than a beat-match can carry (see <see cref="Mixing.TransitionPlanner"/>).
/// <see cref="ModelBeatGrid"/> turns these times into the even grid that the rest of the app
/// uses.</para>
/// </summary>
public sealed record BeatReading
{
    /// <summary>Every beat, in seconds, in order.</summary>
    public required IReadOnlyList<double> BeatTimes { get; init; }

    /// <summary>The bar lines, in seconds, in order. A subset of the beats, up to rounding.</summary>
    public required IReadOnlyList<double> DownbeatTimes { get; init; }
}

/// <summary>
/// Finds the beats and bar lines of a track by running a trained model over it.
///
/// <para>Implemented outside <c>RPM.Core</c>, because running a model needs a runtime and a file of
/// weights, and neither belongs in a library of pure DSP. The analyser uses an implementation when
/// the host provides one, and falls back to <see cref="BeatTracker"/> when it does not. The
/// fallback also covers a build with no model, a machine where the runtime will not start, and a
/// track the model declines to read.</para>
/// </summary>
public interface IBeatModel
{
    /// <summary>
    /// The beats and bar lines of a mono signal, or null when the model cannot read it (too short,
    /// no runtime, nothing found). Null is not an error: it tells the caller to fall back.
    ///
    /// <para>A whole track is several windows through the model and takes seconds. Cancelling
    /// <paramref name="ct"/> stops it between windows. The caller gets an
    /// <see cref="OperationCanceledException"/> and not a null, because a stopped set wants no
    /// reading at all, not the lower-quality fallback.</para>
    /// </summary>
    BeatReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default);
}

/// <summary>What a key model made of a track.</summary>
/// <param name="Key">The key it chose.</param>
/// <param name="Confidence">How far ahead of the runner-up the key was, 0..1. This is the same scale
/// <see cref="KeyDetector"/> reports, so a caller can use either.</param>
public readonly record struct KeyReading(MusicalKey Key, double Confidence);

/// <summary>
/// Names a track's key by running a trained model over it, instead of correlating a chromagram
/// against the twenty-four key profiles.
///
/// <para>The gain is in the major/minor decision. A chromagram cannot separate a key from its
/// relative minor by counting notes, because both contain the same notes. Only the weight of each
/// note tells them apart, and that is a judgement, not a sum. A wrong answer puts the track three
/// steps away on the Camelot wheel from where it belongs, which is the difference between a good
/// follow-on track and one that clashes.</para>
/// </summary>
public interface IKeyModel
{
    /// <summary>The key of a mono signal, or null when the model cannot read it. Cancellation works
    /// as in <see cref="IBeatModel.Read"/>.</summary>
    KeyReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default);
}
