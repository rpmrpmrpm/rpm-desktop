namespace RPM.Core.Analysis;

/// <summary>
/// Everything the DJ needs to know about a record before it goes on a deck: how fast it is, where
/// its beats and bar lines fall, what key it is in, how hard it hits, and the two points to mix in
/// and out at.
///
/// <para>Produced once per file by <see cref="TrackAnalyzer"/> and kept in the library database, so
/// the second time a playlist is mixed nothing is analysed. <see cref="AnalyzerVersion"/> makes it
/// safe to improve the analysis: when it is increased, every stored row is analysed again.</para>
/// </summary>
public sealed record TrackAnalysis
{
    /// <summary>The version of the analysis. Increase it whenever a change to the analysis passes would
    /// give a different answer for the same file. It is stored with each row, and rows with a lower
    /// version are ignored and analysed again.</summary>
    /// <remarks>
    /// <para>The current analysis has these properties, which the stored rows depend on.</para>
    ///
    /// <para>The tempo comes from the model or not at all. The DSP reads the dotted quarter note as
    /// the beat on ordinary dance records (two thirds of the true tempo, at full confidence), and
    /// correcting it by an octave cannot recover the true tempo.</para>
    ///
    /// <para>The tempo is measured one stretch at a time (see <see cref="TempoWindows"/>) and the grid
    /// may follow it. The grid has one beat per index with no dropped or doubled beats. It is
    /// straightened to one line on every record where the bend would cost less than
    /// <see cref="Mixing.TransitionPlanner.MaxDriftSeconds"/>, which is most records.</para>
    ///
    /// <para>The beats are found by searching for the best path through the record, not by picking
    /// the peaks of the model's scores one frame at a time (see <see cref="BeatDbn"/>). Peak picking
    /// has no memory, so it could give the grid two beats eighty milliseconds apart, which real music
    /// does not do. The numbering could not merge them and counted each as a beat, so the record came
    /// out too fast. On the 661 dance records of GiantSteps Tempo this happened on a fifth of them
    /// and cost nine points of Accuracy1 and eleven of Accuracy2 (bench/RESULTS.md).</para>
    ///
    /// <para>The bar lines are searched for together with the beats (see <see cref="BarDbn"/>), and
    /// are not picked separately and snapped to the nearest beat. A bar line is a beat by
    /// construction and consecutive bar lines are exactly a bar apart. A mix needs this: every fade is
    /// measured in bars, and a bar phase that is right on average but wrong for eight bars in the
    /// middle of a changeover is worse than no bar phase. The Beat This! paper reports downbeat
    /// continuity on GTZAN going from 67.3 to 73.3 CMLt and from 79.1 to 87.8 AMLt with this method
    /// behind the model.</para>
    ///
    /// <para>A slow record is stored slow. Doubling a tempo below 70 BPM on its way to disk, over
    /// 1697 annotated tracks, fired 49 times, corrected nothing, and made 37 ballads twice as fast as
    /// they are (see <see cref="TempoEstimator.FoldDown"/>). Instead, <see cref="TempoOctave.OtherCount"/>
    /// says what else the record could be called, and the person who owns it decides.</para></remarks>
    public const int AnalyzerVersion = 14;

    /// <summary>
    /// Stamped in place of <see cref="AnalyzerVersion"/> when a trained model produced the reading
    /// and not the DSP passes.
    ///
    /// <para>Both are accepted from the cache, so a machine with no model does not have to analyse
    /// its whole library again at every launch. The stamp records which of the two a stored row came
    /// from, so that a library analysed while the model was unavailable can be found and redone
    /// later instead of staying worse for ever.</para>
    /// </summary>
    public const int ModelledVersion = ModelledOffset + AnalyzerVersion;

    /// <summary>What is added to a stamp to mark it as a model's reading. It is a round number far above
    /// any generation the analyser will reach, so the two parts of a stamp can always be separated
    /// again.</summary>
    public const int ModelledOffset = 1000;

    /// <summary>The analyser generation that a stored stamp came from, without the modelled marker.
    /// Compare stamps on this value, because a modelled row from an old generation is as out of date
    /// as a DSP one.</summary>
    public static int GenerationOf(int storedVersion) =>
        storedVersion >= ModelledOffset ? storedVersion - ModelledOffset : storedVersion;

    /// <summary>Whether a stored stamp still describes what the analyser would produce today.</summary>
    public static bool IsCurrent(int storedVersion) => GenerationOf(storedVersion) >= AnalyzerVersion;

    /// <summary>
    /// Whether a stored row is worth reading again because a model could now do better than
    /// whatever produced it.
    ///
    /// <para>The row this is true for is current, so nothing analyses it again, and was made by the
    /// DSP, so it is the worse of the two readings the app can produce. Without this check it stays
    /// that way for the life of the library.</para>
    ///
    /// <para>An out-of-date row is not upgraded here. It is analysed again by the normal path, and
    /// returning true for it would queue the same decode twice.</para>
    /// </summary>
    /// <param name="modelAvailable">False on a machine with no weights to read, where analysing the
    /// row again would spend a whole decode and reach the same answer.</param>
    public static bool WantsModelUpgrade(int storedVersion, bool modelAvailable) =>
        modelAvailable && storedVersion < ModelledOffset && IsCurrent(storedVersion);

