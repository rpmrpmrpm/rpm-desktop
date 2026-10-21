namespace RPM.Core.Lyrics;

/// <summary>
/// What the model heard over a whole recording: a log-probability per token per frame, frame-major.
/// Frames outside the recording read as silence (see <see cref="SheetOffset.Outside"/>).
/// </summary>
public sealed class HeardRecord
{
    private readonly float[] _log;

    /// <param name="log">Frame-major log-probabilities: <paramref name="frames"/> rows of <paramref name="vocab"/> values.
    /// They must already be normalised. <see cref="SheetOffset.Normalised"/> makes them so.</param>
    public HeardRecord(float[] log, int frames, int vocab, int blank, double frameSeconds)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (frames < 0 || vocab <= 0 || log.Length < (long)frames * vocab)
            throw new ArgumentException("The scores do not cover the frames.", nameof(log));
        if (blank < 0 || blank >= vocab)
            throw new ArgumentOutOfRangeException(nameof(blank));
        if (!(frameSeconds > 0))
            throw new ArgumentOutOfRangeException(nameof(frameSeconds));

        _log = log;
        Frames = frames;
        Vocab = vocab;
        Blank = blank;
        FrameSeconds = frameSeconds;
    }

    public int Frames { get; }
    public int Vocab { get; }
    public int Blank { get; }

    /// <summary>How long one frame is, in seconds.</summary>
    public double FrameSeconds { get; }

    /// <summary>The log-probability of token <paramref name="token"/> at frame <paramref name="frame"/>. A frame before
    /// the start or after the end of the recording is silence: the blank is certain, and every letter is very unlikely.</summary>
    public double At(int frame, int token) =>
        frame < 0 || frame >= Frames
            ? token == Blank ? 0 : SheetOffset.Outside
            : _log[frame * Vocab + token];
}

/// <summary>
/// Finds how far a whole lyric sheet is out of time with its recording, as one constant shift.
///
/// <para><b>Why one shift.</b> A sheet is often written for another release of the song: a radio edit, or an album
/// version with a longer intro. Then every stamp is wrong by the same amount. <see cref="LyricWindow"/> searches only
/// a little before each stamp and up to the next stamp, so such a sheet cannot be fixed one line at a time. The
/// singing is outside the window of almost every line. This finds that amount first. The lines are then placed
/// against the shifted stamps.</para>
///
/// <para><b>How.</b> The model is run once over the whole recording (<see cref="Hear"/>). For each trial shift, each
/// sung line is placed in a window that opens at its shifted stamp, and scored: how much more likely the line's
/// letters make that stretch of audio than silence does. This uses the words, not only "is somebody singing". The
/// model is a weak singing detector, but a line's own letters, and the order of all the lines, are a strong pattern.
/// A repeated chorus can match one line at the wrong place. It cannot match every line at the wrong place under one
/// shift.</para>
///
/// <para><b>The window opens at the stamp, and waiting costs.</b> Each frame of silence before the line's first
/// letter costs <see cref="LeadCost"/>. Without that cost, every shift that puts the singing anywhere inside the
/// window would score the same, and a sheet three seconds early would look right. With it, the best shift puts each
/// line's first letter at its shifted stamp. That is also what a reader needs: the line appears when it is sung.</para>
///
/// <para><b>When it says zero.</b> The shift is used only when it is clear. It must be at least <see cref="Least"/>
/// seconds, and not at the end of the search. Enough lines must agree with it on their own (<see cref="AgreeLines"/>):
/// each line has its own best shift, and on a sheet that is out of time many of them fall within
/// <see cref="AgreeSeconds"/> of the shift for the whole sheet. Where the sheet does not fit the recording, the lines'
/// own best shifts are spread over the whole search, and few fall near any one place. And no shift far from it may
/// score almost as well (<see cref="Rival"/>). Otherwise the answer is zero, and the sheet keeps its own stamps. A
/// wrong shift moves every line, so doubt must give zero.</para>
///
/// <para>The limits were set with tools/LyricBench (<c>--offset --move</c>) on real records with LRCLIB sheets. Where
/// the sheet fits, the best shift was the same to a frame or two whichever way the sheet was moved, and 6 to 16 lines
/// agreed with it. Where the sheet did not fit, at most 3 lines agreed, except at the end of the search.</para>
///
/// <para>Pure, and in Core. Tests check it with made-up scores. The transcoder and the bench use the same code.</para>
/// </summary>
public static class SheetOffset
{
    /// <summary>The largest shift tried, in seconds, either way.</summary>
    public const double Reach = 20.0;

