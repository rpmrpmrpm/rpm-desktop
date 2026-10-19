namespace RPM.Core.Mixing;

/// <summary>
/// The numbers the planner starts from, gathered where a person can reach them.
///
/// <para>Each of these is a constant with a reason, and that reason gives the default. But a default is
/// a starting point, not a fixed rule. A good match may be worth pulling a record harder than ten
/// percent. A long blend may be worth thirty-two bars. The way to find out is to try it on a real pair
/// and listen. The lab gives an edited policy to the planner through <see cref="MixOverrides.Policy"/>.
/// The planner still clamps and halves as always, but it uses these numbers rather than the built-in
/// ones. The policy in force is written to the journal with the plan. So a knob that is often turned
/// shows up as a fact about the defaults.</para>
///
/// <para>The defaults give the planner's normal behaviour.</para>
/// </summary>
public sealed record MixPolicy
{
    /// <summary>The policy every set runs on until someone turns a knob.</summary>
    public static MixPolicy Default { get; } = new();

    /// <summary>How far a record may be pulled off its own tempo, as a ratio. See
    /// <see cref="TransitionPlanner.MaxTempoPull"/> for why ten percent is the default.</summary>
    public double MaxTempoPull { get; init; } = TransitionPlanner.MaxTempoPull;

    /// <summary>How many bars the changeover runs for an outro. See
    /// <see cref="TransitionPlanner.OutroFadeBars"/>.</summary>
    public int OutroFadeBars { get; init; } = TransitionPlanner.OutroFadeBars;

    /// <summary>How far the two grids may drift apart during a changeover, in seconds, before the
    /// planner stops calling it a beat-match.</summary>
    public double MaxDriftSeconds { get; init; } = TransitionPlanner.MaxDriftSeconds;

    /// <summary>How many bars the next key rolls the record playing out over.</summary>
    public int RollOutBars { get; init; } = TransitionPlanner.RollOutBars;

    /// <summary>How many bars of the mix the incoming record takes to come up to its own tempo, once it
    /// has the room to itself.</summary>
    public int ReleaseBars { get; init; } = TransitionPlanner.ReleaseBars;

    /// <summary>How often a skip's changeover is built on noise rather than on the records
    /// alone.</summary>
    public double NoiseChance { get; init; } = TransitionPlanner.NoiseChance;

    /// <summary>Past this length, a record is an extended one and is not played to its outro.</summary>
    public double ExtendedRecordSeconds { get; init; } = TransitionPlanner.ExtendedRecordSeconds;

    /// <summary>Where an extended record hands over instead.</summary>
    public double ExtendedMixOutSeconds { get; init; } = TransitionPlanner.ExtendedMixOutSeconds;

    /// <summary>
    /// Whether the planner chooses a technique for each pair of records, rather than making the same
    /// changeover every time. See <see cref="MixTechniques.Best"/>.
    ///
    /// <para>Off is not a hedge. The app today makes one sixteen-bar beat-matched blend at the outro,
    /// and one roll-out on the next key. That is a set with a sound, not a set with a gap in it. Each
    /// of the six moves in the catalogue is a convention from one kind of room. Applied to the wrong
    /// pair of records, each one is worse than the blend. When this is on, the planner reads the pair:
    /// two drops it can land together, two busy records at one tempo, or two tempos that will not meet
    /// at all. It then picks a technique, and the trace says which one and why.</para>
    ///
    /// <para>The next key is not covered by this. A press means leave the record, and what that does is
    /// roll out. A technique there is asked for by hand or not at all.</para>
    /// </summary>
    public bool Techniques { get; init; }

    /// <summary>
    /// Whether a beat-matched changeover is lined up on the record's measured phrase, not on the fours.
    /// See <see cref="Analysis.TrackShape.Phrases"/>.
    ///
    /// <para>This is a different question from <see cref="Techniques"/>. Techniques chooses WHICH
    /// changeover to make. This setting changes where the beat-matched blend lands and how long it
    /// runs. The two are independent, and either one can be on without the other.</para>
    ///
    /// <para>When on, the mix-out goes to the start of a phrase (thirty-two beats, in the phase the
    /// record was measured to change on), not to the nearest four. The blend runs
    /// <see cref="BlendPhrases"/> of them. A record with no measured phrase is planned exactly as
    /// before. There is nothing to line up on, and a guessed phrase is worse than a four that is
    /// known.</para>
    ///
    /// <para>This applies only to the beat-matched blend. A cut has nothing to align. Each of the six
    /// added moves has a length that is part of the move itself. A two-bar echo out stretched to two
    /// phrases is not an echo out.</para>
    /// </summary>
    public bool PhraseBlend { get; init; }

