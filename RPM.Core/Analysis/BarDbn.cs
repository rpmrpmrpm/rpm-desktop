namespace RPM.Core.Analysis;

/// <summary>
/// Reads the beats and the bar lines together from the model's frame-by-frame scores. It finds the
/// single most likely path of a <em>bar</em> through the whole track.
///
/// <para>This is the same search as <see cref="BeatDbn"/>, with one more item in the hidden state.
/// The state holds not only the tempo and the position in the beat, but also which beat of the bar
/// the track is on. This makes the beats and the bar lines agree by construction: a bar line is a
/// beat by definition, and not a separate reading that is moved onto the nearest beat. It also lets
/// a quiet "one" be found from the bar lines on either side of it, which peak picking cannot do
/// because it judges every frame on its own.</para>
///
/// <para>This is madmom's <c>DBNDownBeatTrackingProcessor</c>, called the way <c>beat_this</c> calls
/// it, which is how the published figures for this model's downbeats were measured. On GTZAN the
/// paper reports downbeat continuity going from 67.3 to 73.3 CMLt and from 79.1 to 87.8 AMLt
/// compared with the peak picker, at the cost of a point of F-measure. Continuity is the measure a
/// changeover needs: a mix is timed in bars, and a bar phase that is right on average but wrong for
/// eight bars in the middle is useless.</para>
///
/// <para>Two bar lengths are searched, three beats and four, and the track chooses. They are
/// separate searches over separate state spaces, and the one that explains the track better
/// wins.</para>
/// </summary>
public static class BarDbn
{
    /// <summary>The tempo range the search runs over, as <c>beat_this</c> sets it.</summary>
    public const double MinBpm = 55, MaxBpm = 215;

    /// <summary>The cost of a tempo change from one beat to the next.</summary>
    public const double TransitionLambda = 100;

    /// <summary>How much of a beat counts as being on the beat: the first sixteenth.</summary>
    public const int ObservationLambda = 16;

    /// <summary>The bar lengths that are searched, in the order in which madmom is asked for them.
    /// A tie goes to the first one, which is why three is written first: it is the smaller
    /// claim.</summary>
    public static readonly int[] BarLengths = [3, 4];

    /// <summary>
    /// How far a score is moved in from the ends of its range before the search sees it.
    ///
    /// <para>A probability of exactly 0 or 1 has no logarithm, and a model that is certain produces
    /// both. <c>beat_this</c> squeezes the sigmoid into (0, 1) by this amount instead of clamping.
    /// This gives different values from a clamp, and it is the method that the published figures
    /// were measured with.</para>
    /// </summary>
    private const double Squeeze = 1e-5;

    /// <summary>
    /// Frames quieter than this at both ends of the track are not searched at all.
    ///
    /// <para>This is madmom's own default. The track is trimmed to the stretch between the first and
    /// last frame that has a score above the threshold. The frames outside it are neither beats nor
    /// evidence. They are the run-in and the run-out, and searching them would only let the tempo
    /// change freely in silence.</para>
    /// </summary>
    private const double ActivationThreshold = 0.05;

    /// <summary>The result of the search: the beats, which of them are bar lines, and how many beats
    /// were counted in the bar. <see cref="BeatsPerBar"/> is 0 when there was nothing to read, and
    /// then both lists are empty.</summary>
    public readonly record struct Reading(double[] BeatTimes, double[] DownbeatTimes, int BeatsPerBar)
    {
        public static readonly Reading None = new([], [], 0);
    }

    /// <summary>
    /// The beats and bar lines of a track whose per-frame scores are the two given curves.
    /// </summary>
    /// <param name="beatLogits">What the model said about beats, one score per frame on the logit
    /// scale, as <see cref="BeatModelDecoder"/> joins the windows together.</param>
    /// <param name="downbeatLogits">The same for bar lines. It must have the same length.</param>
    /// <param name="frameRate">Frames per second. This model uses 50.</param>
    public static Reading Read(float[] beatLogits, float[] downbeatLogits, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(beatLogits);
        ArgumentNullException.ThrowIfNull(downbeatLogits);

        var (found, offBar, onBar, first) = Explore(beatLogits, downbeatLogits, frameRate);
        if (found.Length == 0)
            return Reading.None;

        var best = found[Best(found)];
        return best.Path is { Length: > 0 }
            ? Beats(best, offBar, onBar, first, frameRate)
            : Reading.None;
    }