    /// <summary>The file this describes, as an absolute path.</summary>
    public required string FilePath { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary>Beats per minute, folded into <see cref="TempoEstimator.MinBpm"/>..
    /// <see cref="TempoEstimator.MaxBpm"/>. Zero when the track has no tempo worth tracking.</summary>
    public double Bpm { get; init; }

    /// <summary>How clear the tempo lock was, 0..1. A spoken-word track or a rubato ballad has a tempo
    /// and a low value here. The DJ uses it to decide whether to beat-match or only fade.</summary>
    public double BpmConfidence { get; init; }

    /// <summary>Every beat, in seconds from the start of the file. Empty when untracked.</summary>
    public IReadOnlyList<double> BeatTimes { get; init; } = [];

    /// <summary>Index into <see cref="BeatTimes"/> of the first bar line. Every beat at that index
    /// modulo <see cref="BeatsPerBar"/> is a downbeat.</summary>
    public int FirstDownbeat { get; init; }

    /// <summary>
    /// How clearly the "one" stood out from the other three beats of the bar, 0..1. Some records are
    /// unclear: a loop with no harmonic movement and a kick on every beat has four identical
    /// candidates. <see cref="FirstDownbeat"/> is still the best of the four and is still what the mix
    /// aligns to. A beat chosen without knowing the bar is a bar phase chosen at random, so using beat
    /// alignment instead would land on the wrong beat of the bar just as often, without trying. This
    /// number says how much to trust the reading, not whether to use it.
    /// </summary>
    public double DownbeatConfidence { get; init; } = 1;

    /// <summary>The value below which a reading is worth little. A quarter is already a winner that is
    /// clear of its rivals. Below that, the four phases are effectively tied.</summary>
    public const double MinDownbeatConfidence = 0.25;

    public int BeatsPerBar { get; init; } = 4;

    /// <summary>How many beats apart the lines this track is aligned to are.</summary>
    private int AlignEvery => Math.Max(1, BeatsPerBar);

    public MusicalKey Key { get; init; } = MusicalKey.Unknown;

    public double KeyConfidence { get; init; }

    /// <summary>Loudness every <see cref="EnergyProfile.StepSeconds"/>, and the one-number summary
    /// the planner sorts on.</summary>
    public IReadOnlyList<float> EnergyCurve { get; init; } = [];

    public double Intensity { get; init; }

    /// <summary>Where the intro ends: the earliest sensible point to let this record be heard.</summary>
    public double IntroEndSeconds { get; init; }

    /// <summary>Where the outro begins: the point to start mixing the next record in over it.</summary>
    public double OutroStartSeconds { get; init; }

    /// <summary>
    /// Where there is first anything to hear, measured against an absolute floor and not against the
    /// record's own level. Zero for a track that starts at its first sample, and for every row
    /// analysed before this was measured.
    /// </summary>
    /// <remarks>This is not a cue point. <see cref="IntroEndSeconds"/> is where the record gets going,
    /// which is a judgement about the music. This is the boundary between audio and no audio, which is
    /// a measurement.</remarks>
    public double AudioStartSeconds { get; init; }

    /// <summary>Where there is last anything to hear. Zero when it was never measured, which means
    /// "not known" everywhere it is used (see <see cref="AudibleEndSeconds"/>).</summary>
    public double AudioEndSeconds { get; init; }

    /// <summary>The first beat that the tracker actually held, as opposed to the first beat of the grid.
    /// Zero when not measured.</summary>
    public double BeatFirstSeconds { get; init; }

    /// <summary>
    /// The last beat that the tracker actually held.
    ///
    /// <para>The grid does not stop there. <see cref="BeatTracker.Regularize"/> carries one straight
    /// line to both ends of the track on purpose, because a record's intro and outro are where a
    /// changeover needs a bar line, and the tracker will not follow an onset in either. This value
    /// shows which part of the grid was <em>measured</em> and which part is the continued line.</para>
    /// </summary>
    public double BeatLastSeconds { get; init; }

    // ── what the record is made of ──
    //
    // Four curves on the same clock as EnergyCurve, and four figures about the shape. All of it is
    // calculated from readings that the analyser already had; none of it needs a second pass over the
    // audio. An empty curve and a zero mean "not measured", and every reader below treats them that
    // way.

    /// <summary>The bottom end over time: the kick and the bassline. A bass swap moves this from one
    /// record to the other, and it shows whether the beat has started.</summary>
    public IReadOnlyList<float> BassCurve { get; init; } = [];

    /// <summary>The middle over time: voices, guitars, and the body of most instruments.</summary>
    public IReadOnlyList<float> MidCurve { get; init; } = [];

    /// <summary>The top over time: hats and air.</summary>
    public IReadOnlyList<float> TopCurve { get; init; } = [];

    /// <summary>How much of the record is in the range of a voice, 0..1 per step. It is not a singer
    /// detector, but it is enough to stop a changeover from putting two voices on top of each
    /// other.</summary>
    public IReadOnlyList<float> VocalCurve { get; init; } = [];

    /// <summary>True when the bands were measured. False for every row analysed before they were
    /// measured.</summary>
    public bool HasBands => BassCurve.Count > 0;

    /// <summary>Where the record's phrases fall (see <see cref="TrackShape.Phrases"/>). A mix that
    /// lands on a phrase sounds intended. A mix that lands on an arbitrary bar sounds wrong.</summary>
    public PhraseGrid Phrases { get; init; }

    /// <summary>
    /// How steadily the record is played, 0..1 (see <see cref="TrackShape.TempoStability"/>). It shows
    /// how hard a deck may be pulled, and for how long.
    ///
    /// <para>Exactly zero means that it was not measured, which is not the same as a record that
    /// wanders. It cannot be taken from the pool, because it is the difference between where the beats
    /// were heard and where the straightened grid puts them, and only the grid is shared. Read it
    /// through <see cref="IsSteadyEnoughToHold"/> and not as a number, or an unknown value would be
    /// treated as the worst case on every record that the pool answered for.</para>
    /// </summary>
    public double TempoStability { get; init; }

    /// <summary>
    /// Whether this record can be held on a bent tempo for the length of a long changeover.
    ///
    /// <para>True when it was measured and found steady, and also true when it was not measured. Most
    /// records are sequenced, and treating an unknown value as a drifting record would shorten every
    /// fade in a library that the pool had answered for. False only where a machine analysed the
    /// audio and found otherwise.</para>
    /// </summary>
    public bool IsSteadyEnoughToHold =>
        TempoStability == TrackShape.Unmeasured || TempoStability > 0.4;

    /// <summary>Where the beat comes back after a breakdown, in seconds. These are the best places in
    /// the record to land another one. Empty for a record with no breakdown.</summary>
    public IReadOnlyList<double> DropSeconds { get; init; } = [];

    /// <summary>How loud the record is where a changeover meets it: at the intro cue and at the outro
    /// cue, on the scale of <see cref="Intensity"/>. Zero when not measured.</summary>
    public double IntroLevel { get; init; }

    public double OutroLevel { get; init; }

    /// <summary>Whether the edges were measured at all. False for every row analysed before they were
    /// measured. The code below then behaves as it does without edge information.</summary>
    public bool HasEdges => AudioEndSeconds > AudioStartSeconds;

    /// <summary>
    /// Where this record stops being audible: the measured end, or the file's own end when nothing
    /// measured it. A changeover must not be timed after this point.
    /// </summary>
    public double AudibleEndSeconds => HasEdges ? AudioEndSeconds : DurationSeconds;

    /// <summary>
    /// Whether the grid at this point is a continued line and not beats that were heard.
    ///
    /// <para>Always false where the span was never measured, so a row that was analysed before this
    /// measurement is treated as before.</para>
    /// </summary>
    public bool IsExtrapolatedAt(double seconds) =>
        BeatLastSeconds > 0 && seconds > BeatLastSeconds + 1e-6;

    /// <summary>
    /// Whether the grid here is extrapolated in a place where it matters.
    ///
    /// <para><see cref="IsExtrapolatedAt"/> is true across the tail of nearly every record, and that
    /// is not a fault: the grid is carried to the end of the file on purpose, while the tracker stops
    /// at the last drum it hears. A fade-out, the reverb behind it and the silence after it all come
    /// after the last tracked beat on almost every record. Warning about all of them would hide the
    /// case that matters: a grid counting bars by arithmetic while the record is <em>still
    /// playing</em>, which means the tracker has given up on music that is still there.</para>
    ///
    /// <para>Where the edges were never measured this uses the whole file, so a row that was analysed
    /// before the edges existed still shows the warning.</para>
    /// </summary>
    public bool IsExtrapolatedOverMusicAt(double seconds) =>
        IsExtrapolatedAt(seconds) && seconds <= AudibleEndSeconds;

    /// <summary>
    /// How far two readings of one record's length may differ and still be readings of the same
    /// record. Causes of a small difference are encoder padding, a decoder's rounding, and a tag
    /// written by another program.
    ///
    /// <para>Beyond this they are measurements of two different pieces of audio. It is the one cheap
    /// test for a reading that was borrowed under a name that fits two records (see
    /// <see cref="Mixing.MixTrack.DurationSeconds"/>).</para>
    /// </summary>
    public const double SameRecordSeconds = 2;

    public int Version { get; init; } = AnalyzerVersion;

    /// <summary>True when a trained model read this track rather than the DSP passes.</summary>
    public bool Modelled => Version >= ModelledVersion;

    /// <summary>
    /// True when a person moved something in this reading by hand (see <see cref="GridEdit"/>).
    ///
    /// <para>This is not a database column and not a measured fact. It lives only as long as the set
    /// that is being worked on, because the two usual places for a reading are both wrong for a hand
    /// edit. The cache is stamped with an analyser generation and would return the edit as though the
    /// analyser had produced it, and the pool would serve one machine's opinion to other users as a
    /// measurement. An edit is useful for its difference from what was measured, and that difference
    /// is recorded where it can be read, not merged into the reading where nobody can tell which is
    /// which.</para>
    /// </summary>
    public bool IsEdited { get; init; }

    /// <summary>
    /// How sure the tempo must be before two records are matched to it and not simply faded.
    ///
    /// <para>The value comes from measurement. Over the 661 dance records of GiantSteps Tempo
    /// (tools/TempoBench, bench/RESULTS.md), the readings that pass this gate were compared with the
    /// tempo that the buyers of those records wrote down:</para>
    ///
    /// <code>
    ///   gate   beat-matched   right when matched
    ///   0.00          100 %              72.6 %
    ///   0.15           77 %              86.6 %
    ///   0.30           71 %              88.3 %
    ///   0.50           61 %              89.5 %
    ///   0.80           32 %              90.9 %
    /// </code>
    ///
    /// <para>A gate of 0.15 halves the mistakes and gives most of what a gate can give. 0.30 gains
    /// another point and a half for six percent of the library. This is worth it: a record that is
    /// faded and not matched is still mixed, and a record matched to the wrong tempo goes out of time
    /// in front of the room. Above 0.5 the curve is flat and coverage is lost for no gain.</para>
    ///
    /// <para>The gate cannot do better than about 89 % whatever its value, because it measures whether
    /// the grid fits the beats, and a grid at half the tempo fits them perfectly. Half of the errors
    /// that still pass are of this kind (an octave), which <see cref="Mixing.TransitionPlanner"/>
    /// re-levels at the meeting anyway.</para>
    ///
    /// <para><b>Precision sets the final value.</b> Being at the right tempo is not the same as being
    /// precise enough to hold a changeover: a beat-match needs the tempo to about a twentieth of a
    /// percent, while Accuracy1 accepts four percent. To measure this, the two halves of each record
    /// were read separately. A sequenced record has one tempo, so the difference between the halves
    /// is the reading's own noise. The result over the same 661 dance records:</para>
    ///
    /// <code>
    ///   confidence      tracks   median error   inside 0.05 %
    ///   0.00 - 0.15         60       0.0755 %          41.7 %
    ///   0.15 - 0.30         13       3.7634 %           0.0 %
    ///   0.30 - 0.60         41       2.4202 %           0.0 %
    ///   0.60 - 1.00        546       0.0074 %          76.7 %
    /// </code>
    ///
    /// <para>The change is sudden and not gradual. Above 0.60 the tempo is accurate to seven times
    /// better than a changeover needs. Between 0.15 and 0.60, not one record in fifty-four was
    /// inside the limit. A gate at 0.30 would match forty-one records that have no chance of holding.
    /// A gate at 0.60 costs six percent of the library and removes a band that never worked.</para>
    /// </summary>
    public const double MinBeatMatchConfidence = 0.60;

    /// <summary>True when there is enough to beat-match: a tempo, a confident one, and a grid to
    /// align to. Everything else can still be mixed, with a plain fade.</summary>
    public bool CanBeatMatch => Bpm > 0 && BpmConfidence >= MinBeatMatchConfidence && BeatTimes.Count >= 8;

    /// <summary>The bar line at or before <paramref name="seconds"/>, for lining up a transition.
    /// It returns the time itself when there is no grid at all.</summary>
    public double BarAtOrBefore(double seconds) =>
        CuePointFinder.SnapToBar(seconds, BeatTimes, FirstDownbeat, AlignEvery, forward: false);

    /// <summary>The bar line at or after <paramref name="seconds"/>. This is where a record coming in
    /// starts, so that it starts on a bar line and not part way through a bar.</summary>
    public double BarAtOrAfter(double seconds) =>
        CuePointFinder.SnapToBar(seconds, BeatTimes, FirstDownbeat, AlignEvery, forward: true);

    /// <summary>Seconds per bar at this track's tempo. Zero when there is no tempo.</summary>
    public double BarSeconds => Bpm > 0 ? 60.0 / Bpm * BeatsPerBar : 0;

    /// <summary>
    /// How fast the record is <em>at a given point</em>, as opposed to <see cref="Bpm"/>, which is
    /// one number for the whole track.
    ///
    /// <para>A changeover should be planned against this value. A sync taken at the start of a
    /// record cannot be trusted four minutes later, so the two records should be lined up just before
    /// the mix. A record that drifts half a percent has a grid that, extrapolated from its first beat,
    /// is a second out by its outro, while over the sixteen bars that a mix lasts the error is a tenth
    /// of that. The tempo a changeover needs is the tempo where the changeover happens.</para>
    ///
    /// <para>The value is read directly from <see cref="BeatTimes"/>, which <see cref="ModelBeatGrid"/>
    /// bends to follow the record wherever the bend was worth keeping. It needs no extra storage, it
    /// travels through the pool with the grid it is read from, and it equals <see cref="Bpm"/> exactly
    /// on every record whose grid is a straight line.</para>
    ///
    /// <para>The value is measured over a bar, not between two beats: one gap is one rounding, and a
    /// bar is still short enough that nothing a record does inside it is averaged away.</para>
    /// </summary>
    public double BpmAt(double seconds) => BpmAt(seconds, AlignEvery);

    /// <summary>
    /// The same, over a span that the caller names, which should be the span it is about to plan
    /// against.
    ///
    /// <para><b>The span decides the accuracy.</b> A tempo taken over four beats is taken over about
    /// two seconds, and the beats at each end are where the model put them, which is a frame or two
    /// from where the music is. Sixteen milliseconds at each end of two seconds is a tempo error of
    /// one and a half percent. A changeover planned on it drifts a quarter of a second away from the
    /// record it must follow, while <see cref="Mixing.TransitionPlanner.MaxDriftSeconds"/> allows only
    /// fifteen milliseconds for the whole changeover. Over sixty-four beats the same scatter is an
    /// eighth of that before the fit is counted, and the fit removes most of what is left.</para>
    ///
    /// <para>So the method fits a line through every beat in the span, not the gap between the two
    /// beats at its ends. Least squares over n beats uses all n, while a division uses two and ignores
    /// the rest, and those two are the beats most likely to be a frame out.</para>
    /// </summary>
    /// <param name="spanBeats">How many beats to read over. For a mix, use the changeover's own
    /// length. For a display of what the deck is doing now, use one bar.</param>
    public double BpmAt(double seconds, int spanBeats)
    {
        int count = BeatTimes.Count;
        if (count < 2 || Bpm <= 0)
            return Bpm;

        int span = Math.Clamp(spanBeats, 1, count - 1);

        // The beat at or before the point, found by search. The grid is in order and may be bent, so
        // its spacing cannot be divided through.
        int at = BinarySearch(seconds);

        // Centre the window on the point where the record allows it, and move it inside the ends where
        // it does not. A mix at the very start or the very end of a record is planned against the
        // nearest bar that exists.
        int from = Math.Clamp(at - span / 2, 0, count - 1 - span);
        int to = from + span;

        // A line through the beats, by index against time. The slope is the period, whatever the
        // grid does on either side of the window.
        double meanIndex = (from + to) / 2.0;
        double meanTime = 0;
        for (int i = from; i <= to; i++)
            meanTime += BeatTimes[i];
        meanTime /= to - from + 1;

        double covariance = 0, variance = 0;
        for (int i = from; i <= to; i++)
        {
            double dIndex = i - meanIndex;
            covariance += dIndex * (BeatTimes[i] - meanTime);
            variance += dIndex * dIndex;
        }

        double period = variance > 0 ? covariance / variance : 0;
        return period > 0 ? 60.0 / period : Bpm;
    }

    /// <summary>
    /// Which bar of the grid a time falls in, numbered as <see cref="FirstWholeBar"/> numbers them:
    /// zero is the bar the drums come in on, and the lead-in is negative.
    ///
    /// <para>The bar is found by walking the grid, not by dividing the time by a bar length. The two
    /// agree only while the grid is evenly spaced, and <see cref="ModelBeatGrid"/> bends it on a
    /// record that does not hold one tempo. On those records, division is right at the start of the
    /// record and a bar or two out by the outro, which is where a set does its mixing.</para>
    /// </summary>
    public int BarIndexAt(double seconds)
    {
        if (BeatTimes.Count == 0)
            return 0;

        int at = BinarySearch(seconds);
        return (int)Math.Floor((at - FirstDownbeat) / (double)AlignEvery) - FirstWholeBar;
    }

    /// <summary>
    /// How long the bar containing <paramref name="seconds"/> actually is, read from the grid itself.
    /// It returns <see cref="BarSeconds"/> where the grid does not reach.
    /// </summary>
    public double BarSecondsAt(double seconds)
    {
        int count = BeatTimes.Count;
        if (count < 2 || Bpm <= 0)
            return BarSeconds;

        int span = Math.Clamp(AlignEvery, 1, count - 1);
        int at = BinarySearch(seconds);

        // Start from the bar line at or before the point, or the last whole bar where the record ends
        // first.
        int from = Math.Clamp(at - (((at - FirstDownbeat) % span + span) % span), 0, count - 1 - span);
        double length = BeatTimes[from + span] - BeatTimes[from];
        return length > 0 ? length : BarSeconds;
    }

    /// <summary>The index of the last beat at or before a time, or zero when the grid starts after
    /// it.</summary>
    private int BinarySearch(double seconds)
    {
        int low = 0, high = BeatTimes.Count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (BeatTimes[mid] <= seconds)
                low = mid;
            else
                high = mid - 1;
        }
        return low;
    }