    /// <summary>
    /// Whether the phrase layout may also make the two changeovers it makes possible: the drop swap,
    /// where both records have a drop that can land on one line, and the bass swap, where both are
    /// heavy where they meet.
    ///
    /// <para>On by default, because those two are the core of the phrase mode. A phrase line is what
    /// makes either of them plannable. A mode that finds two drops it could land together, and then
    /// blends over them anyway, has read the record and ignored it.</para>
    ///
    /// <para>Off is for a mode that wants the alignment and nothing else: the ordinary changeover, laid
    /// out on the phrase, with the same fade or cut when the pair cannot be matched. See the
    /// workbench's Auto X. Which changeover to make and where to put it are separate questions. This
    /// setting answers the second one on its own.</para>
    ///
    /// <para>It is read only where <see cref="PhraseBlend"/> puts the plan on a phrase. A style asked
    /// for by name is not changed by it. A person who picks the bass swap gets the bass swap.</para>
    /// </summary>
    public bool PhraseMoves { get; init; } = true;

    /// <summary>
    /// How many phrases a phrase blend runs for. The default is two: sixteen bars of a record counted
    /// in fours. That gives one phrase for the two records to arrive at together, and one phrase for
    /// the old record to leave over.
    ///
    /// <para>A phrase is <see cref="Analysis.TrackAnalysis.PhraseBeats"/>, thirty-two beats. It is not
    /// <see cref="Analysis.PhraseGrid.Bars"/>, which counts bars and is the span the record repeats
    /// over. Two of those on a thirty-two bar record would be sixty-four bars.</para>
    ///
    /// <para>It is read only when <see cref="PhraseBlend"/> is on. A length asked for by hand overrides
    /// it.</para>
    /// </summary>
    public int BlendPhrases { get; init; } = 2;

    /// <summary>True when every knob still has its default value. Every set starts in this state, and
    /// it is the state that needs no log line.</summary>
    public bool IsDefault => Equals(Default);

    /// <summary>Only the knobs that were changed, for a journal line and for the screen.</summary>
    public override string ToString()
    {
        if (IsDefault)
            return "default";

        var parts = new List<string>(4);
        if (Math.Abs(MaxTempoPull - Default.MaxTempoPull) > 1e-9) parts.Add($"pull {MaxTempoPull:P0}");
        if (OutroFadeBars != Default.OutroFadeBars) parts.Add($"fade {OutroFadeBars} bars");
        if (Math.Abs(MaxDriftSeconds - Default.MaxDriftSeconds) > 1e-9) parts.Add($"drift {MaxDriftSeconds * 1000:F0} ms");
        if (RollOutBars != Default.RollOutBars) parts.Add($"roll {RollOutBars} bars");
        if (ReleaseBars != Default.ReleaseBars) parts.Add($"release {ReleaseBars} bars");
        if (Math.Abs(NoiseChance - Default.NoiseChance) > 1e-9) parts.Add($"noise {NoiseChance:P0}");
        if (Math.Abs(ExtendedRecordSeconds - Default.ExtendedRecordSeconds) > 1e-9)
            parts.Add($"extended past {ExtendedRecordSeconds:F0}s");
        if (Math.Abs(ExtendedMixOutSeconds - Default.ExtendedMixOutSeconds) > 1e-9)
            parts.Add($"extended out at {ExtendedMixOutSeconds:F0}s");
        if (Techniques != Default.Techniques) parts.Add("techniques on");
        if (PhraseBlend != Default.PhraseBlend) parts.Add("on the phrase");
        if (PhraseMoves != Default.PhraseMoves) parts.Add("blend only");
        if (BlendPhrases != Default.BlendPhrases) parts.Add($"blend {BlendPhrases} phrases");
        return string.Join(", ", parts);
    }
}
