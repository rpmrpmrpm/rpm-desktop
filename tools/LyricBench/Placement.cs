using RPM.Core.Lyrics;
using RPM.Onnx;

namespace LyricBench;

/// <summary>One line placed on one record: where every word was placed, and the model's scores for
/// that placement.</summary>
/// <param name="Words">The words, in the record's own clock, before <c>Rescue</c> and before the line is
/// put back on the reader's split. This is the raw result, which is what a set of weights is scored
/// on.</param>
/// <param name="Window">The time range that was searched.</param>
/// <param name="Wall">The end of that range, in the record's clock: where a tail that ran out of room
/// ends.</param>
/// <param name="Score">Mean log-probability of the letters where they were placed. Higher means the
/// model is more confident.</param>
/// <param name="Frames">How many frames the model read.</param>
/// <param name="OnTokens">How many of those frames the path spent on a letter, not on blank.</param>
/// <param name="Holed">Words whose letters are split by a hole: a letter, then a bar of nothing, then
/// the rest of the word. See <see cref="Placement.Hole"/>.</param>
/// <param name="WorstHole">The biggest such hole on the line.</param>
/// <param name="Took">Wall-clock seconds the model and the aligner needed.</param>
internal readonly record struct Placed(
    WordSpan[] Words, LyricWindow Window, double Wall,
    double Score, int Frames, int OnTokens, int Holed, double WorstHole, double Took);

/// <summary>
/// Places lyric lines the way <c>LyricAlignment</c> does, and returns the intermediate results.
///
/// <para>The window comes from <see cref="LyricWindow"/> and the words from <see cref="CtcAligner"/>
/// and <see cref="CtcVocabulary"/>: the app's own code, not copies. What is NOT reproduced here is
/// what the app does to the result afterwards (the tail rescue, putting the line back on the reader's
/// split). Those are corrections to a measurement, and a bench must see the measurement without
/// them, or it scores the correction.</para>
/// </summary>
internal sealed class Placement(string models)
{
    private readonly OnnxAligner _model = new(models);

    /// <summary>A gap this wide inside a word is not part of the word being sung: the model lost its
    /// place and found the rest of the letters later.</summary>
    public const double Hole = 0.5;

    public bool IsAvailable => _model.IsAvailable;

    /// <summary>The model's own alphabet, for a caller that needs the blank's id, not a
    /// placement.</summary>
    public CtcVocabulary? Vocabulary => _model.Vocabulary;

    /// <summary>The model's output for one range of audio, not aligned. <see cref="Voice"/> reads the
    /// blank from this; nothing else needs it.</summary>
    public (float[] Scores, int Frames, int Vocab)? Read(ReadOnlySpan<float> mono) => _model.Read(mono);


    public double Seconds => (double)OnnxAligner.SamplesPerFrame / OnnxAligner.SampleRate;

    /// <summary>
    /// One line, or null when there is no model or nothing to place.
    /// </summary>
    /// <param name="mono">The record, decoded once at the model's own rate.</param>
    /// <param name="clock">Added to every output time. Zero for the record the sheet was written for;
    /// the offset for a second recording of the same take that is read on its own clock.</param>
    public Placed? Place(float[] mono, string text, double from, double until, double clock = 0)
    {
        if (_model.Vocabulary is not { } alphabet || alphabet.Spell(text) is not { } spelling)
            return null;

        var window = LyricWindow.For(text, from, until);
        var at = (int)(window.Opens * OnnxAligner.SampleRate);
        if (at >= mono.Length)
            return null;

        var samples = Math.Min((int)(window.Length * OnnxAligner.SampleRate), mono.Length - at);
        if (samples < OnnxAligner.SampleRate / 5)
            return null;

        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_model.Read(mono.AsSpan(at, samples)) is not { } scores)
            return null;

        var aligned = CtcAligner.Align(
            scores.Scores, scores.Frames, scores.Vocab, spelling.Tokens, alphabet.Blank);
        var took = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds;

        if (aligned.Count != spelling.Tokens.Length)
            return null;

        var found = CtcVocabulary.Words(spelling, aligned);
        if (found.Count == 0)
            return null;

        double score = 0;
        var onTokens = 0;
        foreach (var token in aligned)
        {
            score += token.Score;
            onTokens += token.ToFrame - token.FromFrame + 1;
        }

        // The holes, checked per word: a word's own letters in order, and the widest gap between two
        // of them. A hole makes a word MEASURE long when it was not sung long, so a bench must count
        // holes separately from the word's length.
        var holed = 0;
        double worst = 0;
        for (var w = 0; w < spelling.Words.Length; w++)
        {
            var previous = -1;
            double biggest = 0;
            for (var i = 0; i < aligned.Count; i++)
            {
                if (spelling.Owners[i] != w)
                    continue;
                if (previous >= 0)
                    biggest = Math.Max(biggest, (aligned[i].FromFrame - previous) * Seconds);
                previous = aligned[i].ToFrame;
            }
            worst = Math.Max(worst, biggest);
            if (biggest > Hole)
                holed++;
        }

        var words = new WordSpan[found.Count];
        for (var i = 0; i < found.Count; i++)
            words[i] = new WordSpan(
                found[i].Start, found[i].Length,
                clock + window.Opens + found[i].FromFrame * Seconds,
                clock + window.Opens + (found[i].ToFrame + 1) * Seconds);

        return new Placed(
            words, window, clock + window.Opens + scores.Frames * Seconds,
            score / aligned.Count, scores.Frames, onTokens, holed, worst, took);
    }
}