    // ── the four ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The length of the unit that a changeover is lined up on, in bars.
    ///
    /// <para>The value is four, not <see cref="Phrases"/>. The measured phrase is the span that a
    /// record repeats over, and it is eight, sixteen or thirty-two bars. That is the right value for
    /// deciding where a record can be joined at all, but it is too coarse to bring a record in on. A
    /// changeover is heard against the four: if it lands on one it sounds intended, and if it lands
    /// between two it sounds wrong.</para>
    /// </summary>
    public const int PhraseBars = 4;

    /// <summary>
    /// Which bar of the grid is the song's first whole bar.
    ///
    /// <para>This is not bar zero, and that is the main difficulty. The grid is one straight line
    /// carried to both ends of the file on purpose (see <see cref="BeatTracker.Regularize"/>), so it
    /// names bar lines in the silence before the music. Which of the first few beats
    /// <see cref="FirstDownbeat"/> lands on is a fact about the file and not about the song. Counting
    /// fours from there puts every four one, two or three bars away from the fours that the record is
    /// written in.</para>
    ///
    /// <para>So the count starts at the bar the drums come in on (see
    /// <see cref="DrumsStartSeconds"/>). Listeners count a record from that bar. It is not where the
    /// audio starts: a pad, a vocal or a riser before the beat is sound, but it is not the one. The
    /// bar <em>containing</em> the measurement is used, not the next one, because a measurement of
    /// where something started is taken a little way into the thing that started.</para>
    ///
    /// <para>A record whose bands were never measured uses the start of its audio. A record whose
    /// edges were not measured either counts from bar zero.</para>
    /// </summary>
    public int FirstWholeBar
    {
        get
        {
            double from = CountFromSeconds;
            if (from <= 0 || BeatTimes.Count == 0)
                return 0;

            // The last beat at or before it, which is the beat the drums came in on. A measurement of
            // where something started is taken a little way into the thing that started.
            int at = 0;
            while (at + 1 < BeatTimes.Count && BeatTimes[at + 1] <= from + 1e-6)
                at++;

            return (int)Math.Floor((at - FirstDownbeat) / (double)AlignEvery);
        }
    }

