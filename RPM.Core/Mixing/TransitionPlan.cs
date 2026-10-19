namespace RPM.Core.Mixing;

/// <summary>How one record is taken over by the next.</summary>
public enum TransitionStyle
{
    /// <summary>The incoming record is pulled onto the outgoing record's tempo and dropped on a bar
    /// line. The two run together for several bars while the fader and the bass swap over. This is what
    /// a mix normally means.</summary>
    BeatMatched,

    /// <summary>
    /// The phrase blend. It uses the same lock as <see cref="BeatMatched"/>, laid out against the
    /// records' own phrasing rather than against the outro.
    ///
    /// <para>The two differ in almost every way. A beat-matched blend happens where the record is
    /// running out. It starts on a four, runs for <see cref="TransitionPlanner.OutroFadeBars"/> bars,
    /// and cues the new record a fade's length before the end of its intro. So the intro plays under
    /// the old record, and the drop lands as the fade finishes. A phrase blend happens where the music
    /// says so. It starts on one of the outgoing record's measured phrase lines and runs for
    /// <see cref="MixPolicy.BlendPhrases"/> whole phrases. It starts the new record at the first phrase
    /// of its body, so neither its intro nor its outro is mixed over. If the two tempos cannot be
    /// locked exactly, it is refused rather than shortened.</para>
    ///
    /// <para>They also fail differently, which is the practical reason for two names. The beat-matched
    /// blend allows for drift and halves its length until it fits. A phrase blend has no barely audible
    /// ends to hide drift in. So it either holds the pair on one tempo for its whole length, or it
    /// gives way and the ordinary changeover happens instead.</para>
    ///
    /// <para>It is also the gate on the two moves built on it. <see cref="BassSwap"/> and the drop swap
    /// are phrase moves. They happen only where a phrase blend would have happened.</para>
    /// </summary>
    PhraseBlend,

    /// <summary>The two tempos will not meet, or one record has no usable grid. So the records simply
    /// cross over. The crossover is still bar-aligned on whichever side has a grid.</summary>
    Blend,

    /// <summary>A quick change. The tail of the outgoing record plays out under a second of the new
    /// one. It is used for tracks with nothing that could hold a longer mix, such as spoken word, a
    /// hard ending, or a tempo that does not match.</summary>
    Cut,

    /// <summary>
    /// The next key. The record playing is taken to its next bar line. That bar is looped for
    /// <see cref="TransitionPlanner.RollOutBars"/> bars while the fader comes down and a filter takes
    /// the top and the middle away. The new record comes up through the last
    /// <see cref="TransitionPlanner.RollInBars"/> of them. It starts at the bar where its own
    /// instruments come in, and loops that bar until the record it arrived over stops.
    /// </summary>
    RollOut,

    // ── The moves below come from how other rooms mix. See MixTechniques for the source of each one.
    // None of them changes the styles above. A set that never asks for one, and never turns
    // MixPolicy.Techniques on, plays exactly as before.

    /// <summary>
    /// The cut on the one. The record playing is closed and the new one is opened on the same downbeat,
    /// with nothing under it and nothing over it. It is one beat wide, bar-aligned, with no overlap to
    /// speak of.
    ///
    /// <para>This is not <see cref="Cut"/>. Cut is what the app does when there is nothing to time a
    /// changeover to. Slam is timed. It is the hip-hop and open-format move. The point is that the room
    /// hears the change happen, not that it hears the new record arrive.</para>
    /// </summary>
    Slam,

    /// <summary>
    /// The bass swap. This is a long layer. The incoming record arrives with its bottom end cut, and
    /// both records play whole. The bass changes hands across a phrase. Then the old record, now the
    /// thinner of the two, is taken away from above.
    ///
    /// <para>This is the move that progressive house, melodic house and trance are built on. It takes
    /// three phrases (see <see cref="TransitionPlanner.BassSwapPhrases"/>): one to arrive over, one to
    /// hand the bottom end across, and one to leave in. It needs two records steady enough to be held
    /// together for all of it.</para>
    /// </summary>
    BassSwap,

    /// <summary>
    /// The filter fade. The record playing is high-passed out of the mix: first its bottom, then its
    /// body. The new record arrives whole underneath it.
    ///
    /// <para>This is what house and techno do with two busy records that will not layer. Two full
    /// arrangements would fight over the middle. So the old one is thinned until only air is left. The
    /// fader only finishes what the filter started.</para>
    /// </summary>
    FilterFade,

