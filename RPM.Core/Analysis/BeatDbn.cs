namespace RPM.Core.Analysis;

/// <summary>
/// Reads beats from the model's frame-by-frame scores. It finds the single most likely path of
/// beats through the whole track, and does not pick the peaks one at a time.
///
/// <para>This is Böck's dynamic Bayesian network, the same one that madmom implements, and the one
/// that <c>beat_this</c> uses when its own peak picker is not enough. The hidden state is a pair:
/// the tempo, and how far through the current beat the track is. At every frame the state moves
/// one step forward. The tempo may change only at the end of a beat, and the change has a cost.
/// The Viterbi algorithm then finds the single path through the track that best explains the
/// model's scores.</para>
///
/// <para><b>Why the extra calculation is worth it.</b> Peak picking has no memory. Each frame is
/// judged only against its neighbours, so if the model marks one beat twice, eighty milliseconds
/// apart, the result is two beats, and a quiet bar gives none. Neither can happen here, because a
/// path cannot leave one beat and arrive at the next in four frames, and it cannot stand still. The
/// result is a sequence of beats that a real track could play. A mix needs exactly that: the beat
/// the track was keeping, not the frames that the model scored highly.</para>
///
/// <para>The tempo range is a limit on the search, not a reading. It defines which paths exist, and
/// the track still chooses its own path. The range is wider than the range in which tempo values
/// are stored.</para>
/// </summary>
public static class BeatDbn
{
    /// <summary>The tempo range of the search, as <c>beat_this</c> sets it. It is wide enough for
    /// a quickstep at one end and a ballad at the other.</summary>
    public const double MinBpm = 55, MaxBpm = 215;

    /// <summary>The cost of a tempo change from one beat to the next. It is high, so the search
    /// prefers a track that holds its tempo and changes it only where the evidence is strong. A
    /// sequencer holds tempo exactly, and a band holds it nearly.</summary>
    public const double TransitionLambda = 100;

    /// <summary>How much of a beat counts as being on the beat. It is a sixteenth: in the first
    /// 1/16th of the beat the model's score is read as "this is a beat", and in the rest as "this
    /// is not".</summary>
    public const double ObservationLambda = 16;

    /// <summary>Scores are read as probabilities, and a probability of exactly zero or one has no
    /// logarithm. The result does not depend on the exact width of the clamp.</summary>
    private const double Clamp = 1e-7;

    /// <summary>
    /// The beats, in seconds, of a track whose per-frame beat scores are <paramref name="logits"/>.
    /// </summary>
    /// <param name="logits">The model output before any peak picking: one score per frame, on the
    /// logit scale, as <see cref="BeatModelDecoder"/> joins the windows together.</param>
    /// <param name="frameRate">Frames per second. This model uses 50.</param>
    public static double[] Beats(float[] logits, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(logits);
        if (logits.Length < 2 || frameRate <= 0)
            return [];

        var space = StateSpace.For(frameRate);
        if (space.States == 0)
            return [];

        var path = Viterbi(logits, space);
        return path.Length == 0 ? [] : Align(path, logits, space, frameRate);
    }

    /// <summary>How many states the search runs over at a given frame rate. It is public because
    /// it is the one number that shows whether the state space has the same shape as the
    /// reference's.</summary>
    public static int StateCount(double frameRate) => StateSpace.For(frameRate).States;

    /// <summary>
    /// The state of the track at each frame: the path itself, before it is read as beats.
    ///
    /// <para>It is public for the reference test. Two searches can agree on every beat and still be
    /// different searches. They can also differ on one beat for a reason that has nothing to do with
    /// the search. So the test compares this path with madmom's. A disagreement then points to one
    /// frame and one state, and not to a list of times that must be matched up.</para>
    /// </summary>
    public static int[] Path(float[] logits, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(logits);
        var space = StateSpace.For(frameRate);
        return logits.Length < 2 || space.States == 0 ? [] : Viterbi(logits, space);
    }