    /// <summary>
    /// What the count of fours is measured from: where the drums come in or, where the bands were
    /// not measured, the start of the audio less <see cref="LeadInBars"/>.
    /// </summary>
    private double CountFromSeconds =>
        DrumsStartSeconds is > 0 and var drums
            ? drums
            : HasEdges && AudioStartSeconds > 0
                ? AudioStartSeconds - LeadInBars * BarSeconds
                : 0;

    /// <summary>
    /// Where the drums come in, in seconds. Zero when the bands were never measured.
    ///
    /// <para>The value is read from <see cref="BassCurve"/>, because that band answers the question:
    /// a kick puts energy below 200 Hz, and a record's opening pad or vocal puts almost none there.
    /// The opening pad or vocal is what makes <see cref="AudioStartSeconds"/> the wrong answer here.
    /// The record's own normal level is the reference and not an absolute level, so a quietly
    /// mastered record is tested in the same way as a loud one.</para>
    ///
    /// <para>The level must hold, not only occur: one loud reading is a hit, and two in a row mean
    /// the drums have started. This separates the crash that opens a record from the groove that
    /// follows it.</para>
    /// </summary>
    public double DrumsStartSeconds
    {
        get
        {
            var bass = BassCurve;
            if (bass.Count < 4)
                return 0;

            // The result is cached per curve, because this is read on the audio thread (through the
            // phrase arithmetic that a skip plan walks), and sorting a copy of about seven hundred
            // points on every block costs too much for a 50 ms budget. The answer depends only on the
            // curve, so the curve's identity is the whole key: a record cloned with a new reading has
            // a new list and misses the cache, and a clone that kept its curves keeps its answer.
            var known = _drumsStart;
            if (known is not null && ReferenceEquals(known.Curve, bass))
                return known.Seconds;

            var sorted = bass.ToArray();
            Array.Sort(sorted);
            double body = sorted[sorted.Length / 2];

            double at = 0;
            if (body > 0)
            {
                double floor = body * DrumsShareOfBody;
                for (int i = 0; i + 1 < bass.Count; i++)
                {
                    if (bass[i] < floor || bass[i + 1] < floor)
                        continue;
                    at = i * EnergyProfile.StepSeconds;
                    break;
                }
            }

            _drumsStart = new CurveMemo(bass, at);
            return at;
        }
    }