    /// <summary>
    /// The double drop. The two records are lined up so that their drops, where the beat comes back
    /// after a breakdown (see <see cref="Analysis.TrackAnalysis.DropSeconds"/>), land on the same beat.
    /// The record playing is cut across the beat, or the two beats in front of it, so that the drop the
    /// room hears on that line is the new record's.
    ///
    /// <para>This is the signature move of drum and bass. It is the one changeover planned backwards
    /// from the moment it is aimed at. The cue point on the incoming record is whatever puts its drop
    /// on the outgoing record's drop. Everything else follows from that.</para>
    /// </summary>
    DoubleDrop,

    /// <summary>
    /// The echo out. On the bar line, the record playing is thrown into a beat-synced echo and its
    /// fader is shut. The tail rings on alone for a bar. The new record lands dry on the next bar.
    ///
    /// <para>This is the "echo, fade, return" of afro house, and the way out of a record with no outro
    /// in hip-hop. The echo is taken after the fader. See <see cref="Audio.EchoTail"/>. That is what
    /// lets the record leave while its sound stays in the room.</para>
    /// </summary>
    EchoOut,

    /// <summary>
    /// The spinback. The phrase is played to its end. Then it is ripped backwards, slowing as it goes,
    /// and the new record is slammed in.
    ///
    /// <para>This is the rewind, from jungle and garage, and from every sound system that had an MC. It
    /// is a gesture, not a mix, and it is rationed like one. See
    /// <see cref="MixTechniques.TheatricalChance"/>.</para>
    /// </summary>
    Spinback,
}

/// <summary>
/// The instruction the DJ writes before touching anything. It says which record comes next, where to
/// drop the needle on it, how far to pull its tempo, when to start the fade, and how long to take over
/// it. The booth then carries it out.
/// </summary>
public sealed record TransitionPlan
{
    public required MixTrack Incoming { get; init; }

    public TransitionStyle Style { get; init; }

    /// <summary>Position on the OUTGOING record, in its own seconds, where the fade starts.</summary>
    public double StartAtSeconds { get; init; }

    /// <summary>Where to cue the incoming record, in its own seconds, without any stretching.</summary>
    public double CueInSeconds { get; init; }

    /// <summary>The speed the incoming deck is pulled to. 1 means play it as recorded.</summary>
    public double TempoRatio { get; init; } = 1;

    /// <summary>
    /// The speed the OUTGOING deck is pulled to for the changeover. The two records meet in the middle,
    /// instead of one being dragged all the way onto the other. Bending both by half as much is what a
    /// DJ does with two pitch faders. It keeps each record closer to its own tempo. A pull is heard as
    /// a record sounding fast well before it is heard as a mix that is out of time.
    /// </summary>
    public double OutgoingTempoRatio { get; init; } = 1;

    /// <summary>How long the two records overlap, in real (heard) seconds.</summary>
    public double FadeSeconds { get; init; }

    /// <summary>
    /// How long the incoming record takes to come back up to its own tempo once the changeover is over,
    /// in real seconds. It is zero when the record was not pulled at all.
    ///
    /// <para>A record is bent onto the outgoing record's tempo so that the two grids agree while both
    /// are playing. Once the old record has gone, there is nothing left to agree with. Leaving the new
    /// record bent would play all of it at the wrong speed. It stays bent until then. Releasing it
    /// while the old record is still audible would let the grids drift apart during the one stretch
    /// that beat-matching exists for.</para>
    /// </summary>
    public double ReleaseSeconds { get; init; }

    /// <summary>
    /// How far apart the two tempos are after they have been pulled as far as allowed, as a fraction.
    /// It is zero when they meet exactly.
    ///
    /// <para>The pull on each record is capped (<see cref="MixPolicy.MaxTempoPull"/>), because a record
    /// bent too far is audibly being interfered with. Two records further apart than twice that cap
    /// cannot be brought together at all. The changeover then runs with the two grids drifting apart at
    /// this rate for as long as both are playing.</para>
    ///
    /// <para>This is not a fault, and it is not rare. A beat-match between records eleven percent apart
    /// is this case. The value is here so the lab can show it. On screen, a changeover that will hold
    /// and one that will slide look exactly alike.</para>
    /// </summary>
    public double TempoMismatch { get; init; }

    /// <summary>
    /// The worst distance between the two grids anywhere inside the changeover, in the seconds the room
    /// hears. The length of the changeover is chosen to keep this number down.
    ///
    /// <para>This is a different reading from <see cref="TempoMismatch"/>, which is about the tempos.
    /// Two records can meet on one tempo exactly and still drift apart in the middle. A real grid
    /// bends, and one constant pull cannot follow it. The changeover is shortened until this value is
    /// inside <see cref="MixPolicy.MaxDriftSeconds"/>. If even one bar cannot do that, the pair is
    /// blended rather than beat-matched.</para>
    /// </summary>
    public double GridDriftSeconds { get; init; }

