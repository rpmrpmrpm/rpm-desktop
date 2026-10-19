namespace RPM.Core.Lyrics;

/// <summary>One token of a transcript and the frames the singer spent on it. <paramref name="ToFrame"/>
/// is inclusive: a token that occupies a single frame has <c>FromFrame == ToFrame</c>.</summary>
public readonly record struct TokenFrames(int Token, int FromFrame, int ToFrame, double Score);

/// <summary>
/// Forced alignment. Given what a model heard, frame by frame, and the words that were actually sung,
/// it works out which frames belong to which token.
///
/// <para><b>It is not recognition, and the difference matters.</b> Recognition asks "what was said", and
/// it can be wrong about the words. Forced alignment is told the words, because a lyric sheet has them.
/// It asks only when each word was sung. That is much cheaper and more reliable. The search covers
/// alignments of one known string, not every string the model might have heard.</para>
///
/// <para>The model is CTC. For every frame it gives a score to every token, and it may emit a BLANK, which
/// means nothing new in this frame. So a transcript of L tokens has many possible frame paths. A token can
/// be held for twenty frames, or emitted once and followed by blanks for nineteen. The aligner wants the
/// most likely path. This is a Viterbi walk over a trellis of 2L+1 states: the transcript with a blank
/// before, between and after every token. A blank is optional everywhere except between two identical
/// tokens, where it is required. Without that rule, "SEE" and "SE" would have the same path, and the second
/// E could not be told apart from a held first E.</para>
///
/// <para>Pure, and in Core. Tests can check it against hand-written scores. That is the only way to test an
/// aligner without shipping a model to the test runner.</para>
/// </summary>
public static class CtcAligner
{
    /// <summary>
    /// The most likely frame for every token of <paramref name="tokens"/>, or an empty list where there is no
    /// path at all.
    /// </summary>
    /// <param name="scores">Frame-major: <paramref name="frames"/> rows of <paramref name="vocab"/> values. For
    /// each frame, the model's score for every token. Logits or log-probabilities both work. They are
    /// normalised on the way in. Normalising an input that is already normalised changes nothing, so the caller
    /// cannot get this wrong by passing the wrong kind.</param>
    /// <param name="frames">How many frames of audio were read.</param>
    /// <param name="vocab">How many tokens the model knows.</param>
    /// <param name="tokens">The transcript as token ids: what was actually sung.</param>
    /// <param name="blank">The CTC blank's id. It is zero in every model that ships one.</param>
    public static IReadOnlyList<TokenFrames> Align(
        ReadOnlySpan<float> scores, int frames, int vocab, ReadOnlySpan<int> tokens, int blank = 0)
    {
        if (frames <= 0 || vocab <= 0 || tokens.Length == 0 || scores.Length < (long)frames * vocab)
            return [];

        if (blank < 0 || blank >= vocab)
            return [];

        foreach (var token in tokens)
            if (token < 0 || token >= vocab)
                return [];   // a transcript with a letter the model does not have cannot be aligned to it

        // A path must spend at least one frame on every token. It also needs one frame on the blank between
        // any two identical tokens. With fewer frames than that, nothing can be found. The words do not fit in
        // the audio. That is an answer, not an error.
        var least = tokens.Length;
        for (var i = 1; i < tokens.Length; i++)
            if (tokens[i] == tokens[i - 1])
                least++;

        if (frames < least)
            return [];

        var log = Normalise(scores, frames, vocab);

        // The interleaved transcript: state 0 is a blank, state 1 is the first token, state 2 is a blank, and so
        // on, ending with a blank. Even states are blanks. Odd state s holds token (s-1)/2.
        var states = 2 * tokens.Length + 1;

        var best = new double[frames * states];
        var from = new byte[frames * states];   // 0 stayed, 1 came from s-1, 2 skipped a blank from s-2
        Array.Fill(best, double.NegativeInfinity);

        // Only two starts are allowed: the leading blank, or the first token directly. Anything else would mean
        // tokens sung before the audio starts.
        best[0] = log[blank];
        if (states > 1)
            best[1] = log[tokens[0]];

        for (var t = 1; t < frames; t++)
        {
            var now = t * states;
            var was = now - states;
            var frame = t * vocab;

            for (var s = 0; s < states; s++)
            {
                var here = best[was + s];
                byte came = 0;

                if (s >= 1 && best[was + s - 1] > here)
                {
                    here = best[was + s - 1];
                    came = 1;
                }

                // The blank between two tokens may be skipped, but only between DIFFERENT tokens. Between two
                // identical tokens it is the only thing that separates them. Skipping it would turn "SEE" into
                // "SE" without any error.
                if (s >= 2 && (s & 1) == 1
                    && tokens[(s - 1) / 2] != tokens[(s - 3) / 2]
                    && best[was + s - 2] > here)
                {
                    here = best[was + s - 2];
                    came = 2;
                }

                if (double.IsNegativeInfinity(here))
                    continue;   // nothing reaches this state yet, so it stays unreachable

                best[now + s] = here + log[frame + ((s & 1) == 0 ? blank : tokens[(s - 1) / 2])];
                from[now + s] = came;
            }
        }

        // It ends on the last token or on the blank after it, whichever the audio scored higher.
        var last = (frames - 1) * states;
        var end = states - 1;
        if (states >= 2 && best[last + states - 2] > best[last + states - 1])
            end = states - 2;

        if (double.IsNegativeInfinity(best[last + end]))
            return [];   // no path reaches the end of the transcript

        // Back down the trellis, writing which state each frame belongs to.
        var path = new int[frames];
        var at = end;
        for (var t = frames - 1; t >= 0; t--)
        {
            path[t] = at;
            at -= from[t * states + at];
        }

        return Spans(path, log, frames, vocab, tokens, states);
    }