    /// <summary>One measurement remembered with the curve it was taken from. It is a class, not a
    /// struct, so the write is a single reference and safe to race: a reader on the audio thread sees
    /// the old answer or the new one, never half of either.</summary>
    private sealed record CurveMemo(IReadOnlyList<float> Curve, double Seconds);

    private CurveMemo? _drumsStart;

    /// <summary>
    /// How much of the record's usual bottom end must be present before the drums count as in.
    /// A third is clearly above the rumble under a pad, and clearly below a bare kick, which
    /// carries most of what the record will ever carry down there even before the bassline joins it.
    /// </summary>
    private const double DrumsShareOfBody = 0.35;

    /// <summary>
    /// How many bars before the measured start of the audio to count from, when the bands were never
    /// measured and <see cref="DrumsStartSeconds"/> cannot answer.
    ///
    /// <para>The value is an estimate, not derived from the audio, and is used only on that path.
    /// <see cref="AudioStartSeconds"/> is a level crossing, and a record does not begin at a level: an opening that fades in crosses
    /// an absolute floor well after the music has started.</para>
    /// </summary>
    private const int LeadInBars = 1;

    /// <summary>Where the song's first whole bar begins, in seconds.</summary>
    public double FirstWholeBarSeconds => BarStart(FirstWholeBar) ?? 0;