    /// <summary>
    /// Every bar length searched over the same frames, and the frames themselves.
    ///
    /// <para>The searches are independent. Nothing is shared between them and neither reads the
    /// result of the other, so running them in parallel changes only the time taken.</para>
    /// </summary>
    private static (Result[] Found, double[] OffBar, double[] OnBar, int First) Explore(
        float[] beatLogits, float[] downbeatLogits, double frameRate)
    {
        int frames = Math.Min(beatLogits.Length, downbeatLogits.Length);
        if (frames < 2 || frameRate <= 0)
            return ([], [], [], 0);

        var (offBar, onBar) = Observed(beatLogits, downbeatLogits, frames);
        var (first, last) = Loud(offBar, onBar);
        if (last <= first)
            return ([], [], [], 0);

        var spaces = BarLengths.Select(beats => BarSpace.For(frameRate, beats)).ToArray();
        if (spaces.Any(s => s.States == 0))
            return ([], [], [], 0);

        var found = new Result[spaces.Length];
        Parallel.For(0, spaces.Length, i => found[i] = Search(offBar, onBar, first, last, spaces[i]));
        return (found, offBar, onBar, first);
    }

    /// <summary>
    /// Which bar length explained the track best. This is not a tie-break between two readings of
    /// one thing: a track is in three or it is in four, and the log probability decides. An exact
    /// tie goes to the first, which is the smaller claim, as in the reference.
    /// </summary>
    private static int Best(Result[] found)
    {
        int best = 0;
        for (int i = 1; i < found.Length; i++)
            if (found[i].LogProbability > found[best].LogProbability)
                best = i;
        return best;
    }

    /// <summary>
    /// The state of the track at each frame, the bar length that won, and what each bar length
    /// scored.
    ///
    /// <para>It is public for the reference test. Two searches can agree on every beat and still be
    /// different searches. They can also differ on one beat for a reason that has nothing to do with
    /// the search. So the test compares this path with madmom's. A disagreement then points to one
    /// frame and one state, and not to a list of times that must be matched up. The scores are
    /// compared too, because the choice between three beats to the bar and four is made on them,
    /// and it is sometimes close.</para>
    /// </summary>
    public static (int[] Path, int BeatsPerBar, double[] LogProbabilities) Score(
        float[] beatLogits, float[] downbeatLogits, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(beatLogits);
        ArgumentNullException.ThrowIfNull(downbeatLogits);

        var (found, _, _, _) = Explore(beatLogits, downbeatLogits, frameRate);
        if (found.Length == 0)
            return ([], 0, []);

        int best = Best(found);
        return (found[best].Path, BarLengths[best], [.. found.Select(r => r.LogProbability)]);
    }

    /// <summary>How many states each bar length is searched over. It is public because it is the
    /// one number that shows whether the state space has the same shape as the reference's.</summary>
    public static int StateCount(double frameRate, int beatsPerBar)
        => BarSpace.For(frameRate, beatsPerBar).States;

    /// <summary>Which of the three densities each state reads: 0 off the beat, 1 on a beat, 2 on a
    /// bar line. It is public for the reference test. A port that is wrong by one state at one
    /// interval still finds beats, but places them slightly wrong on slow tracks.</summary>
    public static byte[] Reads(double frameRate, int beatsPerBar)
        => BarSpace.For(frameRate, beatsPerBar).Reads ?? [];

    /// <summary>
    /// Every transition the search may take, destination by destination: which state may come
    /// before each one, in the order the search considers them, and what it costs in logs.
    ///
    /// <para>It is public for the reference test. It covers the half of the model that the path
    /// comparison does not check on its own. The order matters as much as the numbers: the search
    /// takes the first of two equally good predecessors, so a port that lists them in the other
    /// order breaks ties in the other way.</para>
    /// </summary>
    public static IEnumerable<(int To, int From, double LogProbability)> Transitions(
        double frameRate, int beatsPerBar)
    {
        var space = BarSpace.For(frameRate, beatsPerBar);
        if (space.States == 0)
            yield break;

        int tempos = space.Intervals.Length, beats = space.BeatsPerBar;
        for (int state = 0; state < space.States; state++)
        {
            int b = state / space.BeatStates, within = state % space.BeatStates;

            // Inside a beat there is one way in, and it is certain.
            if (space.PositionOf[within] > 0)
            {
                yield return (state, state - 1, 0);
                continue;
            }

            // At the start of a beat, the previous state is the end of the previous beat of the bar,
            // at any tempo that the change model allows (not every tempo is allowed).
            int previous = ((b - 1 + beats) % beats) * space.BeatStates;
            int i = space.IntervalOf[within];
            for (int j = 0; j < tempos; j++)
            {
                double weight = space.Change[j * tempos + i];
                if (!double.IsNegativeInfinity(weight))
                    yield return (state, previous + space.Offsets[j] + space.Intervals[j] - 1, weight);
            }
        }
    }