    /// <summary>The smallest shift used, in seconds. Below this, the window's own slack (<see cref="LyricWindow.Slack"/>)
    /// already covers the error, and a shift would only add the risk of being wrong.</summary>
    public const double Least = 0.4;

    /// <summary>The fewest sung lines needed to judge a shift. With fewer, one line could decide it.</summary>
    public const int LeastLines = 6;

    /// <summary>The fewest lines whose own best shift must be near the shift for the whole sheet.</summary>
    public const int AgreeLines = 5;

    /// <summary>The smallest share of the judged lines whose own best shift must be near the shift for the whole
    /// sheet. With a long sheet this asks for more than <see cref="AgreeLines"/>.</summary>
    public const double AgreeShare = 0.1;

    /// <summary>How near a line's own best shift must be to the shift for the whole sheet to agree with it, in
    /// seconds.</summary>
    public const double AgreeSeconds = 0.5;

    /// <summary>A shift more than <see cref="RivalDistance"/> seconds from the best one must gain less than this share
    /// of what the best one gains. Otherwise the sheet fits two places, and neither is trusted.</summary>
    public const double Rival = 0.8;

    /// <summary>How far from the best shift another shift must be to count as a rival, in seconds.</summary>
    public const double RivalDistance = 1.0;

    /// <summary>The log-probability of a letter in a frame outside the recording. Very unlikely, but finite, so that a
    /// line pushed off the end scores badly instead of breaking the sum.</summary>
    public const double Outside = -20.0;

    /// <summary>What one frame of silence before a line's first letter costs, in log-probability. Small next to what
    /// a line's letters gain where they are sung, so it only decides between shifts that place the letters equally
    /// well.</summary>
    public const double LeadCost = 0.05;

    /// <summary>How much audio the model reads at once, in seconds, when it reads the whole recording.</summary>
    public const double ChunkSeconds = 20.0;

    /// <summary>How much two chunks overlap, in seconds. The model hears less context at the edges of a chunk, so each
    /// chunk gives up half of the overlap to its neighbour.</summary>
    public const double OverlapSeconds = 1.0;

    /// <summary>The coarse search moves this many frames at a time. The fine search then tries every frame near the
    /// best coarse shift.</summary>
    private const int CoarseStep = 5;

    /// <summary>
    /// What the search found. <paramref name="Seconds"/> is the shift to use: positive when the singing comes later than
    /// the sheet says. It is zero whenever the search was not sure. <paramref name="Best"/> is the best shift found,
    /// used or not, for the log. <paramref name="Lines"/> is how many lines were judged, and <paramref name="Agree"/> how
    /// many of them agree with the best shift on their own. <paramref name="Why"/> says why the shift was used or not.
    /// </summary>
    public readonly record struct Estimate(double Seconds, double Best, int Lines, int Agree, string Why)
    {
        public static Estimate None(string why) => new(0, 0, 0, 0, why);
    }