    /// <summary>
    /// The first four-bar line at or after <paramref name="seconds"/>. This is where a record coming
    /// in starts, so that it arrives in phrase and not only on a bar.
    /// </summary>
    public double PhraseAtOrAfter(double seconds)
    {
        if (BeatTimes.Count == 0 || AlignEvery < 1)
            return seconds;

        int at = 0;
        while (at < BeatTimes.Count && BeatTimes[at] < seconds - 1e-6)
            at++;

        int bar = (int)Math.Ceiling((at - FirstDownbeat) / (double)AlignEvery);
        return BarStart(RoundUpToPhrase(bar)) ?? BarAtOrAfter(seconds);
    }

    /// <summary>The four-bar line at or before <paramref name="seconds"/>.</summary>
    public double PhraseAtOrBefore(double seconds)
    {
        if (BeatTimes.Count == 0 || AlignEvery < 1)
            return seconds;

        int at = BeatTimes.Count - 1;
        while (at > 0 && BeatTimes[at] > seconds + 1e-6)
            at--;

        int bar = (int)Math.Floor((at - FirstDownbeat) / (double)AlignEvery);
        int rounded = RoundUpToPhrase(bar);
        if (rounded > bar)
            rounded -= PhraseBars;

        return BarStart(rounded) ?? BarAtOrBefore(seconds);
    }