    /// <summary>
    /// How the pull moves through the changeover, when one pull will not hold the pair. See
    /// <see cref="TempoStep"/>. The list is empty for an ordinary changeover. There,
    /// <see cref="TempoRatio"/> is the whole story, and the deck is simply left at it.
    ///
    /// <para>When the list is not empty, its first piece is <see cref="TempoRatio"/>. So a reader that
    /// only knows about one pull still gets the one the record comes in on.</para>
    /// </summary>
    public IReadOnlyList<TempoStep> TempoSteps { get; init; } = [];

    /// <summary>The tempo the mix runs at through the transition, for the record. It is counted at the
    /// outgoing record's own octave, so it is always <c>outgoing.Bpm × OutgoingTempoRatio</c>. That is
    /// not necessarily the octave the bars below were measured in.</summary>
    public double MixBpm { get; init; }

    /// <summary>
    /// How long one bar of the mix lasts. That is four beats of what the room is counting. On a record
    /// whose tempo came back an octave low, that is not four beats of <see cref="MixBpm"/>. See
    /// <see cref="Analysis.TempoEstimator.DanceLevel"/>.
    /// </summary>
    public double BarSeconds { get; init; }

    /// <summary>How many bars the changeover lasts. It is <see cref="TransitionPlanner.OutroFadeBars"/>
    /// for a mix, fewer when the record ran out of room or the grids would have drifted too far to hold
    /// the longer one. It is <see cref="TransitionPlanner.RollOutBars"/> for the next key's
    /// loop.</summary>
    public int FadeBars { get; init; }

    // ── the next key only ──

    /// <summary>
    /// How long one bar of the record coming IN lasts, at the speed it will be played at. This is the
    /// same as <see cref="BarSeconds"/> when the two records were brought onto one tempo, which is the
    /// point of doing so. When they could not be, it is the record's own bar.
    /// </summary>
    public double RollInBarSeconds { get; init; }

    /// <summary>
    /// How many bars of the roll-out the incoming record rides up over. These are counted in
    /// <see cref="RollInBarSeconds"/>, which means <em>its own</em> bars, not the mix's.
    ///
    /// <para>The two counts are the same on most records. On one record they are an octave apart, and
    /// that is why this count matters here. It is the count the incoming deck loops. However many of
    /// its own bars that works out to, the loop is sized to last the same
    /// <see cref="TransitionPlanner.RollInBars"/> bars of the mix. That duration is what the drift
    /// between the two grids was budgeted against.</para>
    ///
    /// <para>Zero when the two records could not be brought onto one tempo. Overlapping them would then
    /// mean two grids drifting apart in front of the room. The loop is left to finish on its own, and
    /// the new record follows it. This is honest about there being no match to hear.</para>
    /// </summary>
    public int OverlapBars { get; init; }

    // ── how it was arrived at ──

    /// <summary>
    /// What the planner decided on the way here, in the order it decided it.
    ///
    /// <para>The numbers above say what is going to happen. This says why. When a changeover sounds
    /// wrong, the question is which decision was the wrong one. Examples are the octave, the bar phase,
    /// the clamp that could not reach the tempo, and the halving that took sixteen bars down to four.
    /// Each of these leaves the same trace in the plan: a number that is smaller than it should be,
    /// with nothing that says what made it smaller.</para>
    ///
    /// <para>It is written on every plan, not behind a switch. It is a few short strings every few
    /// minutes, against a changeover that costs two decoders and a thousand blocks of arithmetic. A
    /// diagnostic that only appears when someone remembers to turn it on is never there for the
    /// changeover that went wrong.</para>
    /// </summary>
    public IReadOnlyList<string> Trace { get; init; } = [];

    /// <summary>What a person asked for that the planner would not have chosen, and what came of it.
    /// Null for a plan nobody touched, which is nearly all of them.</summary>
    public MixOverrides? Overrides { get; init; }

    /// <summary>
    /// The volume arc for this changeover. It is the one someone drew, or the one the technique asks
    /// for. See <see cref="TransitionStyle.BassSwap"/> and <see cref="TransitionStyle.DoubleDrop"/>,
    /// whose whole character is a level curve. Null means the equal-power crossfade that the app has
    /// always made. See <see cref="FadeArc"/>.
    ///
    /// <para>Only a crossfade reads it. <see cref="MixTechniques.FollowsAnArc"/> says which changeovers
    /// those are. A roll-out's levels are its own choreography of loop, filter and rise. An echo out's
    /// levels are a channel being shut on a bar line. A rewind's levels are a record at full until a
    /// hand stops it. None of the three is a curve to be drawn over.</para>
    /// </summary>
    public FadeArc? Arc { get; init; }
}