    /// <summary>
    /// Runs the model over the whole of <paramref name="mono"/> in chunks of <see cref="ChunkSeconds"/>, and joins the
    /// answers into one record. Null when the model did not read a chunk: a gap in the record would look like silence,
    /// and silence would pull the shift towards it.
    /// </summary>
    public static HeardRecord? Hear(float[] mono, int sampleRate, int samplesPerFrame, int blank, HearWindow hear)
    {
        ArgumentNullException.ThrowIfNull(mono);
        ArgumentNullException.ThrowIfNull(hear);
        if (sampleRate <= 0 || samplesPerFrame <= 0)
            return null;

        var frames = mono.Length / samplesPerFrame;
        if (frames == 0)
            return null;

        // Chunks start on a frame boundary, so frame f of a chunk is frame (start / samplesPerFrame + f) of the record.
        var chunk = Math.Max(1, (int)Math.Round(ChunkSeconds * sampleRate / samplesPerFrame)) * samplesPerFrame;
        var overlap = (int)Math.Round(OverlapSeconds * sampleRate / samplesPerFrame) * samplesPerFrame;
        if (overlap >= chunk)
            overlap = 0;
        var keepFrom = overlap / samplesPerFrame / 2;   // frames each later chunk leaves to the one before

        float[]? log = null;
        var vocab = 0;
        for (var start = 0; start < mono.Length; start += chunk - overlap)
        {
            var length = Math.Min(chunk, mono.Length - start);
            if (hear(mono.AsSpan(start, length)) is not { } heard || heard.Frames <= 0 || heard.Vocab <= 0)
            {
                // A short tail is too little audio for the model. It stays silence, which is what most tails are.
                if (start > 0 && length < sampleRate)
                    break;
                return null;
            }

            if (log is null)
            {
                vocab = heard.Vocab;
                if (blank < 0 || blank >= vocab)
                    return null;
                log = Silence(frames, vocab, blank);
            }
            else if (heard.Vocab != vocab)
                return null;

            var first = start / samplesPerFrame;
            var skip = start == 0 ? 0 : keepFrom;
            for (var f = skip; f < heard.Frames && first + f < frames; f++)
                Normalise(heard.Scores.AsSpan(f * vocab, vocab), log.AsSpan((first + f) * vocab, vocab));

            if (start + length >= mono.Length)
                break;
        }

        return log is null ? null : new HeardRecord(log, frames, vocab, blank, (double)samplesPerFrame / sampleRate);
    }

    /// <summary>Scores of any kind (logits or log-probabilities), frame-major, as a <see cref="HeardRecord"/>.</summary>
    public static HeardRecord Normalised(ReadOnlySpan<float> scores, int frames, int vocab, int blank, double frameSeconds)
    {
        var log = new float[frames * vocab];
        for (var f = 0; f < frames; f++)
            Normalise(scores.Slice(f * vocab, vocab), log.AsSpan(f * vocab, vocab));
        return new HeardRecord(log, frames, vocab, blank, frameSeconds);
    }

    /// <summary>
    /// The one shift that best fits <paramref name="lines"/> to <paramref name="heard"/>, or zero. See the class.
    /// </summary>
    /// <param name="lines">The whole sheet in order, wordless lines included: a line's window runs to the next line's
    /// stamp, as in <see cref="LineAligner.Place"/>.</param>
    public static Estimate Find(HeardRecord heard, IReadOnlyList<LyricLine> lines, CtcVocabulary alphabet)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(alphabet);

        var fs = heard.FrameSeconds;
        var judged = new List<Judged>();
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            if (LyricSheet.IsWordless(text) || alphabet.Spell(text) is not { Tokens.Length: > 0 } spelling)
                continue;
            foreach (var token in spelling.Tokens)
                if (token < 0 || token >= heard.Vocab)
                    return Estimate.None("the alphabet does not match the model");

            var from = lines[i].Seconds;
            var until = i + 1 < lines.Count ? lines[i + 1].Seconds : double.MaxValue;
            var window = LyricWindow.For(text, from, until);