    /// <summary>
    /// How long a phrase is, in beats. Thirty-two: eight bars of a record counted in four, which is
    /// what a DJ counts as a phrase and what dance music is written in.
    ///
    /// <para>The unit is beats, not bars, because the length is fixed in beats: a record counted in
    /// three has eight-bar phrases of twenty-four beats, and nobody hears those as phrases. Divide by
    /// <see cref="BeatsPerBar"/> for the bar count.</para>
    ///
    /// <para>This is not the same number as <see cref="PhraseGrid.Bars"/>, and the two are easy to
    /// confuse because both are written 8, 16 or 32. That one is counted in BARS and is the span that
    /// the record was measured to REPEAT over: thirty-two of those is a hundred and twenty-eight
    /// beats, which is four of these. This value is the length a changeover is lined up on, and that
    /// one supplies the phase.</para>
    /// </summary>
    public const int PhraseBeats = 32;

    /// <summary>How many bars a phrase is on this record: <see cref="PhraseBeats"/> of its own beats,
    /// which is eight for any record counted in four.</summary>
    public int BarsPerPhrase => Math.Max(1, PhraseBeats / Math.Max(1, AlignEvery));

    /// <summary>
    /// The start of the phrase at or before <paramref name="seconds"/>: a
    /// <see cref="BarsPerPhrase"/>-bar line, in the phase in which the record was measured to change.
    ///
    /// <para>This differs from <see cref="PhraseAtOrBefore"/> in two ways. The length is a phrase and
    /// not a four, and the phase comes from <see cref="Phrases"/> (where the record was heard to
    /// change) and not from the bar where its drums come in. The anchor for that phase is
    /// <see cref="FirstDownbeat"/>, because <see cref="TrackShape.Phrases"/> counted from there: its
    /// bar zero is the bar that starts at that beat.</para>
    ///
    /// <para>The measured span may be longer than a phrase (sixteen or thirty-two bars), and only its
    /// phase is used, never its length. Lining a changeover up on a thirty-two-bar repeat could move
    /// it back by up to thirty-one bars to reach the previous boundary, which discards most of a
    /// minute of music to move a mix by a few seconds. All its boundaries are phrase boundaries anyway,
    /// because they are a whole number of phrases apart.</para>
    ///
    /// <para>If the record has no measured phrase, this uses the four-bar line, so a caller can ask
    /// for this without first checking that it exists. The result is then the standard alignment,
    /// which is the right answer when nothing better is known.</para>
    /// </summary>
    public double MeasuredPhraseAtOrBefore(double seconds)
    {
        if (!Phrases.IsKnown || BeatTimes.Count == 0 || AlignEvery < 1)
            return PhraseAtOrBefore(seconds);

        int at = BeatTimes.Count - 1;
        while (at > 0 && BeatTimes[at] > seconds + 1e-6)
            at--;

        int bar = (int)Math.Floor((at - FirstDownbeat) / (double)AlignEvery);
        int length = BarsPerPhrase;

        // The remainder is calculated by hand, as in RoundUpToPhrase: the bars before the anchor are a
        // record's lead-in, and C# returns a negative remainder for every one of them.
        int off = ((bar - Phrases.FirstBar) % length + length) % length;
        return BarStart(bar - off) ?? PhraseAtOrBefore(seconds);
    }

    /// <summary>
    /// The start of the first phrase at or after <paramref name="seconds"/>, in the phase in which the
    /// record was measured to change: <see cref="MeasuredPhraseAtOrBefore"/> in the other direction.
    ///
    /// <para>This goes forwards, for a record coming IN. The point where that record may start is a
    /// minimum and not a target: if it is told to arrive at the end of its intro, arriving a moment
    /// BEFORE that point means arriving in the intro, which is what it must not do.</para>
    /// </summary>
    public double MeasuredPhraseAtOrAfter(double seconds)
    {
        if (!Phrases.IsKnown || BeatTimes.Count == 0 || AlignEvery < 1)
            return PhraseAtOrAfter(seconds);

        int at = 0;
        while (at < BeatTimes.Count && BeatTimes[at] < seconds - 1e-6)
            at++;

        int bar = (int)Math.Ceiling((at - FirstDownbeat) / (double)AlignEvery);
        int length = BarsPerPhrase;

        int off = ((bar - Phrases.FirstBar) % length + length) % length;
        return BarStart(off == 0 ? bar : bar + (length - off)) ?? PhraseAtOrAfter(seconds);
    }