    /// <summary>
    /// Every (tempo, position in the beat) pair that the track may be in.
    ///
    /// <para>A tempo is stored as the whole number of frames in a beat, from the fastest allowed
    /// tempo to the slowest. A beat of <c>n</c> frames has <c>n</c> positions. States are laid out
    /// one tempo after another, so moving through a beat means moving through consecutive state
    /// numbers, and the calculations below are all offsets.</para>
    /// </summary>
    private readonly struct StateSpace
    {
        /// <summary>Beat lengths in frames, shortest first.</summary>
        public required int[] Intervals { get; init; }

        /// <summary>Where each interval's first state sits in the state numbering.</summary>
        public required int[] Offsets { get; init; }

        /// <summary>Which interval each state belongs to.</summary>
        public required int[] IntervalOf { get; init; }

        /// <summary>How far through its beat each state is, in frames.</summary>
        public required int[] PositionOf { get; init; }

        public required int States { get; init; }

        /// <summary>The cost, in logs, of leaving a beat at interval <c>from</c> and starting one at
        /// <c>to</c>. It is indexed by <c>from * Intervals.Length + to</c>.</summary>
        public required double[] Change { get; init; }

        public static StateSpace For(double frameRate)
        {
            var intervals = TempoStates.Intervals(frameRate, MinBpm, MaxBpm);
            if (intervals.Length == 0)
                return new StateSpace
                {
                    Intervals = [], Offsets = [], IntervalOf = [], PositionOf = [],
                    States = 0, Change = [],
                };

            int count = intervals.Length;
            var offsets = new int[count];
            int states = 0;
            for (int i = 0; i < count; i++)
            {
                offsets[i] = states;
                states += intervals[i];
            }

            var intervalOf = new int[states];
            var positionOf = new int[states];
            for (int i = 0; i < count; i++)
            {
                for (int p = 0; p < intervals[i]; p++)
                {
                    intervalOf[offsets[i] + p] = i;
                    positionOf[offsets[i] + p] = p;
                }
            }

            var change = TempoStates.LogChange(intervals, TransitionLambda);

            return new StateSpace
            {
                Intervals = intervals,
                Offsets = offsets,
                IntervalOf = intervalOf,
                PositionOf = positionOf,
                States = states,
                Change = change,
            };
        }

        /// <summary>
        /// Whether a state is close enough to the start of its beat to be read as a beat.
        ///
        /// <para>The reference compares the position as a <em>fraction</em> of the beat. A beat of
        /// n frames has positions 0/n to (n-1)/n, and the first sixteenth of those is on the beat.
        /// Comparing whole frames and letting the division truncate would lose a state at every
        /// interval that is not a multiple of sixteen. At 55 frames per beat it would give three
        /// states where the reference has four.</para>
        /// </summary>
        public bool IsOnTheBeat(int state) =>
            PositionOf[state] / (double)Intervals[IntervalOf[state]] < 1.0 / ObservationLambda;
    }