            // The window opens at the stamp, without the slack: the lead cost decides how early the line starts. It
            // closes where LyricWindow closes it. It is not held at zero: under a trial shift it moves as a whole, and
            // every trial must score the same number of frames for the sums to be compared.
            var opens = (int)Math.Round(from / fs);
            var closes = (int)Math.Ceiling(window.Closes / fs);
            var tokens = spelling.Tokens;
            var least = tokens.Length;
            for (var t = 1; t < tokens.Length; t++)
                if (tokens[t] == tokens[t - 1])
                    least++;
            if (closes - opens < least)
                continue;   // the words do not fit the window under any shift

            judged.Add(new Judged(opens, closes - opens, tokens));
        }

        if (judged.Count < LeastLines)
            return Estimate.None($"{judged.Count} sung lines, too few to judge");

        var reach = (int)Math.Round(Reach / fs);
        var longest = judged.Max(j => j.Tokens.Length) * 2 + 1;
        var now = new double[longest];
        var next = new double[longest];

        double[] Gains(int shift)
        {
            var gains = new double[judged.Count];
            for (var j = 0; j < judged.Count; j++)
                gains[j] = Gain(heard, judged[j].Opens + shift, judged[j].Frames, judged[j].Tokens, now, next);
            return gains;
        }

        // Coarse, then fine around the best coarse shift. Zero is on the coarse grid.
        var tried = new Dictionary<int, double>();
        var zero = Gains(0);
        tried[0] = zero.Sum();
        // Each line's own best coarse shift, to see how many lines agree with the best one for the whole sheet.
        var lineBest = new int[judged.Count];
        var lineTop = new double[judged.Count];
        Array.Fill(lineTop, double.NegativeInfinity);
        for (var k = -reach - (-reach % CoarseStep); k <= reach; k += CoarseStep)
        {
            var g = k == 0 ? zero : Gains(k);
            tried[k] = g.Sum();
            for (var j = 0; j < g.Length; j++)
                if (g[j] > lineTop[j]) { lineTop[j] = g[j]; lineBest[j] = k; }
        }

        var coarse = tried.MaxBy(p => p.Value).Key;
        for (var k = Math.Max(-reach, coarse - CoarseStep + 1); k <= Math.Min(reach, coarse + CoarseStep - 1); k++)
            if (!tried.ContainsKey(k))
                tried[k] = Gains(k).Sum();

        var best = tried.MaxBy(p => p.Value).Key;
        var bestSeconds = best * fs;
        var gained = tried[best] - tried[0];

        // How many lines agree with the best shift on their own: their own best coarse shift is near it.
        var near = (int)Math.Round(AgreeSeconds / fs);
        var agree = lineBest.Count(k => Math.Abs(k - best) <= near);
        Estimate Keep(string why) => new(0, bestSeconds, judged.Count, agree, why);

        if (Math.Abs(bestSeconds) < Least)
            return Keep("the sheet is in time");
        // Where the sheet does not fit, the sum often keeps rising to the end of the search, and lines whose own best
        // is also at the end then seem to agree with it.
        if (Math.Abs(bestSeconds) > Reach - RivalDistance)
            return Keep("the best shift is at the end of the search");
        if (gained <= 0)
            return Keep("no shift fits better than none");
        if (agree < Math.Max(AgreeLines, AgreeShare * judged.Count))
            return Keep("too few lines agree");

        if (RivalPeak(tried, best, (int)Math.Round(RivalDistance / fs), gained) is { } rival
            && tried[rival] - tried[0] >= Rival * gained)
            return Keep($"a shift of {rival * fs:F2}s fits almost as well");

        return new Estimate(bestSeconds, bestSeconds, judged.Count, agree, "shifted");
    }

    /// <summary>How deep the dip between two peaks must be, as a share of what the best shift gains, for the second
    /// to count as a peak of its own.</summary>
    private const double Dip = 0.1;

    /// <summary>
    /// The highest peak of <paramref name="tried"/> other than the one at <paramref name="best"/>, or null. A peak is a
    /// coarse shift that scores higher than both coarse neighbours, at least <paramref name="apart"/> frames from the
    /// best, with a dip between the two of at least <see cref="Dip"/>. Without the dip, a point on the side of the best
    /// peak would count as a rival. The lead cost makes that side a slope, with values close to the top.
    /// </summary>
    private static int? RivalPeak(Dictionary<int, double> tried, int best, int apart, double gained)
    {
        var grid = tried.Keys.Where(k => k % CoarseStep == 0).Order().ToArray();
        int? rival = null;
        for (var i = 1; i < grid.Length - 1; i++)
        {
            var k = grid[i];
            if (Math.Abs(k - best) <= apart || tried[k] <= tried[grid[i - 1]] || tried[k] < tried[grid[i + 1]])
                continue;

            var valley = double.MaxValue;
            foreach (var j in grid)
                if (j > Math.Min(k, best) && j < Math.Max(k, best))
                    valley = Math.Min(valley, tried[j]);
            if (tried[k] - valley < Dip * gained)
                continue;

            if (rival is null || tried[k] > tried[rival.Value])
                rival = k;
        }

        return rival;
    }

    /// <summary>One sung line, as the search uses it: its window in frames at no shift, and its tokens.</summary>
    private sealed record Judged(int Opens, int Frames, int[] Tokens);

    /// <summary>
    /// How much better the line's letters explain the frames from <paramref name="opens"/> than silence does: the most
    /// likely path through the letters, as in <see cref="CtcAligner"/>, less the all-blank path. A frame spent on the
    /// blank before the first letter also costs <see cref="LeadCost"/>. Only the score is kept, not the path, so this
    /// is much cheaper than an alignment.
    /// </summary>
    private static double Gain(HeardRecord heard, int opens, int frames, int[] tokens, double[] now, double[] next)
    {
        var blank = heard.Blank;
        var states = 2 * tokens.Length + 1;

        Array.Fill(now, double.NegativeInfinity, 0, states);
        now[0] = heard.At(opens, blank) - LeadCost;
        now[1] = heard.At(opens, tokens[0]);
        var silence = now[0];

        for (var t = 1; t < frames; t++)
        {
            var f = opens + t;
            var blankHere = heard.At(f, blank);
            silence += blankHere;

            for (var s = 0; s < states; s++)
            {
                var here = now[s];
                if (s >= 1 && now[s - 1] > here)
                    here = now[s - 1];
                // A blank between two different letters may be skipped. Between two equal letters it may not.
                if (s >= 2 && (s & 1) == 1 && tokens[(s - 1) / 2] != tokens[(s - 3) / 2] && now[s - 2] > here)
                    here = now[s - 2];

                next[s] = double.IsNegativeInfinity(here)
                    ? double.NegativeInfinity
                    : here + (s == 0 ? blankHere - LeadCost : (s & 1) == 0 ? blankHere : heard.At(f, tokens[(s - 1) / 2]));
            }

            (now, next) = (next, now);
        }

        var end = Math.Max(now[states - 1], now[states - 2]);
        return double.IsNegativeInfinity(end) ? Outside * tokens.Length : end - silence;
    }

    /// <summary>Frames of silence: the blank certain, every letter at <see cref="Outside"/>.</summary>
    private static float[] Silence(int frames, int vocab, int blank)
    {
        var log = new float[frames * vocab];
        Array.Fill(log, (float)Outside);
        for (var f = 0; f < frames; f++)
            log[f * vocab + blank] = 0;
        return log;
    }

    /// <summary>A log-softmax of one frame. It changes nothing in scores that are already log-probabilities.</summary>
    private static void Normalise(ReadOnlySpan<float> scores, Span<float> into)
    {
        var peak = double.NegativeInfinity;
        foreach (var v in scores)
            if (v > peak)
                peak = v;

        double sum = 0;
        foreach (var v in scores)
            sum += Math.Exp(v - peak);

        var offset = peak + Math.Log(sum);
        for (var i = 0; i < scores.Length; i++)
            into[i] = (float)(scores[i] - offset);
    }
}