    /// <summary>The frames each token held, read from a finished path.</summary>
    private static List<TokenFrames> Spans(
        int[] path, double[] log, int frames, int vocab, ReadOnlySpan<int> tokens, int states)
    {
        var spans = new List<TokenFrames>(tokens.Length);

        for (var i = 0; i < tokens.Length; i++)
        {
            var state = 2 * i + 1;
            int opens = -1, closes = -1;
            double score = 0;

            for (var t = 0; t < frames; t++)
            {
                if (path[t] != state)
                    continue;

                if (opens < 0)
                    opens = t;
                closes = t;
                score += log[t * vocab + tokens[i]];
            }

            // Every token of a valid path is emitted somewhere, so this should not happen. But a token with no
            // frames would become a span that starts at -1, which is worse than leaving it out.
            if (opens < 0)
                continue;

            spans.Add(new TokenFrames(tokens[i], opens, closes, score / (closes - opens + 1)));
        }

        return spans;
    }

    /// <summary>
    /// The scores as log-probabilities, whatever they arrived as.
    ///
    /// <para>This is a log-softmax. It turns a row of logits into values that the trellis can add up. Applied
    /// to a row that is already log-probabilities, it changes nothing. Those values sum to one in probability
    /// space, so the term it subtracts is the log of one. That is why the caller does not say which kind it
    /// has. Getting that wrong would give a worse alignment with no error. This removes the question, so the
    /// caller cannot get it wrong.</para>
    /// </summary>
    private static double[] Normalise(ReadOnlySpan<float> scores, int frames, int vocab)
    {
        var log = new double[frames * vocab];

        for (var t = 0; t < frames; t++)
        {
            var row = t * vocab;

            var peak = double.NegativeInfinity;
            for (var v = 0; v < vocab; v++)
                if (scores[row + v] > peak)
                    peak = scores[row + v];

            // Shifted by the row's peak before exponentiating. Otherwise a very confident frame overflows.
            double sum = 0;
            for (var v = 0; v < vocab; v++)
                sum += Math.Exp(scores[row + v] - peak);

            var offset = peak + Math.Log(sum);
            for (var v = 0; v < vocab; v++)
                log[row + v] = scores[row + v] - offset;
        }

        return log;
    }
}