    /// <summary>
    /// The most likely path through the track, as one state per frame.
    ///
    /// <para>Only the states at the start of a beat have a choice of where they came from, and there
    /// are as many of those as there are tempos. Every other state can only have come from the
    /// previous frame in the same beat. So the stored trail is one number per tempo per frame, not
    /// one per state per frame. On a long track this is the difference between two megabytes and
    /// two hundred.</para>
    /// </summary>
    private static int[] Viterbi(float[] logits, StateSpace space)
    {
        int frames = logits.Length, states = space.States, tempos = space.Intervals.Length;

        var score = new double[states];
        var next = new double[states];
        var trail = new int[(long)frames * tempos <= int.MaxValue ? frames * tempos : 0];
        if (trail.Length == 0)
            return [];

        // The starting distribution describes where the track was before the first frame, not the
        // first frame itself, so the first frame is reached like every other frame: by a transition.
        // This can look like an off-by-one error, but it is not. The reference works this way, and
        // starting at the second frame would change which path wins on a track that the search finds
        // ambiguous.
        Array.Fill(score, Math.Log(1.0 / states));

        for (int f = 0; f < frames; f++)
        {
            var (onBeat, offBeat) = Densities(logits[f]);

            // Where a new beat may come from: the end of a beat at any tempo, plus the cost of
            // changing to this tempo.
            for (int to = 0; to < tempos; to++)
            {
                double best = double.NegativeInfinity;
                int bestFrom = 0;
                for (int from = 0; from < tempos; from++)
                {
                    double weight = space.Change[from * tempos + to];
                    if (double.IsNegativeInfinity(weight))
                        continue;

                    double candidate = score[space.Offsets[from] + space.Intervals[from] - 1] + weight;
                    if (candidate > best)
                    {
                        best = candidate;
                        bestFrom = from;
                    }
                }

                next[space.Offsets[to]] = best;
                trail[f * tempos + to] = bestFrom;
            }

            // Everywhere else there is nothing to decide: the beat continues.
            for (int i = 0; i < tempos; i++)
            {
                int offset = space.Offsets[i], length = space.Intervals[i];
                for (int p = length - 1; p >= 1; p--)
                    next[offset + p] = score[offset + p - 1];
            }

            for (int s = 0; s < states; s++)
                next[s] += space.IsOnTheBeat(s) ? onBeat : offBeat;

            (score, next) = (next, score);
        }

        // Walk back from the best final state.
        var path = new int[frames];
        int at = 0;
        for (int s = 1; s < states; s++)
            if (score[s] > score[at])
                at = s;

        for (int f = frames - 1; f >= 0; f--)
        {
            path[f] = at;

            // Inside a beat there is only one way back. At the start of a beat, the trail says which
            // tempo the previous beat had, and the state is the last state of that beat. At the first
            // frame this walks back to the state before the track started, which nothing reads.
            if (space.PositionOf[at] > 0)
            {
                at--;
            }
            else
            {
                int from = trail[f * tempos + space.IntervalOf[at]];
                at = space.Offsets[from] + space.Intervals[from] - 1;
            }
        }

        return path;
    }

    /// <summary>
    /// The beats of a path: one per run of frames that the path spent on a beat, placed at the frame
    /// in that run with the highest model score.
    ///
    /// <para>The path gives the position of a beat to the nearest sixteenth of a beat, which is
    /// coarser than the model's resolution. Taking the best-scoring frame inside the run puts the
    /// beat where the model heard it. This is the same correction that madmom makes.</para>
    /// </summary>
    private static double[] Align(int[] path, float[] logits, StateSpace space, double frameRate)
    {
        var beats = new List<double>();
        int bestFrame = -1;

        // One beat per unbroken run of on-the-beat frames, at the frame in the run with the highest
        // model score. This is the reference's own correction, and it groups only by the run.
        for (int f = 0; f <= path.Length; f++)
        {
            if (f < path.Length && space.IsOnTheBeat(path[f]))
            {
                if (bestFrame < 0 || logits[f] > logits[bestFrame])
                    bestFrame = f;
            }
            else if (bestFrame >= 0)
            {
                beats.Add(bestFrame / frameRate);
                bestFrame = -1;
            }
        }

        return [.. beats];
    }

    /// <summary>
    /// What one frame's score says about a state, in logs. The reference reads the model's number
    /// as the probability that this frame is a beat, and divides the rest between the states that
    /// are not on the beat.
    /// </summary>
    private static (double OnBeat, double OffBeat) Densities(float logit)
    {
        double activation = Math.Clamp(TempoStates.Sigmoid(logit), Clamp, 1 - Clamp);
        return (Math.Log(activation), Math.Log((1 - activation) / (ObservationLambda - 1)));
    }
}