    /// <summary>
    /// The two curves as the three-way observation that the search needs: how much this frame is a
    /// beat that is <em>not</em> a bar line, and how much it is a bar line.
    ///
    /// <para><c>beat_this</c> builds it this way, and the detail matters. The third density of the
    /// search is one minus the other two. If a frame were both a beat and a bar line, it would be
    /// counted twice and the remainder would be negative, which has no logarithm.</para>
    ///
    /// <para>The squeeze away from 0 and 1 also comes from <c>beat_this</c>, and it gives different
    /// values from a clamp. A score the model is certain of becomes <c>1 - 5e-6</c> and not an
    /// arbitrary epsilon. The published figures for this model were measured with it.</para>
    /// </summary>
    private static (double[] OffBar, double[] OnBar) Observed(
        float[] beatLogits, float[] downbeatLogits, int frames)
    {
        var offBar = new double[frames];
        var onBar = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double beat = TempoStates.Sigmoid(beatLogits[f]) * (1 - Squeeze) + Squeeze / 2;
            double bar = TempoStates.Sigmoid(downbeatLogits[f]) * (1 - Squeeze) + Squeeze / 2;
            offBar[f] = Math.Max(beat - bar, Squeeze / 2);
            onBar[f] = bar;
        }

        return (offBar, onBar);
    }

    /// <summary>
    /// Where the track has a signal: the first frame, and one past the last frame, at which either
    /// curve reaches <see cref="ActivationThreshold"/>.
    ///
    /// <para>The range is empty when no frame reaches the threshold. It is also empty, on purpose,
    /// when the <em>only</em> such frame is the very first one. madmom tests its list of frame
    /// numbers for truth and not for length, and a list that holds only frame zero is false. This is
    /// a quirk and not a rule. It affects only a track that has a signal in its first twenty
    /// milliseconds and never again, and reproducing it is the purpose of a port.</para>
    /// </summary>
    private static (int First, int Last) Loud(double[] offBar, double[] onBar)
    {
        int first = -1, last = -1;
        for (int f = 0; f < offBar.Length; f++)
            if (offBar[f] >= ActivationThreshold || onBar[f] >= ActivationThreshold)
            {
                if (first < 0)
                    first = f;
                last = f;
            }

        return first < 0 || last == 0 ? (0, 0) : (first, last + 1);
    }

    /// <summary>The result of one search: the state of the track at each frame it covered, and how
    /// well that path explains the frames.</summary>
    private readonly record struct Result(int[] Path, double LogProbability, BarSpace Space)
    {
        public static readonly Result None = new([], double.NegativeInfinity, default);
    }

    /// <summary>
    /// Every (beat of the bar, tempo, position in the beat) combination that the track may be in.
    ///
    /// <para>The states of one beat (the same space that <see cref="BeatDbn"/> searches) are repeated
    /// once for each beat of the bar, so the state numbers run through beat one, then beat two, and
    /// so on. Within a beat the calculations are all offsets. The bar matters only at a beat
    /// boundary, where the next beat of the bar is reached, and the last beat of the bar wraps round
    /// to the first.</para>
    /// </summary>
    private readonly struct BarSpace
    {
        /// <summary>Beat lengths in frames, shortest first.</summary>
        public required int[] Intervals { get; init; }

        /// <summary>Where each interval's first state sits within one beat.</summary>
        public required int[] Offsets { get; init; }

        /// <summary>Which interval each state of one beat belongs to.</summary>
        public required int[] IntervalOf { get; init; }

        /// <summary>How far through its beat each state of one beat is, in frames.</summary>
        public required int[] PositionOf { get; init; }

        /// <summary>Which of the three densities each state reads: 0 off the beat, 1 on a beat, 2 on
        /// a bar line. There is one entry per state of the whole bar. It is calculated once and not
        /// for every frame, because it is read for about six thousand states in each frame and a
        /// track has about twelve thousand frames.</summary>
        public required byte[] Reads { get; init; }

        public required int BeatsPerBar { get; init; }

        /// <summary>How many states one beat of the bar holds.</summary>
        public required int BeatStates { get; init; }

        /// <summary>The cost, in logs, of leaving a beat at one tempo and starting the next at
        /// another. It is indexed by <c>from * Intervals.Length + to</c>.</summary>
        public required double[] Change { get; init; }

        public int States => BeatStates * BeatsPerBar;

        public static BarSpace For(double frameRate, int beatsPerBar)
        {
            var intervals = TempoStates.Intervals(frameRate, MinBpm, MaxBpm);
            if (intervals.Length == 0 || beatsPerBar < 1)
                return default;

            int count = intervals.Length;
            var offsets = new int[count];
            int beatStates = 0;
            for (int i = 0; i < count; i++)
            {
                offsets[i] = beatStates;
                beatStates += intervals[i];
            }

            var intervalOf = new int[beatStates];
            var positionOf = new int[beatStates];
            var reads = new byte[beatStates * beatsPerBar];
            for (int i = 0; i < count; i++)
            {
                for (int p = 0; p < intervals[i]; p++)
                {
                    int s = offsets[i] + p;
                    intervalOf[s] = i;
                    positionOf[s] = p;

                    // The reference compares the position as a fraction of the beat, calculated as
                    // p * (1/n). This is what numpy's linspace produces, and it gives the same
                    // result as p/n in this comparison for every state in the space.
                    if (p * (1.0 / intervals[i]) >= 1.0 / ObservationLambda)
                        continue;

                    // Every beat of the bar is "on the beat", and the first beat is also the bar line.
                    reads[s] = 2;
                    for (int b = 1; b < beatsPerBar; b++)
                        reads[b * beatStates + s] = 1;
                }
            }

            return new BarSpace
            {
                Intervals = intervals,
                Offsets = offsets,
                IntervalOf = intervalOf,
                PositionOf = positionOf,
                Reads = reads,
                BeatsPerBar = beatsPerBar,
                BeatStates = beatStates,
                Change = TempoStates.LogChange(intervals, TransitionLambda),
            };
        }
    }

    /// <summary>
    /// The most likely path through the track, as one state per frame.
    ///
    /// <para>Only the states at the start of a beat have a choice of where they came from. There are
    /// as many of them as beats in the bar times tempos: a hundred and sixty-eight for four beats,
    /// out of nearly six thousand states. So the stored trail is one number per beat of the bar per
    /// tempo per frame. On a four minute track this is eight megabytes and not two hundred and
    /// eighty.</para>
    /// </summary>
    private static Result Search(double[] offBar, double[] onBar, int from, int to, BarSpace space)
    {
        int frames = to - from;
        int states = space.States, tempos = space.Intervals.Length, beats = space.BeatsPerBar;
        int choices = beats * tempos;

        var score = new double[states];
        var next = new double[states];
        var trail = new int[(long)frames * choices <= int.MaxValue ? frames * choices : 0];
        if (trail.Length == 0)
            return Result.None;

        // The starting distribution describes where the track was before the first frame, not the
        // first frame itself, so the first frame is reached like every other frame: by a
        // transition. This can look like an off-by-one error, but it is not. The reference works
        // this way. The value is written as the log of one over the count, not as the negated log of
        // the count, because the two differ in the last bit, and this constant is the basis for the
        // comparison between a bar of three and a bar of four.
        Array.Fill(score, Math.Log(1.0 / states));

        int[] offsets = space.Offsets, intervals = space.Intervals;
        double[] change = space.Change;
        byte[] reads = space.Reads;
        int beatStates = space.BeatStates;

        Span<double> density = stackalloc double[3];

        for (int f = 0; f < frames; f++)
        {
            int at = from + f;
            density[0] = Math.Log((1.0 - (offBar[at] + onBar[at])) / (ObservationLambda - 1));
            density[1] = Math.Log(offBar[at]);
            density[2] = Math.Log(onBar[at]);

            // Where a new beat may come from: the end of the previous beat of the bar, at any tempo,
            // plus the cost of changing to this tempo. The first beat of the bar follows the last
            // beat. This is the one line that makes the search a bar search and not a beat search.
            for (int b = 0; b < beats; b++)
            {
                int previous = ((b - 1 + beats) % beats) * beatStates;
                for (int i = 0; i < tempos; i++)
                {
                    double best = double.NegativeInfinity;
                    int bestFrom = 0;
                    for (int j = 0; j < tempos; j++)
                    {
                        double weight = change[j * tempos + i];
                        if (double.IsNegativeInfinity(weight))
                            continue;

                        double candidate = score[previous + offsets[j] + intervals[j] - 1] + weight;
                        if (candidate > best)
                        {
                            best = candidate;
                            bestFrom = j;
                        }
                    }

                    next[b * beatStates + offsets[i]] = best;
                    trail[f * choices + b * tempos + i] = bestFrom;
                }
            }

            // Everywhere else there is nothing to decide: the beat continues.
            for (int b = 0; b < beats; b++)
            {
                int start = b * beatStates;
                for (int i = 0; i < tempos; i++)
                {
                    int offset = start + offsets[i], length = intervals[i];
                    for (int p = length - 1; p >= 1; p--)
                        next[offset + p] = score[offset + p - 1];
                }
            }

            for (int s = 0; s < states; s++)
                next[s] += density[reads[s]];

            (score, next) = (next, score);
        }

        // Walk back from the best final state.
        int end = 0;
        for (int s = 1; s < states; s++)
            if (score[s] > score[end])
                end = s;

        double probability = score[end];
        if (double.IsNegativeInfinity(probability) || double.IsNaN(probability))
            return Result.None;

        var path = new int[frames];
        int state = end;
        for (int f = frames - 1; f >= 0; f--)
        {
            path[f] = state;

            // Inside a beat there is only one way back. At the start of a beat, the trail says which
            // tempo the previous beat had, and the state is the last state of that beat, in the
            // previous beat of the bar (for the first beat of the bar, that is its last beat).
            int within = state % beatStates;
            if (space.PositionOf[within] > 0)
            {
                state--;
            }
            else
            {
                int b = state / beatStates;
                int j = trail[f * choices + b * tempos + space.IntervalOf[within]];
                state = ((b - 1 + beats) % beats) * beatStates + offsets[j] + intervals[j] - 1;
            }
        }

        return new Result(path, probability, space);
    }

    /// <summary>
    /// The beats of a path, and which of them are bar lines.
    ///
    /// <para>There is one beat for each unbroken run of frames that the path spent on a beat. It is
    /// placed at the frame in the run where either curve is highest. Both curves are used, because a
    /// bar line is marked on the bar-line curve and its beat may be flat on the other curve. The
    /// path gives the position of a beat only to the nearest sixteenth of a beat, which is coarser
    /// than the model resolution, so this step puts the beat where the model heard it.</para>
    /// </summary>
    private static Reading Beats(Result found, double[] offBar, double[] onBar, int first, double frameRate)
    {
        var space = found.Space;
        var path = found.Path;

        var beats = new List<double>();
        var bars = new List<double>();

        int runFrom = -1;
        for (int f = 0; f <= path.Length; f++)
        {
            bool onBeat = f < path.Length && space.Reads[path[f]] >= 1;
            if (onBeat)
            {
                if (runFrom < 0)
                    runFrom = f;
                continue;
            }

            if (runFrom < 0)
                continue;

            // The frame in the run where either curve was highest. The values are read in the
            // reference's own order (this frame's beat score, then its bar-line score, then the next
            // frame's), so that where two are exactly equal the earlier one wins, as in the reference.
            int peak = runFrom;
            double loudest = double.NegativeInfinity;
            for (int k = runFrom; k < f; k++)
            {
                if (offBar[first + k] > loudest)
                {
                    loudest = offBar[first + k];
                    peak = k;
                }
                if (onBar[first + k] > loudest)
                {
                    loudest = onBar[first + k];
                    peak = k;
                }
            }

            double seconds = (peak + first) / frameRate;
            beats.Add(seconds);

            // Which beat of the bar the run was on. It is taken from the state of the path at the beat
            // itself and not from the run, because a run cannot cover two beats of the bar.
            if (path[peak] < space.BeatStates)
                bars.Add(seconds);

            runFrom = -1;
        }

        return beats.Count == 0
            ? Reading.None
            : new Reading([.. beats], [.. bars], space.BeatsPerBar);
    }
}