    /// <summary>How long one phrase of this record lasts, in seconds. Zero when there is no tempo to
    /// measure it with.</summary>
    public double PhraseSeconds => BarSeconds > 0 ? BarsPerPhrase * BarSeconds : 0;

    /// <summary>The next bar number at or after <paramref name="bar"/> that is a whole number of
    /// phrases from <see cref="FirstWholeBar"/>.</summary>
    private int RoundUpToPhrase(int bar)
    {
        int anchor = FirstWholeBar;

        // The remainder is calculated by hand, because C# returns a negative remainder for the bars
        // before the anchor, and those are the bars in a record's lead-in.
        int off = ((bar - anchor) % PhraseBars + PhraseBars) % PhraseBars;
        return off == 0 ? bar : bar + (PhraseBars - off);
    }

    /// <summary>Where a numbered bar of the grid begins, or null when the grid does not reach it.</summary>
    private double? BarStart(int bar)
    {
        int beat = FirstDownbeat + bar * AlignEvery;
        return beat >= 0 && beat < BeatTimes.Count ? BeatTimes[beat] : null;
    }

    /// <summary>
    /// The beat of this record's grid nearest a point, or null where there is no grid to read.
    ///
    /// <para>The nearest beat is used, not the next one, because this answers how far the needle is
    /// from a beat, and a needle slightly PAST a beat is on it and not most of a beat from the next
    /// one. The search is binary and not a scan: this is read on the audio thread at every block while
    /// two records are playing together (see the phase lock in <see cref="Mixing.Dj"/>).</para>
    /// </summary>
    public double? BeatNearest(double seconds)
    {
        if (BeatTimes.Count == 0)
            return null;

        int lo = 0, hi = BeatTimes.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (BeatTimes[mid] < seconds)
                lo = mid + 1;
            else
                hi = mid;
        }

        double after = BeatTimes[lo];
        double before = lo > 0 ? BeatTimes[lo - 1] : after;
        return seconds - before <= after - seconds ? before : after;
    }

    /// <summary>
    /// The first bar line strictly after <paramref name="seconds"/>, or null when the grid does not
    /// reach that far. This is what a changeover is timed against: the incoming record is held until
    /// the outgoing one arrives here, so the two bar lines land on the same sample.
    /// </summary>
    public double? NextDownbeatAfter(double seconds)
    {
        if (BeatTimes.Count == 0 || BeatsPerBar < 1)
            return null;

        int step = AlignEvery;
        for (int i = FirstDownbeat; i < BeatTimes.Count; i += step)
            if (BeatTimes[i] > seconds + 1e-6)
                return Audible(BeatTimes[i]);

        // Past the last beat in the grid: continue the line at the known tempo and do not give up.
        // The outro of a record often runs past the point where the beat tracker stopped finding
        // onsets.
        double every = BarSeconds / Math.Max(1, BeatsPerBar) * step;
        if (every <= 0)
            return null;

        double last = BeatTimes[^1];
        double lines = Math.Ceiling((seconds - last) / every + 1e-9);
        return Audible(last + Math.Max(1, lines) * every);
    }

    /// <summary>
    /// A bar line, unless it falls after the record has stopped making a sound.
    ///
    /// <para>The grid is a straight line carried to both ends of the file, and after the last note it
    /// keeps naming bar lines in silence, which a changeover would wait for. The next record would
    /// then come in over silence, late by the length of the tail left by the mastering. Where the
    /// edges were never measured this does nothing, so an old row behaves as before.</para>
    /// </summary>
    private double? Audible(double at) =>
        HasEdges && at > AudioEndSeconds + 1e-6 ? null : at;

    /// <summary>
    /// How hard the record is hitting <em>at a given point</em>, 0..1, as opposed to
    /// <see cref="Intensity"/>, which is one number for the whole track.
    ///
    /// <para>A follow-on record should be chosen against this value. A record mixed out of during its
    /// breakdown needs a different next record than the same record mixed out of at its peak, and the
    /// track average cannot tell those two moments apart. The value is averaged over a few seconds so
    /// that one dip in a single bar does not decide the next record.</para>
    /// </summary>
    public double IntensityAt(double seconds)
    {
        if (EnergyCurve.Count == 0)
            return Intensity;

        const double window = 3;
        int half = Math.Max(1, (int)(window / EnergyProfile.StepSeconds / 2));
        int at = (int)Math.Round(seconds / EnergyProfile.StepSeconds);
        int from = Math.Clamp(at - half, 0, EnergyCurve.Count - 1);
        int to = Math.Clamp(at + half, 0, EnergyCurve.Count - 1);

        double sum = 0;
        for (int i = from; i <= to; i++)
            sum += EnergyCurve[i];
        return EnergyProfile.Rescale(sum / (to - from + 1));
    }

    /// <summary>An analysis for a file that could not be analysed: no tempo, no key, play it
    /// whole. The DJ still accepts it, and it gets a plain crossfade.</summary>
    public static TrackAnalysis Unanalyzable(string path, double durationSeconds) => new()
    {
        FilePath = path,
        DurationSeconds = durationSeconds,
        OutroStartSeconds = durationSeconds,
    };
}
