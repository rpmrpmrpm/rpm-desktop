using RPM.Core.Analysis;

namespace RPM.Core.Mixing;

/// <summary>
/// Works out how to get from the record that is playing to the one chosen to follow it. It decides how
/// far the new record's tempo can be pulled, where to drop the needle, and how long the changeover
/// takes. This class only makes decisions. It does not touch any audio.
/// </summary>
public static class TransitionPlanner
{
    /// <summary>
    /// How far a record may be pulled off its own tempo before it stops sounding like itself. This is
    /// ten percent. It is the usual range of a deck's pitch fader, and it is what a hand-made mix of
    /// two of these records measures at. For example, 137 dragged onto 123 is a pull of 10.2%.
    ///
    /// <para>Pitch is kept through the stretch (see <see cref="Audio.FfmpegPcmSource"/>). So a pulled
    /// record does not go out of key, however far it is bent. The limit is about how wrong a record
    /// sounds when it is played fast, not about tuning. With both decks bent to meet in the middle, one
    /// pulled up and the other down, this reaches any pair within a factor of 1.1 or 0.9. For example,
    /// a record at 128 BPM meets anything from 104 to 158. That covers most of what a set puts side by
    /// side.</para>
    /// </summary>
    public const double MaxTempoPull = 0.10;

    /// <summary>
    /// A changeover that the record arrives at on its own runs this many bars: eight out of the old
    /// record and eight into the new one.
    ///
    /// <para>Two phrases. This is long enough to be a mix rather than a change. It is short enough that
    /// what the listener hears is the mix. At sixteen bars the crossing alone ran for twelve bars, and
    /// its ends were inaudible. The incoming record is under -13 dB for the first tenth of the fade,
    /// and the outgoing record is under -20 dB for the last quarter. So the outer bars of a long
    /// changeover hold two records that are not both audible. See
    /// <see cref="Crossfader.OutBy"/>.</para>
    ///
    /// <para>It is halved rather than trimmed, because everything that depends on this number is halved
    /// too. The drift budget and the room left on the record each cut the count in two at a time. So an
    /// eight that will not fit becomes four, then two. It never becomes 11.3. See
    /// <see cref="Plan"/>.</para>
    ///
    /// <para>This is the only mixed changeover a set makes. The next key does not make one. It is the
    /// next key, and what it does is play the next record. See <see cref="SkipTo"/>.</para>
    /// </summary>
    public const int OutroFadeBars = 8;


    /// <summary>
    /// How many bars the next key spends bringing the record playing onto the tempo the changeover will
    /// run at, before any of the changeover happens.
    ///
    /// <para>A record pulled several percent inside one block is plainly audible as a record being
    /// interfered with. Matching two records only works if nobody hears it being done. Four bars is
    /// slow enough that a full ten percent comes off at well under a beat per minute per second. It is
    /// short enough that the next key still answers within a phrase.</para>
    /// </summary>
    public const int MatchInBars = 4;

    /// <summary>
    /// How many bars the next key rolls the record playing out over. Eight is a phrase, which is how
    /// long a room can be kept waiting for something it can hear coming.
    /// </summary>
    public const int RollOutBars = 8;

    /// <summary>
    /// How many bars the noise takes to come down after a skip has landed. This is four bars, which is
    /// half a phrase. That is long enough for the filter to be heard taking the middle and the top out,
    /// rather than the sound simply stopping. The noise is finished well before the new record has said
    /// anything.
    /// </summary>
    public const int DropBars = 4;

    /// <summary>
    /// How often a changeover is built on noise rather than on the records alone. The value is one
    /// quarter.
    ///
    /// <para>A trick that happens every single time is not a trick. It becomes the sound of the app.
    /// The fourth one in a row teaches the listener what the machine does, instead of letting them hear
    /// a DJ do something. Kept rare, it is a moment. Kept constant, it is a habit. The roll-out itself
    /// happens every time, because that is how the records are handed over. Only the noise over the top
    /// of it is occasional.</para>
    /// </summary>
    public const double NoiseChance = 0.25;

    /// <summary>
    /// How long the record playing takes to get out of the way when the next key is pressed and there
    /// is no bar to roll it out on. This is a quarter of a second. That is long enough for the old
    /// record to leave rather than stop, because a waveform cut off mid-cycle is a click. It is short
    /// enough that the press and the new record happen at the same moment.
    /// </summary>
    public const double SkipCutSeconds = 0.25;

    /// <summary>How long the incoming record takes to come back up to its own tempo once it has landed,
    /// in bars of the mix. This is eight, the same phrase again. That is slow enough that a ten percent
    /// pull comes off at about a beat per minute per second, which is under what anyone hears as a
    /// record speeding up.</summary>
    public const int ReleaseBars = 8;

    /// <summary>Fallback lengths, in seconds, when there is no grid to count bars on.</summary>
    private const double BlendSeconds = 8, CutSeconds = 1.5;

    // ── The added moves ───────────────────────────────────────────────────────
    //
    // Numbers for the six changeovers taken from how other rooms mix. See MixTechniques for where each
    // one was read. None of them can be reached unless someone asks for it or turns on
    // MixPolicy.Techniques. So none of them changes a set that does neither.

    /// <summary>
    /// How many bars an echo out runs: two. The first is the tail on its own, after the fader has been
    /// shut. The record coming in lands dry on the second. Any longer, and the room listens to an
    /// effect rather than to a record arriving.
    /// </summary>
    public const int EchoBars = 2;

    /// <summary>
    /// How often a changeover that would otherwise be a cut is an echo out instead. The value is one in
    /// four.
    ///
    /// <para>The cut is the one move in the app that nobody chooses. It is what happens when the record
    /// coming in cannot be read at all: no tempo, too few beats, or confidence too low. So there is
    /// nothing to hold an overlap together, and the old record simply stops. On a library that has not
    /// been analysed, that is many changeovers. Every one of them sounds like the app giving up.
    /// </para>
    ///
    /// <para>The echo out needs nothing from the record coming in. It is thrown on the OUTGOING
    /// record's bar line (see <see cref="MixNeeds.OutgoingGrid"/>). That grid is still there in this
    /// case, because the record playing is the one the set has been reading all along. So it costs
    /// nothing to reach. The guides recommend it for exactly this case: a big tempo change, with
    /// nothing to fade over.</para>
    ///
    /// <para>It is one in four rather than every time, because a gesture that happens on every unread
    /// record stops being a gesture. Three in four of those changeovers stay cuts.</para>
    /// </summary>
    public const double EchoOutChance = 0.25;

    /// <summary>How long the echo is, in beats of the mix. This is one beat, a quarter note. It is the
    /// plainest beat-synced delay there is, and it cannot be heard as a rhythm of its own.</summary>
    public const double EchoBeats = 1;

    /// <summary>
    /// How much of the echo comes back round each time. The value is 0.6. That gives about four audible
    /// repeats. Each repeat is 4.4 dB down, so a tail thrown on the bar line is 18 dB down by the next
    /// one, and gone under the record that lands there. It is enough to fill a bar, and not enough to
    /// still ring over the new record's first phrase.
    /// </summary>
    public const double EchoFeedback = 0.6;

    /// <summary>
    /// How many bars a spinback plays before it rips, and how many it rips over. Both are one bar. The
    /// phrase is heard out to its end, and that bar is the part that gets pulled back. The rewind takes
    /// the same bar to travel back through it. That is about as long as a hand takes on a platter.
    /// </summary>
    public const int SpinBars = 1;

    /// <summary>
    /// How wide the cut on the one is: thirty milliseconds. This is far too short to hear as a fade. It
    /// is far more than enough to stop two waveforms that meet at any value from making a click. It is
    /// what a hand does to a channel fader, measured.
    /// </summary>
    public const double SlamSeconds = 0.03;

    /// <summary>
    /// How far the two records may drift apart over a changeover before it stops being beat-matched.
    /// The tempo pull is a fixed ratio applied when the deck opens. So any tempo it could not reach
    /// exactly makes the grids drift apart at a steady rate. For example, a 1% mismatch is 75 ms over
    /// four bars. That is a quarter of a beat, and it is plainly audible. 15 ms is under the point
    /// where two kicks stop sounding like one.
    /// </summary>
    /// <remarks>Public because it has to be read together with
    /// <see cref="Audio.FfmpegPcmSource.UnstretchedRatio"/>. A pull that the decoder declines to apply
    /// is drift that this budget was never told about. The two numbers are only correct
    /// together.</remarks>
    public const double MaxDriftSeconds = 0.015;

    /// <summary>
    /// How far apart two tempos may be and still count as one. The value is one billionth. This is not
    /// a real tolerance. It is a guard against floating point error.
    ///
    /// <para>Two records inside the pull limits meet exactly. Both ratios come out unclamped, both
    /// decks land on the same target, and the difference is zero, apart from the last bit or two. Two
    /// records outside the limits do not meet at all. The gap is then a percentage, not a fraction of
    /// one. Nothing lies between these cases to argue about. So the number only has to be small enough
    /// to catch arithmetic error, and large enough not to be fooled by it.</para>
    ///
    /// <para>This is a different question from <see cref="MaxDriftSeconds"/>. That one asks how long
    /// two tempos that did NOT meet can be held together before anyone hears it. This one asks whether
    /// they met. The changeovers where both records are audible throughout, the phrase blend and the
    /// bass swap, need the second answer. They cannot use the first.</para>
    /// </summary>
    public const double TempoLockTolerance = 1e-9;

    /// <summary>The last stretch of a record is reserved. Even when no outro is found, the mix starts
    /// before the very end, so a track never simply stops.</summary>
    private const double MinTailSeconds = 12;

    /// <summary>
    /// Past this length, a record is an extended one and is not played out.
    ///
    /// <para>Six minutes. A record written to be listened to says what it has to say in about four
    /// minutes. Past six minutes it is a twelve-inch, a club edit or an album version. The extra time
    /// is a long intro and a long outro, written so that another record can be brought in over them.
    /// Playing all of it in a set does not respect the record. It means the listener hears two other
    /// records instead.</para>
    /// </summary>
    public const double ExtendedRecordSeconds = 360;

    /// <summary>
    /// Where an extended record hands over instead: about three minutes.
    ///
    /// <para>This is where a record that was not extended would have handed over anyway. That is the
    /// point of the number, and it is not a coincidence. It is a ceiling, not a target. A way out that
    /// the record offers earlier is still preferred. Examples are a breakdown, or the drums carrying on
    /// with the singing stopped (see <see cref="MixOutFinder"/>). The changeover still lands on a bar
    /// line once <see cref="Plan"/> has snapped it.</para>
    /// </summary>
    public const double ExtendedMixOutSeconds = 180;

    /// <summary>
    /// The longest record a set will mix at all: twenty minutes.
    ///
    /// <para>A record longer than this is not a track. It is a DJ set someone else recorded, one side
    /// of a live album, or a full mix. These have no outro to mix out of and no intro to mix in on.
    /// Their tempo reading is an average of an hour of different records. A changeover planned against
    /// that is planned against nothing. They are left out of the crate (see <see cref="DjCrate"/>). If
    /// one is playing anyway, the set moves on by a plain skip (see <see cref="SkipTo"/>).</para>
    /// </summary>
    public const double MaxMixableSeconds = 1200;

    /// <summary>
    /// Plans the changeover from <paramref name="outgoing"/>, playing at <paramref name="outgoingBpm"/>
    /// (its own tempo times whatever it was pulled to), into <paramref name="incoming"/>.
    /// </summary>
    /// <param name="overrides">What a person asked for, instead of what the planner would have chosen.
    /// These are fed into the arithmetic as inputs, not written over its output. So a request that the
    /// record has no room for is cut down by the same clamps as an ordinary plan. The cut is visible in
    /// <see cref="TransitionPlan.Trace"/>.</param>
    /// <param name="carried">The reasoning so far, when this plan was reached by way of another plan.
    /// See the hand-off between this and <see cref="SkipTo"/>. A changeover that changed its mind
    /// halfway has to say both halves out loud. Otherwise the trace reads as if the other option was
    /// never considered.</param>
    /// <param name="luck">A number from 0 to 1, asked once per changeover. It is read only when the
    /// policy says to choose a technique (see <see cref="MixPolicy.Techniques"/>). The default is above
    /// <see cref="MixTechniques.TheatricalChance"/> on purpose. A caller that has no randomness to pass
    /// gets the moves that are mixes, and never the moves that are gestures.</param>
    public static TransitionPlan Plan(
        MixTrack outgoing, double outgoingBpm, MixTrack incoming, MixOverrides? overrides = null,
        List<string>? carried = null, double luck = 0.5)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);

        var from = outgoing.Analysis;
        var to = incoming.Analysis;
        var trace = carried ?? new List<string>(12);

        // The numbers this plan starts from. Nearly always the defaults. A hand on a knob is traced
        // like any other hand, because a knob that keeps getting turned is a default that is wrong.
        var policy = overrides?.Policy ?? MixPolicy.Default;
        if (!policy.IsDefault)
            trace.Add($"policy: {policy}");

        int wantBars = policy.OutroFadeBars;
        double startAt = MixOutPoint(outgoing, policy);
        trace.Add($"mix-out at {MixInputs.Sec(startAt)} of {MixInputs.Sec(outgoing.DurationSeconds)}");

        bool canMatch =
            from is { CanBeatMatch: true } &&
            to is { CanBeatMatch: true } &&
            outgoingBpm > 0;

        if (!canMatch)
            trace.Add(
                $"cannot beat-match: outgoing {Match(from)}, incoming {Match(to)}, " +
                $"playing at {outgoingBpm:F1}");

        // Everything below measures the record by where it stops making a sound, rather than by the
        // length of its file (see MixOutPoint). A fade that runs into the silent tail of a padded file
        // is a fade nobody hears the end of.
        double playsTo = outgoing.AudibleEndSeconds;
        if (playsTo < outgoing.DurationSeconds - 1)
            trace.Add(
                $"record stops at {MixInputs.Sec(playsTo)} of a {MixInputs.Sec(outgoing.DurationSeconds)} " +
                $"file: the rest is silence with a grid over it");

        if (overrides is { StartAtSeconds: { } askedStart })
        {
            trace.Add($"asked to start at {MixInputs.Sec(askedStart)} instead of {MixInputs.Sec(startAt)}");
            startAt = Math.Clamp(askedStart, 0, Math.Max(0, playsTo));
        }

        // Whether this changeover is lined up on the record's own repeat rather than on the fours. It
        // needs a measured phrase to line up on, and a beat-match to be worth lining up. A cut and a
        // blend are over inside a bar. For them, the start line is the only alignment they have, and
        // the four is the safer choice. Being wrong about a four costs three bars. Being wrong about a
        // phrase costs thirty-one. A style asked for by name settles it, either way. The two blends are
        // separate changeovers with separate names (see TransitionStyle.PhraseBlend). So naming one is
        // an instruction, not a hint. Asking for a phrase blend turns the mode on for this changeover,
        // whatever the policy says. Asking for the beat-matched blend turns it off. That is the only
        // way a hand can get the ordinary changeover while a phrase policy is running. The bass swap
        // and the double drop are built out of a phrase blend. They use three phrases, and a drop on a
        // phrase line. So naming either of those also asks for the phrase layout.
        bool wantsPhrase = overrides?.Style switch
        {
            TransitionStyle.PhraseBlend or TransitionStyle.BassSwap or TransitionStyle.DoubleDrop => true,
            TransitionStyle.BeatMatched => false,
            _ => policy.PhraseBlend,
        };

        bool onPhrase = wantsPhrase && canMatch && from is { Phrases.IsKnown: true };
        if (wantsPhrase && !onPhrase)
            trace.Add(
                from is { Phrases.IsKnown: true }
                    ? "phrase blend asked for, and these two cannot be beat-matched: on the fours"
                    : "phrase blend asked for, and this record has no measured phrase: on the fours");

        // On one of the record's fours. This is settled before the style is. A blend and a cut are held
        // to it just as hard as a beat-match. The outro reading gives a start point, and the grid puts
        // a bar line under it. The record is written in fours. So a changeover that starts two bars
        // into a phrase is one the room hears slip, whatever is faded over what.
        double named = startAt;
        startAt = Align(from, startAt, onPhrase);
        if (Math.Abs(startAt - named) > 1e-6)
            trace.Add(
                $"back to the {(onPhrase ? "phrase" : "four")} at {MixInputs.Sec(startAt)}, " +
                $"from {MixInputs.Sec(named)}");

        if (overrides is { FadeBars: > 0 and var askedBars })
        {
            trace.Add($"asked for {askedBars} bars instead of {wantBars}");
            wantBars = askedBars;
        }

        if (overrides is { Arc: not null })
            trace.Add("volume arc drawn by hand");

        // ── The room's own move ──
        //
        // Nobody asked for a style, and the policy says to play the room rather than to make the same
        // sixteen-bar blend every time. The result is a judgement about these two records (see
        // MixTechniques.Best). It is traced as a judgement, not as a hand. So a changeover the app
        // chose cannot be mistaken for one a DJ asked for.
        //
        // Off by default. A set where nobody has turned this on never reaches this block.
        if (overrides?.Style is null && policy.Techniques)
        {
            var choice = MixTechniques.Best(outgoing, incoming, outgoingBpm, canMatch, luck);
            trace.Add($"technique: {choice}");

            if (IsAddedMove(choice.Technique.Style))
                return Shaped(choice.Technique.Style, outgoing, outgoingBpm, incoming, startAt, wantBars, canMatch, policy, overrides, trace);

            if (choice.Technique.Style == TransitionStyle.Blend)
                return Blend(incoming, startAt, outgoingBpm, trace, overrides);

            // Beat-matched, which is where most changeovers land and which is the path below.
        }

        // The phrase mode's third changeover. It is tried first, because its conditions are the
        // narrowest. The record playing has a drop on a phrase line within reach. The record coming in
        // has one to put there. And the new record is the harder of the two.
        //
        // It swaps one drop for the other. The changeover ends ON the outgoing record's drop. What the
        // room hears at that moment is the incoming record's drop, because the old record is taken off
        // across the beat or two in front of it. See DropSwap. PhraseMoves is what a mode turns off
        // when it wants the alignment and nothing else. The two branches below are the only places
        // where the phrase layout makes a changeover the ordinary planner would not have made. A mode
        // called "the usual, on the phrase" has to mean that.
        if (onPhrase && policy.PhraseMoves && overrides is not { Style: not null } && overrides is not { FadeBars: > 0 } &&
            DropSwap(outgoing, outgoingBpm, incoming, policy, startAt, trace) is { } swapAt)
        {
            return Shaped(
                TransitionStyle.DoubleDrop, outgoing, outgoingBpm, incoming, swapAt, wantBars,
                canMatch, policy, overrides, trace);
        }

        // The phrase mode's second changeover. Two records that both carry a lot of bass have one
        // bassline too many for a blend. Laid over each other whole, the two kicks and the two
        // basslines share the bottom of the mix for a phrase, and the room hears mud. The bass swap is
        // the answer to exactly that, and it is why the move exists. So where both records are heavy at
        // the point where the mix meets them, the mode makes one.
        //
        // This is judged at the meeting points, not over the whole record. The reason is the same as in
        // MixTechniques.Best. A changeover meets an outro and an intro. Neither one is the average of
        // the record it belongs to.
        //
        // The FadeBars test is as much a recursion guard as a rule. Shaped reaches this method again
        // through Matched, and Matched always names a length. So the inner call cannot take this branch
        // a second time. A length asked for by hand keeps its normal meaning.
        if (onPhrase && policy.PhraseMoves && overrides is not { Style: not null } && overrides is not { FadeBars: > 0 } &&
            BothHeavyDownLow(from!, to!, startAt, incoming, trace))
        {
            return Shaped(
                TransitionStyle.BassSwap, outgoing, outgoingBpm, incoming, startAt, wantBars,
                canMatch, policy, overrides, trace);
        }

        // One of the six added moves, asked for by hand. It is taken before the moves the app started
        // with, and separately from them, so that block stays exactly as it was.
        if (overrides is { Style: { } wanted } && IsAddedMove(wanted))
            return Shaped(wanted, outgoing, outgoingBpm, incoming, startAt, wantBars, canMatch, policy, overrides, trace);

        // A style that was asked for, but that the records cannot support, is refused rather than
        // relabelled. The whole point of naming a style is that it says what the listener is about to
        // hear. A beat-matched changeover between two records with no grid between them is a blend with
        // the wrong name.
        if (overrides is { Style: { } askedStyle })
        {
            if (askedStyle is TransitionStyle.BeatMatched or TransitionStyle.PhraseBlend && !canMatch)
                trace.Add($"refused: {askedStyle} asked for, and these two cannot be matched");
            else if (askedStyle == TransitionStyle.Blend)
                return Blend(incoming, startAt, outgoingBpm, trace, overrides);
            else if (askedStyle == TransitionStyle.Cut)
                return Cut(incoming, startAt, trace, overrides);

            // The other planner's changeover, asked for here. The record is handed over on a loop of
            // its own bar, rather than mixed out of. A record that reaches its outro and is rolled out
            // of instead is worth having. It is the sound of a set that does not fade every time. The
            // roll starts at the same point the mix would have.
            else if (askedStyle == TransitionStyle.RollOut)
                return Roll(outgoing, outgoingBpm, incoming, startAt, overrides, trace);
        }

        if (canMatch)
        {
            // The tempo each record is running at where the two actually meet, not the tempo of the
            // whole of either. See TrackAnalysis.BpmAt and Local below. The reading is taken over the
            // span the changeover will occupy, because that is the span the reading has to hold for. A
            // tempo measured over one bar, then applied to sixteen bars, inherits any error at either end
            // of the bar.
            double outHere = from!.BpmAt(startAt, Span(from, policy.OutroFadeBars));
            double inHere = to!.BpmAt(to.IntroEndSeconds, Span(to, policy.OutroFadeBars));
            Local(from, outHere, "outgoing", startAt, trace);
            Local(to, inHere, "incoming", to.IntroEndSeconds, trace);

            var meet = Meet(outHere, inHere, outgoingBpm, policy);
            double outNative = outHere;
            double inNative = meet.InNative;
            double target = meet.Target;
            double outRatio = meet.OutRatio;
            double ratio = meet.InRatio;
            double reached = meet.Reached;
            double outReached = meet.OutReached;

            double mixBpm = outReached;

            trace.Add(
                $"meet at {target:F2}: outgoing {outNative:F2}x{outRatio:F4} = {outReached:F2}, " +
                $"incoming {inHere:F2} counted as {inNative:F2}x{ratio:F4} = {reached:F2}");

            if (Math.Abs(inNative - inHere) > 0.01)
                trace.Add($"incoming counted at the outgoing octave: {inHere:F2} read as {inNative:F2}");

            if (Math.Abs(Math.Abs(target / outNative) - outRatio) > 1e-9)
                trace.Add($"outgoing pull clamped at {policy.MaxTempoPull:P0}, wanted {target / outNative:F4}");
            if (Math.Abs(Math.Abs(target / inNative) - ratio) > 1e-9)
                trace.Add($"incoming pull clamped at {policy.MaxTempoPull:P0}, wanted {target / inNative:F4}");

            // A bar is four beats of what the room is counting. That is not always the octave the
            // reading came back at. See <see cref="TempoEstimator.DanceLevel"/>. Counting bars on a
            // reading an octave low makes every bar twice as long, so a sixteen-bar changeover would
            // run for thirty-two.
            double barSeconds = MixBarSeconds(mixBpm);

            // Four, and not the outgoing record's own BeatsPerBar. This is the one place where a
            // measured bar length is not used. So a record counted in threes has its bar lines snapped
            // on three, and its fade measured on four. This is noted here, and not kept quiet, because
            // the plan that comes out looks exactly like a correct one.
            if (from.BeatsPerBar != 4)
                trace.Add(
                    $"record counts {from.BeatsPerBar} to a bar, fade measured on 4: " +
                    $"bar {barSeconds:F2}s against a counted {60.0 / TempoEstimator.DanceLevel(mixBpm) * from.BeatsPerBar:F2}s");

            // Whatever tempo the clamps could not reach, as the rate the two grids drift apart.
            double mismatch = Math.Abs(reached - outReached) / outReached;

            // A phrase blend is two records audible together for its whole length. For that, they have
            // to be on ONE tempo, not on two that are near enough to each other. Anything the pitch
            // faders could not close drifts the grids apart from the first bar. Over sixteen bars even
            // a tenth of a percent is most of a beat by the end. Two kicks that started as one finish
            // as a flam.
            //
            // The ordinary changeover budgets for that drift and shortens itself to stay inside it
            // (below). That is the right answer for a fade whose ends are barely audible. It is the
            // wrong answer here. This move has no barely audible ends, so there is no stretch of it
            // where a drift would go unheard. It must be exact, or it does not happen.
            if (onPhrase && mismatch > TempoLockTolerance)
            {
                trace.Add(
                    $"no phrase blend: {mismatch:P4} apart after both pulls, and this changeover needs " +
                    $"the two locked to one tempo for the whole of it");
                onPhrase = false;
                startAt = OnAFour(from, named);
            }

            // Every shortening below halves the count rather than trimming seconds off it: sixteen
            // bars, eight, four. A changeover is a whole number of phrases or it is not a phrase at
            // all. Eleven point three bars of a phrase would put the new record's first bar in the
            // middle of the old one's.
            int bars = wantBars;

            // Asked for in the record's own repeats rather than in bars. This is off by default. The
            // phrase decides where a changeover STARTS. How long it runs is a separate question, and
            // the ordinary fade length already answers it.
            //
            // It is worked out in seconds and then turned back into mix bars. The two are not the same
            // bar. The mix counts at the octave the room is dancing at, while the record's phrase is
            // measured in its own bars. On a reading an octave away from the count, those differ by a
            // factor of two.
            //
            // It is still a power of two away from wantBars. The phrase is 8, 16 or 32 bars, and the
            // octave factor is itself a power of two. So the halving below still reaches one, without
            // ever landing on a fraction of a bar.
            if (onPhrase && policy.BlendPhrases > 0 && overrides is not { FadeBars: > 0 })
            {
                double phrase = from.BarsPerPhrase * OwnBarSeconds(from, outRatio);
                int asked = Math.Max(1, policy.BlendPhrases);
                bars = Math.Max(1, (int)Math.Round(asked * phrase / barSeconds));
                trace.Add(
                    $"phrase blend: {asked} x {from.BarsPerPhrase}-bar phrase of {phrase:F2}s = " +
                    $"{bars} bars of the mix's {barSeconds:F2}s");
            }

            // The record also has to have a blend's worth of body to put it in. A phrase blend is two
            // records laid over each other while both are in full flow. A record whose outro arrives
            // before there is room for one has nothing to lay over anything. A short blend is not a
            // small version of this move. It is the ordinary changeover with the wrong name on it.
            //
            // So the mode gives way, not the rule. What comes back is the changeover the planner would
            // have made anyway: at the outro, on the fours, at the usual length. This is said out loud
            // in the trace. A mode that quietly does something else on some records is a mode nobody
            // can tell is working.
            if (onPhrase)
            {
                double body = from.OutroStartSeconds > 0 ? from.OutroStartSeconds : playsTo;
                if (bars * barSeconds > body)
                {
                    trace.Add(
                        $"no phrase blend: {bars} bars is {bars * barSeconds:F1}s and this record has " +
                        $"{body:F1}s of body in front of its outro at {MixInputs.Sec(body)}");
                    onPhrase = false;
                    bars = wantBars;
                    startAt = OnAFour(from, named);
                }
            }

            // Whatever tempo the clamps could not reach drifts the grids apart for as long as the two
            // records run together. That drift, and not the size of the pull, decides whether this is a
            // beat-match. The changeover is shortened to the number of bars that stays inside
            // MaxDriftSeconds. If even one bar cannot stay inside it, the planner does not pretend this
            // is a beat-match. It blends instead.
            if (mismatch > TempoLockTolerance)
            {
                double affordable = policy.MaxDriftSeconds / mismatch;
                if (affordable < barSeconds)
                {
                    trace.Add(
                        $"blending: {mismatch:P3} apart drifts {policy.MaxDriftSeconds * 1000:F0} ms in " +
                        $"{affordable:F2}s, which is under one bar of {barSeconds:F2}s");
                    return Blend(incoming, startAt, outgoingBpm, trace, overrides);
                }

                int before = bars;
                while (bars > 1 && bars * barSeconds > affordable)
                    bars /= 2;
                if (bars != before)
                    trace.Add(
                        $"drift: {before} bars down to {bars}, {mismatch:P3} apart affords {affordable:F2}s");
            }

            // Neither the intro nor the outro is mixed over. The changeover FINISHES where the outro
            // begins, rather than starting there. So both records are in their bodies for the whole of
            // it. The old one is at full until it is taken away, and what plays after the blend is the
            // new record's own music. It is not two thin ends laid over each other.
            //
            // This is the opposite of what the app does everywhere else, on purpose. See MixInPoint,
            // where the intro is played UNDER the outgoing record. That changeover hands one record to
            // the next. This one lays two records over each other. For that, the parts a record was
            // written to be mixed over are the wrong parts to use.
            //
            // Where each record sings, worked out once. Both readings scan a whole curve on a record
            // with no lyric sheet, and the search below asks about them a hundred times.
            //
            // A record nobody has read is taken to sing from end to end. This is the cautious half of
            // the guess. Reading an unread record as silence would make it the safest record in the
            // crate to lay a vocal over. See VocalMap.SingingThroughout.
            var outVoices = Assume(outgoing);
            var inVoices = Assume(incoming);

            // Where the record coming in was cued to avoid the two of them singing together, for the
            // changeover that is allowed to move it. Null everywhere else. There, the cue is worked out
            // the way it always was. See below, where this is read.
            double? voiceCue = null;

            // Not where a hand, or a technique planning through Matched, has already named the point.
            // The rules below choose a place to blend. A caller that has chosen one has done that job
            // already. Moving it would be the planner overruling its own callers.
            if (onPhrase && overrides is not { StartAtSeconds: not null })
            {
                double outroAt = from.OutroStartSeconds > 0 ? from.OutroStartSeconds : playsTo;
                double before = startAt;
                double fadeSeconds = bars * barSeconds;

                // The back of the record first: the latest phrase line that still leaves room for the
                // whole blend in front of the outro.
                double latest = Align(from, Math.Max(0, outroAt - fadeSeconds), onPhrase);

                // Then both ends at once. This looks for a place where the two records are not singing
                // over each other. Two records can share drums, but they cannot share voices. A
                // sixteen-bar layer under two vocals is two people talking over each other. It is the
                // one fault in a long blend that no amount of beat-matching fixes.
                //
                // Both ends are searched, because a phrase blend does not touch either record's intro.
                // So the place where the new record comes in is a free choice, and moving it costs
                // nothing. Moving the exit costs a phrase of the record that is playing. See
                // VoicePlacement.
                var placed = VoicePlacement.Choose(
                    new(from, outVoices, latest, from.PhraseSeconds),
                    new(to!, inVoices, to!.MeasuredPhraseAtOrAfter(to.IntroEndSeconds),
                        to.PhraseSeconds),
                    fadeSeconds, VoicePlacement.CueIn.Free);

                startAt = placed.StartAt;
                voiceCue = placed.CueIn;
                trace.Add(Moved(placed, latest, outVoices, inVoices));

                if (Math.Abs(startAt - before) > 1e-6)
                    trace.Add(
                        $"clear of the outro: {bars} bars back from {MixInputs.Sec(outroAt)} to " +
                        $"{MixInputs.Sec(startAt)}, was {MixInputs.Sec(before)}");
            }
            else if (!onPhrase && overrides is not { StartAtSeconds: not null })
            {
                // The ordinary changeover. This is the one most sets are made of. The voice check runs
                // here too, and not only inside the phrase blend, which is off by default.
                //
                // Only one end moves here, not two. This changeover cues the record coming in a fade's
                // length before its intro ends, SO THAT its drop lands as the fade finishes. That is
                // the whole reason its intro is played under the old record. Moving that cue would not
                // trade one vocal for another. It would throw the arrival away. So the cue is Fixed and
                // the exit does the work. That is still worth doing, because what the new record sings
                // over the fade is fixed, and what the old one sings is not.
                double fadeSeconds = bars * barSeconds;

                // One step is one of this record's own four-bar lines (TrackAnalysis.PhraseBars). These
                // are the same lines that Align snaps to on this path. So every candidate is on a line
                // that the starting point was already on, and nothing has to be snapped again
                // afterwards.
                double fourSeconds = TrackAnalysis.PhraseBars * from.BarSeconds;
                double was = startAt;

                var placed = VoicePlacement.Choose(
                    new(from, outVoices, startAt, fourSeconds),
                    new(to!, inVoices, to!.BarAtOrAfter(MixInPoint(incoming, fadeSeconds)), 0),
                    fadeSeconds, VoicePlacement.CueIn.Fixed);

                startAt = placed.StartAt;
                trace.Add(Moved(placed, was, outVoices, inVoices));
            }

            // The fade has to finish on the record, not run off the end of it. Mixing out "at the
            // outro" only works for a record that fades out. A record that ends on a beat reports an
            // outro a second before its last sample. A changeover planned from there has no room to
            // happen in. That is how a sixteen-bar mix turns into a one-bar cut.
            int beforeLength = bars;
            while (bars > 1 && bars * barSeconds > playsTo)
                bars /= 2;
            if (bars != beforeLength)
                trace.Add($"record length: {beforeLength} bars down to {bars}");
            startAt = Math.Max(0, Math.Min(startAt, playsTo - bars * barSeconds));

            // Both ends land on a line of their own grid. The outgoing record is on one of its fours.
            // The incoming record is on a bar. That is what makes the two agree once the tempos do.
            //
            // This is done again here, because the clamp above may have moved the point back to make
            // room for the fade. A point moved off a four has to go back onto one.
            double unsnapped = startAt;
            startAt = Align(from, startAt, onPhrase);
            if (Math.Abs(startAt - unsnapped) > 1e-6)
                trace.Add(
                    $"snapped back to the {(onPhrase ? "phrase" : "four")} at " +
                    $"{MixInputs.Sec(startAt)}, from {MixInputs.Sec(unsnapped)}");

            if (from.IsExtrapolatedAt(startAt))
                trace.Add(
                    $"that bar line is extrapolated: the tracker stopped at " +
                    $"{MixInputs.Sec(from.BeatLastSeconds)} and the grid is a straight line after it");

            // Whatever room is left after all that. A record too short to hold even one bar still has
            // to end somewhere.
            double room = Math.Max(0, playsTo - startAt);
            int beforeRoom = bars;
            while (bars > 1 && bars * barSeconds > room)
                bars /= 2;
            if (bars != beforeRoom)
                trace.Add($"room after the snap: {beforeRoom} bars down to {bars}, {room:F1}s left");

            double fade = bars * barSeconds;
            if (fade > room)
            {
                trace.Add($"fade cut to the {room:F1}s that is left");
                fade = Math.Max(CutSeconds, room);
            }

            // The bar, and deliberately not the four-bar line that the next key cues to.
            //
            // This cue is not chosen for where it sits in the incoming record's own phrasing. It is
            // chosen so that the record's drop lands at the exact moment the changeover finishes. That
            // is what the intro is being played under the old record for. Moving it back to a four
            // would put the drop up to four bars past the end of the fade, where there is nothing for
            // it to land against. The record coming in starts where its own music does, not before. The
            // ordinary changeover cues a fade's length EARLIER than the intro end. That way the intro
            // plays under the old record, and the drop lands as the fade finishes. A blend that must
            // not touch the intro cannot do that. So it starts at the first phrase of the body instead.
            // Unless the voice search moved it. That only happens on a phrase blend, and only where it
            // found the two records singing over each other at the record's own first phrase. See
            // VoicePlacement.CueIn for why the ordinary changeover's cue is not a free choice.
            double cueIn = onPhrase
                ? voiceCue ?? to.MeasuredPhraseAtOrAfter(to.IntroEndSeconds)
                : to.BarAtOrAfter(MixInPoint(incoming, fade));

            if (onPhrase)
                trace.Add(
                    $"clear of the intro: coming in at {MixInputs.Sec(cueIn)}, the first phrase at or " +
                    $"after its intro end of {MixInputs.Sec(to.IntroEndSeconds)}");

            if (overrides is { CueInSeconds: { } askedCue })
            {
                trace.Add($"asked to cue at {MixInputs.Sec(askedCue)} instead of {MixInputs.Sec(cueIn)}");
                cueIn = to.BarAtOrAfter(Math.Clamp(askedCue, 0, Math.Max(0, incoming.DurationSeconds)));
            }

            if (wantBars != bars)
                trace.Add($"{wantBars} bars asked for, {bars} played");

            // And now the pull is worked out again, against the two grids over the stretch they will
            // actually run together. See SpanLock. Everything above chose the pull from a tempo read at
            // one point. That is the right number for that point. It drifts away from the record on
            // either side of it.
            //
            // Then the two grids are read through the MIDDLE of the changeover. Nothing did this
            // before. Everything above is an argument about tempo. Each of its answers is checked at
            // the ends: the two tempos meet, the drop is held to the bar line, and the pull is bent so
            // the far end lands on the sample as well. A record whose grid wanders satisfies all of
            // that, and it is still half a beat out in the middle. It runs ahead of a constant pull for
            // a while, and behind it for a while. Two ends landing together says nothing about where it
            // was in between. Both faders are up for all of that time.
            //
            // What is done about it, in order: play the speed that keeps the beats landing (see
            // TempoStep: the pull is cut into pieces, and each piece follows the grid). Then shorten
            // the changeover. Then refuse to call it a beat-match at all. The first is what a sync
            // button does, and it costs the room nothing. The other two give something up, so they
            // happen only when the first is not enough.
            var settling = new List<string>();
            var (steps, drift) = Settle(bars, fade, trace);
            int beforeDrift = bars;
            while (drift > policy.MaxDriftSeconds && bars > 1)
            {
                bars /= 2;
                fade = Math.Min(bars * barSeconds, Math.Max(CutSeconds, room));
                settling.Clear();
                (steps, drift) = Settle(bars, fade, settling);
            }

            ratio = steps[0].Ratio;

            // The pull that holds the pair, in as few pieces as will do it. One piece is the ordinary
            // changeover and needs no telling. More than one is worth saying, because a record whose
            // speed changes part way through a mix is something a listener will hear and go looking
            // for.
            if (steps.Count > 1)
                trace.Add(
                    $"the pull follows the grid: {steps.Count} pieces, " +
                    $"{string.Join(" then ", steps.Select(s => $"x{s.Ratio:F5}"))}, " +
                    $"changing on the bar at {string.Join(", ", steps.Skip(1).Select(s => $"{s.AtSeconds:F1}s"))}");

            // How the pull is settled for a given length. First SpanLock's single answer. Then the same
            // arithmetic in pieces, for as long as pieces keep helping. This is local, because the loop
            // above needs the whole of it again each time it shortens the changeover.
            (IReadOnlyList<TempoStep> Steps, double Drift) Settle(int forBars, double forFade, List<string> log)
            {
                double first = SpanLock(
                    from, startAt, outRatio, to, cueIn, meet.InRatio, forFade, inNative, policy, log);

                IReadOnlyList<TempoStep> best = new[] { new TempoStep(0, cueIn, first) };
                double worst = GridDrift(from, startAt, outRatio, to, cueIn, best, forFade, inNative);

                for (int pieces = 2; pieces <= MaxPullPieces && worst > policy.MaxDriftSeconds; pieces *= 2)
                {
                    if (forBars < pieces)
                        break;

                    var tried = Warp(
                        from, startAt, outRatio, to, cueIn, first, forFade, inNative, pieces, policy);
                    double apart = GridDrift(from, startAt, outRatio, to, cueIn, tried, forFade, inNative);
                    if (tried.Count > 1 && apart < worst)
                        (best, worst) = (tried, apart);
                }

                return (best, worst);
            }

            if (drift > policy.MaxDriftSeconds)
            {
                // There is nowhere left to go. The grids will not hold for one bar, so no beat-match is
                // possible at any length. A one-bar beat-match is not a small beat-match. It is the
                // same claim, made where it cannot be backed up. The blend is the move that is honest
                // about two records that will not lock.
                trace.Add(
                    $"no beat-match: the grids walk {drift * 1000:F0} ms apart inside even one bar of " +
                    $"{barSeconds:F2}s, past the {policy.MaxDriftSeconds * 1000:F0} ms two kicks stop " +
                    $"sounding like one at");
                return Blend(incoming, startAt, outgoingBpm, trace, overrides);
            }

            if (bars != beforeDrift)
            {
                trace.AddRange(settling);
                trace.Add(
                    $"grids: {beforeDrift} bars down to {bars}, which the two hold to within " +
                    $"{drift * 1000:F0} ms of each other");
            }

            // Where in the changeover the record coming in reaches full. That is where its drop has to
            // land. It is the whole reason its intro is played under the old record at all.
            //
            // Normally the cue was taken a fade's length before the intro ends. So the answer is the
            // end of the fade, and the arc is the ordinary one. A record whose intro is SHORTER than
            // the changeover cannot be cued that far back. There is no audio before its first sample.
            // Without this, the record would start at its very top, the whole intro would play under
            // the old one, and the drop would arrive part way up the fade while the record was still
            // quiet.
            //
            // So the record comes up over its own intro instead of over the fade. The outgoing record
            // still fades across the whole of it. See FadeArc.IncomingUpBy. A record with no intro at
            // all falls out of the same arithmetic at zero. It arrives at full with no ramp, which is
            // the only honest thing to do with a record that starts on its drop.
            var arc = overrides?.Arc;
            if (arc is null && !onPhrase && fade > 0 && to is { IntroEndSeconds: > 0 })
            {
                double upBy = Math.Clamp((to.IntroEndSeconds - cueIn) / fade, 0, 1);

                // A bar of slack. The cue is snapped forward onto a bar line of the record coming in.
                // On a record with plenty of intro, the drop already lands a fraction of a bar inside
                // the fade. An arc drawn for that would be a curve nobody asked for, over a rounding
                // nobody can hear. The arc is for a record that really cannot be cued far enough back.
                if (upBy < 1 - barSeconds / fade)
                {
                    arc = FadeArc.EqualPower().IncomingUpBy(upBy);
                    trace.Add(
                        $"short intro: the record coming in is up by {upBy:P0} of the changeover, at " +
                        $"its drop at {MixInputs.Sec(to.IntroEndSeconds)} — cued at " +
                        $"{MixInputs.Sec(cueIn)} with only {to.IntroEndSeconds - cueIn:F1}s of intro " +
                        $"to play under a {fade:F1}s fade");
                }
            }

            return new TransitionPlan
            {
                Incoming = incoming,

                // The two are one method and two changeovers. They are named apart because nothing
                // about their layout, their length or their failure is shared. See
                // TransitionStyle.PhraseBlend. The plan reports whichever one was actually made, which
                // is not always the one asked for. Every give-way above turns onPhrase off. A plan that
                // came back from one of those is the ordinary changeover, and it has to say so.
                Style = onPhrase ? TransitionStyle.PhraseBlend : TransitionStyle.BeatMatched,
                StartAtSeconds = startAt,
                CueInSeconds = cueIn,
                TempoRatio = ratio,
                OutgoingTempoRatio = outRatio,
                TempoMismatch = meet.Mismatch,
                FadeSeconds = fade,
                ReleaseSeconds = Math.Abs(ratio - 1) < 1e-6 ? 0 : policy.ReleaseBars * barSeconds,
                MixBpm = mixBpm,
                BarSeconds = barSeconds,
                GridDriftSeconds = drift,
                TempoSteps = steps.Count > 1 ? steps : [],
                FadeBars = (int)Math.Round(fade / barSeconds),
                Trace = trace,
                Overrides = overrides is { IsEmpty: false } ? overrides : null,
                Arc = arc,
            };
        }

        // The record coming in cannot be read, so this is a cut. The exception is when the luck says
        // otherwise and the record PLAYING has a bar line to leave on. See EchoOutChance.
        //
        // The new record starts at its own beginning, not at its mix-in point. Nothing is mixed over
        // it. The echo tail rings out alone, and the record lands dry on the next bar. So there is no
        // old record for an intro to play under. Skipping past the intro would only throw part of the
        // track away. This is the same reason the opening record of a set starts at zero.
        if (overrides is null or { Style: null, CueInSeconds: null } &&
            from is { CanBeatMatch: true } &&
            incoming.Analysis is not { CanBeatMatch: true } &&
            luck < EchoOutChance)
        {
            trace.Add(
                $"echo out rather than a cut: luck {luck:F2} under {EchoOutChance:F2}, and the record " +
                $"playing has a grid to throw the echo on");

            return Gesture(
                TransitionStyle.EchoOut, EchoBars, outgoing, outgoingBpm, incoming, startAt,
                wantBars, canMatch: false, policy, overrides, trace) with { CueInSeconds = 0 };
        }

        return Blend(incoming, startAt, outgoingBpm, trace, overrides);
    }

    /// <summary>
    /// How wide the switch in a double drop is: one beat.
    ///
    /// <para>This is not a fade and not a cut. The two records never play together. The one leaving
    /// stops on its drop, and the one arriving starts on its own. So there is no crossing to shape. The
    /// beat is there for the same reason as <see cref="SlamSeconds"/>. Two waveforms joined at whatever
    /// value they happened to be at make a click.</para>
    ///
    /// <para>It is a beat, not the thirty milliseconds of <see cref="SlamSeconds"/>, because both
    /// records are at full through it. Thirty milliseconds between two drops is a join that is heard as
    /// a glitch. A beat is a join that nobody hears.</para>
    /// </summary>
    public const double DoubleDropSwitchBeats = 1;

    /// <summary>
    /// One bar of the mix, in seconds: four beats of the tempo the room is actually counting. See
    /// <see cref="TempoEstimator.DanceLevel"/>. This is the only definition. The roll-overlap
    /// arithmetic depends on the fact that two bars only ever differ by a power of two. Two copies of
    /// this formula could drift apart, and then that promise would break.
    /// </summary>
    private static double MixBarSeconds(double bpm) => 4 * 60.0 / TempoEstimator.DanceLevel(bpm);

    /// <summary>
    /// The four-bar line at or before <paramref name="seconds"/>, on the record's own grid, counted
    /// from the bar where its drums come in. See <see cref="TrackAnalysis.PhraseAtOrBefore"/>. If the
    /// record has no grid to count on, the time itself is used.
    ///
    /// <para>Backwards, because a changeover is a length of record given away. Taken forwards, it would
    /// use up to four bars of what is left. The room hears the reserve at the end of a record long
    /// before it hears four bars from the middle of one.</para>
    /// </summary>

    /// <summary>Where a record sings. An unread record is taken to sing throughout. This is the
    /// cautious half of the guess. See VocalMap.SingingThroughout.</summary>
    private static VocalMap Assume(MixTrack track) =>
        track.Voices is { IsKnown: true } known
            ? known
            : VocalMap.SingingThroughout(track.DurationSeconds);

    /// <summary>
    /// What the voice search did, for the trace. This includes when it did nothing. That is the answer
    /// most of the time, and it is the one a person reading a plan needs to tell apart from "this never
    /// ran".
    /// </summary>
    /// <param name="was">Where the outgoing record was going to hand over before the search.</param>
    private static string Moved(
        VoicePlacement.Placed placed, double was, VocalMap outVoices, VocalMap inVoices)
    {
        var read = $"voices read from {Told(outVoices)} and {Told(inVoices)}";

        if (!outVoices.IsKnown && !inVoices.IsKnown)
            return $"voices: neither record read, changeover left where it was";

        if (placed.Unmoved)
            return placed.Voices.Clash
                ? $"voices: nowhere better within reach, {placed.Voices} ({read})"
                : $"voices: {placed.Voices} where it was already going ({read})";

        var moved = placed.StepsBack > 0
            ? placed.StepsIn > 0
                ? $"out {placed.StepsBack} back to {MixInputs.Sec(placed.StartAt)} and in " +
                  $"{placed.StepsIn} on to {MixInputs.Sec(placed.CueIn)}"
                : $"out {placed.StepsBack} back to {MixInputs.Sec(placed.StartAt)}, was {MixInputs.Sec(was)}"
            : $"in {placed.StepsIn} on to {MixInputs.Sec(placed.CueIn)}";

        return $"voices: {moved} — {placed.Voices} ({read})";
    }

    /// <summary>Where one record's voice reading came from, named rather than numbered. A sheet is
    /// exact and the bands are a guess. A plan that acted on one should say which.</summary>
    private static string Told(VocalMap voices) => voices.Source switch
    {
        VocalMap.Told.Sheet => voices.IsInstrumental ? "a sheet (instrumental)" : "a sheet",
        VocalMap.Told.Bands => "the bands",

        // The ordinary case, on a record that has been both written down and measured. See
        // VocalMap.For. Showing "nothing" here would make the trace claim that the changeover was
        // placed blind.
        VocalMap.Told.Agreed =>
            voices.IsInstrumental ? "sheet and bands (instrumental)" : "sheet and bands",

        _ => "nothing",
    };

    private static double OnAFour(TrackAnalysis? analysis, double seconds) =>
        analysis is null ? seconds : Math.Max(0, analysis.PhraseAtOrBefore(seconds));

    /// <summary>Where a changeover may start on this record: one of its fours. If the policy asked for
    /// a phrase blend and the record was measured to have phrases, it is the start of a phrase instead.
    /// In both cases the point moves backwards, for the reason given at
    /// <see cref="OnAFour"/>.</summary>
    private static double Align(TrackAnalysis? analysis, double seconds, bool onPhrase) =>
        analysis is null ? seconds
            : onPhrase ? Math.Max(0, analysis.MeasuredPhraseAtOrBefore(seconds))
            : Math.Max(0, analysis.PhraseAtOrBefore(seconds));

    /// <summary>
    /// The four-bar line at or after <paramref name="seconds"/>, on the record's own grid.
    ///
    /// <para>This goes forwards. It is the other half of <see cref="OnAFour"/>, and it belongs to the
    /// next key. A press asks for the record to be left. A line behind the hand is a line that has
    /// already gone by.</para>
    /// </summary>
    private static double OnTheFourAfter(TrackAnalysis? analysis, double seconds) =>
        analysis is null ? seconds : Math.Max(0, analysis.PhraseAtOrAfter(seconds));

    /// <summary>How many beats a changeover of <paramref name="bars"/> bars covers on this record. This
    /// is the span its tempo has to be right over, and so the span to read that tempo across.</summary>
    private static int Span(TrackAnalysis analysis, int bars) =>
        Math.Max(1, bars) * Math.Max(1, analysis.BeatsPerBar);

    /// <summary>
    /// Says so when a record is running at a different tempo where the changeover meets it than the one
    /// in the table.
    ///
    /// <para>This is printed only when it matters. That is when the difference would use up the
    /// changeover's whole drift allowance (see <see cref="MixPolicy.MaxDriftSeconds"/>). Below that,
    /// the two numbers say the same thing, and printing both would be noise. Above it, a plan that says
    /// 126 while the record under the needle is doing 127 looks like a bug, until the reader knows that
    /// the grid bends.</para>
    /// </summary>
    private static void Local(
        TrackAnalysis analysis, double here, string which, double seconds, List<string> trace)
    {
        if (analysis.Bpm <= 0 || here <= 0)
            return;

        double apart = Math.Abs(here - analysis.Bpm) / analysis.Bpm;
        if (apart * OutroFadeBars * (60.0 / analysis.Bpm * Math.Max(1, analysis.BeatsPerBar))
            > MaxDriftSeconds)
            trace.Add(
                $"{which} is doing {here:F2} at {MixInputs.Sec(seconds)}, not the {analysis.Bpm:F2} " +
                $"in the table: the grid bends by {apart:P2} over the record");
    }

    /// <summary>
    /// The two tempos meet in the middle, geometrically, at one octave. Both changeovers and matched
    /// rolls use this arithmetic. Dragging the incoming record all the way onto the outgoing one bends
    /// it by the whole difference. Splitting the difference bends each record by half. That is less
    /// audible, and it is what two pitch faders do. The midpoint is geometric because tempo is heard in
    /// ratios. Both records are counted at the same octave first, because two counts of the same tempo
    /// can look like a factor of two. That would need a pull that no fader reaches.
    /// </summary>
    private static TempoMeet Meet(double outNative, double inBpm, double playingBpm, MixPolicy policy)
    {
        double inNative = TempoEstimator.MatchLevel(inBpm, playingBpm);
        double target = Math.Sqrt(playingBpm * inNative);

        double outRatio = Math.Clamp(target / outNative, 1 - policy.MaxTempoPull, 1 + policy.MaxTempoPull);
        double inRatio = Math.Clamp(target / inNative, 1 - policy.MaxTempoPull, 1 + policy.MaxTempoPull);

        return new TempoMeet(target, outRatio, inRatio, inNative * inRatio, outNative * outRatio, inNative);
    }

    private readonly record struct TempoMeet(
        double Target, double OutRatio, double InRatio, double Reached, double OutReached, double InNative)
    {
        /// <summary>Whatever tempo the clamps could not reach, as the rate the two grids drift
        /// apart.</summary>
        public double Mismatch => Math.Abs(Reached - OutReached) / OutReached;
    }

    /// <summary>
    /// How long one bar of this record's own grid lasts when it is played at <paramref name="ratio"/>,
    /// in heard seconds.
    ///
    /// <para>This uses the record's own beats and its own count. That is the only bar a loop may be cut
    /// to, if the record is going to play on out of it and still be where its grid says it is. It is
    /// not <see cref="TempoEstimator.DanceLevel"/>, and it is not four. A record read an octave from
    /// the count has bar lines twice as far apart as the room is counting. A record measured in three
    /// has them three beats apart.</para>
    /// </summary>
    private static double OwnBarSeconds(TrackAnalysis analysis, double ratio)
    {
        if (analysis.Bpm <= 0 || ratio <= 0)
            return 0;

        int perBar = Math.Max(1, analysis.BeatsPerBar);
        return perBar * 60.0 / (analysis.Bpm * ratio);
    }

    /// <summary>
    /// Whether two bar lengths land on the same line often enough to be laid over each other. That is,
    /// one is a whole number of the other.
    ///
    /// <para>This is true for every pair an octave apart, which is the normal case. A record read at
    /// half the count has a bar line at every second bar of the mix. It is false for three against
    /// four. There the lines coincide once every three bars, and the loop would start from the wrong
    /// place the other two times.</para>
    /// </summary>
    private static bool SharesABarLine(double one, double other)
    {
        if (one <= 0 || other <= 0)
            return false;

        double ratio = one > other ? one / other : other / one;
        return Math.Abs(ratio - Math.Round(ratio)) < 0.01;
    }

    /// <summary>Why a record can or cannot be matched, for the trace.</summary>
    private static string Match(TrackAnalysis? analysis) =>
        analysis is null ? "unread"
            : analysis.CanBeatMatch ? "ok"
            : analysis.Bpm <= 0 ? "no tempo"
            : analysis.BpmConfidence < TrackAnalysis.MinBeatMatchConfidence
                ? $"tempo confidence {analysis.BpmConfidence:F2}"
                : $"only {analysis.BeatTimes.Count} beats";

    /// <summary>The first plan of a set. Nothing is playing, so the only decision is where the needle
    /// goes down: straight in from the top, at the record's own tempo.</summary>
    public static TransitionPlan Opening(MixTrack first) => new()
    {
        Incoming = first,
        Style = TransitionStyle.Cut,
        StartAtSeconds = 0,

        // From the top, not from the mix-in point. A cue point exists so that a record arriving over
        // another one lands on something worth hearing, instead of on its build. But the first record
        // of a set arrives over nothing. So skipping its intro would only throw part of the track away.
        // The opening record is played, not mixed in.
        CueInSeconds = 0,
        TempoRatio = 1,
        FadeSeconds = 0,
        MixBpm = first.Bpm,
        Trace = [$"opening record, played from the top at its own {first.Bpm:F1}"],
    };

    /// <summary>
    /// The next key: the record playing is rolled out, and <paramref name="incoming"/> takes over.
    ///
    /// <para>This is not a mix. The two records never play together. The record on the deck runs to its
    /// next bar line. That bar is then looped for <see cref="RollOutBars"/> bars. The fader comes down,
    /// and a filter takes the top and then the middle away. What is left at the end is a kick under
    /// nothing. Everyone in the room can hear a record coming before it arrives. That expectation is
    /// most of what makes the next record land.</para>
    ///
    /// <para>The new record comes in at the bar where its own instruments start (see
    /// <see cref="MixInFinder"/>). It does not come in before that bar, because there is no long fade
    /// for an intro to sit under here. It arrives over the last <see cref="RollInBars"/> bars of the
    /// loop, rising as the loop goes. It is playing on by the time the loop stops.</para>
    ///
    /// <para>A record with no grid has no bar to loop. That record is cut instead, over
    /// <see cref="SkipCutSeconds"/>. That is long enough not to click, and short enough that the cut happens
    /// at the press.</para>
    /// </summary>
    /// <param name="startAt">Where on the outgoing record the key was pressed.</param>
    public static TransitionPlan SkipTo(
        MixTrack outgoing, double outgoingBpm, MixTrack incoming, double startAt,
        MixOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);
        return Roll(
            outgoing, outgoingBpm, incoming, startAt, overrides,
            [$"next key pressed at {MixInputs.Sec(startAt)}"]);
    }

    /// <summary>
    /// The roll itself. It is reached from the next key and from <see cref="Plan"/>. A changeover asked
    /// to be a roll-out rather than a mix comes here too. It lands at the mix-out point, not at a
    /// press.
    /// </summary>
    private static TransitionPlan Roll(
        MixTrack outgoing, double outgoingBpm, MixTrack incoming, double startAt,
        MixOverrides? overrides, List<string> trace)
    {
        var from = outgoing.Analysis;
        var to = incoming.Analysis;
        double bpm = outgoingBpm > 0 ? outgoingBpm : outgoing.Bpm;

        var policy = overrides?.Policy ?? MixPolicy.Default;
        if (!policy.IsDefault)
            trace.Add($"policy: {policy}");

        // A style that was asked for is a mix and not a hand-over. Two records playing together is
        // Plan's arithmetic, and there is no second copy of it here. This is the same changeover that
        // Plan would have made at the outro, made from where the hand is instead.
        //
        // It moves forward to the four after the press, never back. A mix planned behind the needle has
        // already missed its own start. The fade would then begin at a point that has already passed.
        if (overrides is { Style: { } asked } && asked != TransitionStyle.RollOut)
        {
            if (asked == TransitionStyle.Cut)
            {
                trace.Add("asked to cut rather than roll out");
                return Cut(incoming, startAt, trace, overrides);
            }

            // "Planned from", not "mixed from". Three of the styles that reach here are not mixes at
            // all: the cut on the one, the echo out and the rewind. They hand the record over rather
            // than laying anything over anything.
            double from4 = OnTheFourAfter(from, startAt);
            trace.Add(
                $"asked for {asked}: planned from {MixInputs.Sec(from4)} rather than rolled out");
            return Plan(
                outgoing, bpm, incoming,
                overrides with { StartAtSeconds = overrides.StartAtSeconds ?? from4 },
                trace);
        }

        // A record too long to be mixed is left the hard way. There is no roll to be had out of an hour
        // of somebody else's set. The tempo it reports is an average of everything in it. The bar line
        // under the needle is a straight line through music that changed six times. A loop cut to
        // either would be the wrong length, over the wrong beat. The press should do what it plainly
        // means.
        if (outgoing.DurationSeconds > MaxMixableSeconds)
        {
            trace.Add(
                $"hard skip: {MixInputs.Sec(outgoing.DurationSeconds)} is past the " +
                $"{MixInputs.Sec(MaxMixableSeconds)} a set will mix, so there is nothing here to roll");
            return Cut(incoming, startAt, trace, overrides);
        }

        if (from is not { CanBeatMatch: true } || bpm <= 0)
        {
            trace.Add($"no bar to roll out on: outgoing {Match(from)}, playing at {bpm:F1}");
            return Cut(incoming, startAt, trace, overrides);
        }

        // A press is a moment when somebody's hand lands. The record is left on a line. The set moves
        // forward to the next of the record's fours. It moves forward, not back, because a press means
        // leave, not rewind. It goes to a four and not to the next bar, because the set carries on from
        // here. A record handed over two bars into a phrase leaves everything after it two bars out of
        // phrase.
        double pressed = startAt;
        startAt = OnTheFourAfter(from, startAt);
        if (Math.Abs(startAt - pressed) > 1e-6)
            trace.Add(
                $"held to the four at {MixInputs.Sec(startAt)}, " +
                $"{startAt - pressed:F2}s after the press");

        double ratio = 1, outRatio = 1, mixBpm = bpm, mismatch = 0;
        int overlapBars = 0;

        // Both loops run together for the overlap, so they have to BE one tempo. The arithmetic is the
        // same as for any changeover: meet in the middle, geometrically, at one octave. The only
        // difference is how long they have to hold together. That is four bars rather than sixteen. A
        // pull that would be refused for a mix is comfortable here.
        if (to is { CanBeatMatch: true })
        {
            // This arithmetic is shared with Plan, so a mix and a skip can never beat-match
            // differently. The only difference is how long the two have to hold together: four bars
            // rather than sixteen. The meeting point is where the hand landed, not where the record
            // began. This is the case the local reading exists for. A press can come anywhere, so the
            // tempo the two loops are cut to is read off the bar under the needle, not off the record's
            // average.
            double outHere = from.BpmAt(startAt, Span(from, MatchInBars));
            double inHere = to.BpmAt(to.IntroEndSeconds, Span(to, MatchInBars));
            Local(from, outHere, "outgoing", startAt, trace);
            Local(to, inHere, "incoming", to.IntroEndSeconds, trace);

            var meet = Meet(outHere, inHere, bpm, policy);
            mismatch = meet.Mismatch;
            double bar = MixBarSeconds(meet.OutReached);

            // Whatever tempo the clamps could not reach drifts the two grids apart at a steady rate.
            // Over four bars, a pull the fader cannot quite make is still inaudible. Past that, the two
            // records do not agree, and the overlap is dropped instead.
            if (mismatch <= 1e-9 || policy.MaxDriftSeconds / mismatch >= RollInBars * bar)
            {
                ratio = meet.InRatio;
                outRatio = meet.OutRatio;
                mixBpm = meet.OutReached;
                overlapBars = RollInBars;
                trace.Add(
                    $"loops matched at {meet.OutReached:F2}: outgoing x{meet.OutRatio:F4}, incoming " +
                    $"{inHere:F2} counted as {meet.InNative:F2}x{meet.InRatio:F4}, {mismatch:P3} apart");
            }
            else
            {
                trace.Add(
                    $"no overlap: {mismatch:P3} apart drifts past {policy.MaxDriftSeconds * 1000:F0} ms " +
                    $"inside {RollInBars} bars, so the loop finishes alone");
            }
        }
        else
        {
            trace.Add($"incoming has no grid to loop ({Match(to)}): it follows the roll rather than rising under it");
        }

        // The bar both loops are cut to, at the octave the room is counting. A record read an octave
        // low would otherwise be looped two bars at a time.
        double barSeconds = MixBarSeconds(mixBpm);

        double rollInBar = to is { CanBeatMatch: true } ? OwnBarSeconds(to, ratio) : 0;

        // The two loops run against each other, so they have to agree about where a bar line is. They
        // agree when the record coming in counts in four, whatever octave it was read at. Its bar is
        // then a power of two away from the mix's, and each of its downbeats is one of the mix's.
        //
        // A record counted in three is a different matter. Three beats against four share a bar line
        // only every three bars. So if one loop is laid over the other, the new record's bar line lands
        // somewhere inside the old one's for two turns out of three. That cannot be averaged out. The
        // overlap is dropped, and the loop finishes on its own. This is what happens to two records
        // whose tempos will not meet.
        if (overlapBars > 0 && rollInBar > 0 && !SharesABarLine(rollInBar, barSeconds))
        {
            trace.Add(
                $"no overlap: the record coming in counts {to!.BeatsPerBar} to a bar " +
                $"({rollInBar:F3}s) against the mix's {barSeconds:F3}s, and the two do not share a bar line");
            overlapBars = 0;
        }

        // Counted in the incoming record's own bars from here. It is sized so the overlap lasts the
        // same four bars of the MIX, however long its own bar happens to be.
        //
        // The two counts are the same on most records. On records read away from the count, they are an
        // octave apart. The duration is what matters. Four bars of the mix is what the drift budget
        // above allows for. Four of the incoming record's own bars would be twice the drift the budget
        // allowed. The two lengths are a power of two apart (SharesABarLine has just checked this), so
        // the division always comes out whole.
        if (overlapBars > 0 && rollInBar > 0)
        {
            overlapBars = Math.Max(1, (int)Math.Round(RollInBars * barSeconds / rollInBar));
            if (Math.Abs(overlapBars * rollInBar - RollInBars * barSeconds) > 1e-6)
                trace.Add(
                    $"overlap {overlapBars} of the incoming record's bars = " +
                    $"{overlapBars * rollInBar:F2}s against the mix's {RollInBars * barSeconds:F2}s");
        }

        int rollBars = policy.RollOutBars;
        if (overrides is { FadeBars: > 0 and var askedBars })
        {
            trace.Add($"asked to roll out over {askedBars} bars instead of {rollBars}");
            rollBars = askedBars;
        }

        // A drawn arc is a crossfade's to follow. A roll-out's levels are its own choreography: the
        // loop under a closing filter, and the new record rising through the last bars. A curve drawn
        // over that would be two hands on one fader.
        if (overrides is { Arc: not null })
            trace.Add("volume arc ignored: a roll-out has its own shape");

        // The point where the record's own instruments come in, taken back to the four-bar line it
        // belongs to. The roll hands the record over on a bar line. Landing it on a bar that is not one
        // of its own fours puts the whole record a bar or two out of phrase with the set from there on.
        double cueIn = to is not null ? to.PhraseAtOrBefore(MixInFinder.WhereItStarts(to)) : 0;
        if (overrides is { CueInSeconds: { } askedCue })
        {
            trace.Add($"asked to cue at {MixInputs.Sec(askedCue)} instead of {MixInputs.Sec(cueIn)}");
            cueIn = to is not null
                ? to.PhraseAtOrBefore(Math.Clamp(askedCue, 0, Math.Max(0, incoming.DurationSeconds)))
                : Math.Clamp(askedCue, 0, Math.Max(0, incoming.DurationSeconds));
        }

        trace.Add(
            $"roll out {rollBars} bars of {barSeconds:F2}s, incoming rises over the last " +
            $"{overlapBars} of them, cue {MixInputs.Sec(cueIn)}");

        return new TransitionPlan
        {
            Incoming = incoming,
            Style = TransitionStyle.RollOut,
            StartAtSeconds = startAt,
            CueInSeconds = cueIn,
            TempoRatio = ratio,
            OutgoingTempoRatio = outRatio,
            TempoMismatch = mismatch,
            FadeSeconds = rollBars * barSeconds,

            // A record that landed pulled is let back up to its own speed once it has the room to
            // itself, exactly as one that landed out of a mix is.
            ReleaseSeconds = Math.Abs(ratio - 1) < 1e-6 ? 0 : policy.ReleaseBars * barSeconds,
            MixBpm = mixBpm,
            BarSeconds = barSeconds,
            FadeBars = rollBars,

            // The bar the record coming in loops. This is ITS OWN bar, not the mix's.
            //
            // The two are usually the same. Sometimes they differ by a whole octave. The mix counts
            // bars at the tempo the room is dancing at (DanceLevel). The record's stored grid counts
            // them at whatever octave the reading came back on. For example, an 87 BPM record is danced
            // at 174. So the mix's bar is 1.38s, while the record's own bar lines are 2.76s apart.
            //
            // For the record going OUT, this difference does no harm. Its loop repeats and then stops,
            // and half a bar of a record played round and round still sounds like the record. The
            // record coming IN does not stop. It plays ON, out of the loop and into the track. A loop
            // that is half of its bar leaves it half a bar out from there to the end of the record.
            // That is the changeover that lands on beat three instead of beat one. It happens on
            // exactly the records whose reading sits an octave from the count.
            //
            // Both are still a whole number of the other, because the two only ever differ by a power
            // of two. So the loops still start and finish together. That is the only thing the overlap
            // needs from them.
            RollInBarSeconds = rollInBar,
            OverlapBars = overlapBars,
            Trace = trace,
            Overrides = overrides is { IsEmpty: false } ? overrides : null,
        };
    }

    /// <summary>
    /// How much of a record has to be left for a changeover out of it to be worth planning.
    ///
    /// <para>This is the scrubber's rule. When the listener drags the position into the last minute of
    /// a track, there is no room to mix out of it. The listener has just chosen where to be. Building a
    /// sixteen-bar handover from what is left would move them somewhere else again.</para>
    /// </summary>
    public const double MinRoomToMixSeconds = 60;

    /// <summary>
    /// Plays this record to its end and starts the next one. This is what an ordinary queue does. It is
    /// also what a set does with a record that the listener has scrubbed to the end of.
    ///
    /// <para>The new record starts at its own beginning, not at a cue point. Nothing is mixed, so there
    /// is no intro to skip past, and nothing for one to play under. It is simply the next track.</para>
    /// </summary>
    public static TransitionPlan PlayOut(MixTrack outgoing, MixTrack incoming)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);

        double end = outgoing.Analysis?.AudibleEndSeconds ?? outgoing.DurationSeconds;

        return new TransitionPlan
        {
            Incoming = incoming,
            Style = TransitionStyle.Cut,

            // The last breath of the record, not past it. A fade that starts after the file has ended
            // never starts. The set would then sit on a silent deck, waiting for a point that is not
            // coming.
            StartAtSeconds = Math.Max(0, end - SkipCutSeconds),
            CueInSeconds = 0,
            TempoRatio = 1,
            OutgoingTempoRatio = 1,
            FadeSeconds = SkipCutSeconds,
            ReleaseSeconds = 0,
            MixBpm = incoming.Bpm,
            Trace =
            [
                $"played out to {MixInputs.Sec(end)}, next record follows from the top",
                "no mix: the listener scrubbed into the last minute, so there was no room to mix out of",
            ],
        };
    }

    /// <summary>The next key when there is no bar to roll out on. The old record leaves, and the new
    /// one starts. Nothing is looped and nothing is matched, because there is no grid to do either
    /// against.</summary>
    private static TransitionPlan Cut(
        MixTrack incoming, double startAt, List<string>? trace = null, MixOverrides? overrides = null)
    {
        double cueIn = incoming.Analysis is { } to ? MixInFinder.WhereItStarts(to) : 0;
        if (overrides is { CueInSeconds: { } asked })
        {
            trace?.Add($"asked to cue at {MixInputs.Sec(asked)} instead of {MixInputs.Sec(cueIn)}");
            cueIn = Math.Clamp(asked, 0, Math.Max(0, incoming.DurationSeconds));
        }

        trace?.Add($"cut over {SkipCutSeconds:F2}s: nothing to loop and nothing to match");

        return new TransitionPlan
        {
            Incoming = incoming,
            Style = TransitionStyle.Cut,
            StartAtSeconds = startAt,
            CueInSeconds = cueIn,
            TempoRatio = 1,
            OutgoingTempoRatio = 1,
            FadeSeconds = SkipCutSeconds,
            ReleaseSeconds = 0,
            MixBpm = incoming.Bpm,
            Trace = trace ?? [],
            Overrides = overrides is { IsEmpty: false } ? overrides : null,
            Arc = overrides?.Arc,
        };
    }

    /// <summary>
    /// How many bars the record coming in loops its own first bar for before it plays on. This is half
    /// a phrase. That is long enough for it to be heard as arriving, rather than simply starting. It is
    /// over before the room has time to wonder whether the record is stuck.
    ///
    /// <para>It is also the overlap. The roll-in runs against the last of the roll-out, one coming up
    /// as the other goes down. So the new record is at full, and playing on, at the moment the loop it
    /// arrived over stops. Nothing lands in a hole.</para>
    /// </summary>
    public const int RollInBars = 4;

    /// <summary>
    /// The pull that makes the two grids meet at the END of the changeover as well as at the start of
    /// it. It is worked out from the grids themselves, not from a tempo.
    ///
    /// <para><b>What this fixes.</b> Everything upstream chooses the pull from
    /// <see cref="TrackAnalysis.BpmAt"/>. That is a line fitted through the beats around one point of
    /// each record. It is the tempo AT that point, and it is the right number for that point. A real
    /// grid is not a straight line. <see cref="Analysis.ModelBeatGrid"/> bends it to follow a record
    /// that does not hold one tempo, and a tracker's beats sit where the transients are. So a pull that
    /// is exactly right at the point where it was read is slightly wrong everywhere else. A changeover
    /// spends a long time being slightly wrong. Twenty-four bars at 121 BPM is the better part of a
    /// minute. A thirteen-hundredth of a percent over that is sixty milliseconds, which is two
    /// kicks.</para>
    ///
    /// <para>There is a second problem as well. The tempo of the record coming in is read at its intro
    /// end, which is not where it is cued from. On a record whose grid wanders between those two
    /// points, the pull answers a question about a different part of the record.</para>
    ///
    /// <para><b>What it does instead.</b> The record playing passes a countable number of its own beats
    /// while the changeover runs. The record coming in has to pass the same music in the same time. So
    /// the pull is the distance to its own beat that many beats after its cue point, divided by how
    /// long the changeover lasts. Both ends then land on the sample. What is left in the middle is the
    /// wobble of the two grids against each other. It is not a rate that is wrong for the whole of
    /// it.</para>
    ///
    /// <para>The record playing is left alone. It is already turning, and it has already been ridden
    /// onto this tempo. It is also the record setting the tempo of the room. Moving it here would
    /// change the speed of the record everybody is listening to, to suit one that nobody can hear
    /// yet.</para>
    /// </summary>
    /// <returns>The pull to use. This is <paramref name="ratio"/> unchanged wherever the grids cannot
    /// answer, or where the answer is not one the pull limits allow.</returns>
    private static double SpanLock(
        TrackAnalysis from, double startAt, double outRatio,
        TrackAnalysis to, double cueIn, double ratio, double fade, double inNative,
        MixPolicy policy, List<string> trace)
    {
        if (fade <= 0 || outRatio <= 0 || ratio <= 0)
            return ratio;

        // How many of its own beats the record playing gets through while the two run together, and
        // then the stretch those beats actually cover.
        //
        // Both sides are measured in whole beats, and that is all of the arithmetic. If the outgoing
        // side were measured as a duration instead, the changeover's own length, the span would end
        // wherever the fade happens to end. The other side ends on a beat. The leftover part of a beat
        // is up to half a second inside a span of fifty. That is a one percent error in the pull. It is
        // put there by the very step that is meant to remove that error.
        int beats = BeatsBetween(from, startAt, startAt + fade * outRatio);
        if (beats < 4)
            return ratio;

        if (BeatFrom(from, startAt, beats) is not { } outEnd)
            return ratio;

        double outSpan = outEnd - startAt;
        if (outSpan <= 0)
            return ratio;

        // The same music on the record coming in, counted at ITS octave. A record read an octave from
        // the count passes two of its own beats for every one of the mix's. Asking for the wrong number
        // of them would lock the pair an octave apart, with great precision.
        double octave = to.Bpm > 0 && inNative > 0 ? to.Bpm / inNative : 1;
        int inBeats = (int)Math.Round(beats * octave);
        if (inBeats < 4)
            return ratio;

        if (BeatFrom(to, cueIn, inBeats) is not { } landing || landing <= cueIn)
            return ratio;

        // The two stretches have to take the same time as each other. The record playing covers its own
        // stretch in outSpan / outRatio. The one coming in has to cover its own stretch in the same
        // time. That is one division.
        double want = (landing - cueIn) * outRatio / outSpan;

        // Never outside what the pair were allowed in the first place. A grid that asks for more than
        // the pull limits is a grid saying something the changeover cannot act on. A record bent past
        // the limit to satisfy it would be audibly interfered with.
        double capped = Math.Clamp(want, 1 - policy.MaxTempoPull, 1 + policy.MaxTempoPull);
        if (Math.Abs(capped - want) > 1e-9)
        {
            trace.Add(
                $"grid lock wanted {want:F5} and the pull limit allows {capped:F5}: left at {ratio:F5}");
            return ratio;
        }

        trace.Add(
            $"grid lock: {beats} beats of the record playing against {inBeats} of the one coming in, " +
            $"pull {ratio:F5} to {want:F5}, taking {(ratio - want) / want * fade * 1000:F0} ms of drift " +
            $"out of the changeover");

        return want;
    }

    /// <summary>
    /// How many pieces the pull may be cut into before the changeover is shortened instead. See
    /// <see cref="TempoStep"/>.
    ///
    /// <para>The value is four. Each boundary costs a decoder reopened at the new speed on the cue
    /// thread, and a step in the record's pitch. The point of the schedule is to hold the grids
    /// together, not to chase the last millisecond. On a real pair, one extra piece takes a sixteen-bar
    /// blend from a hundred milliseconds out to nine. The pieces after that are worth about a
    /// millisecond each.</para>
    /// </summary>
    public const int MaxPullPieces = 4;

    /// <summary>
    /// The pull in pieces: the speed the record coming in has to be played at through each stretch of
    /// the changeover, for its beats to land on the other record's beats. It is worked out from the two
    /// grids, not from either record's tempo.
    ///
    /// <para>One piece is <see cref="SpanLock"/>'s answer, the single pull whose two ends meet. More
    /// pieces is the same arithmetic done over each stretch in turn. So the grids meet at every piece
    /// boundary as well. What is left inside a piece is the wobble of a few bars, not the wobble of a
    /// whole changeover. The boundaries are bar lines of the mix.</para>
    ///
    /// <para>All or nothing. A piece that wants a pull the policy would not allow means the grid is
    /// asking for something the changeover cannot do. The whole schedule is then dropped, not applied
    /// in part.</para>
    /// </summary>
    private static IReadOnlyList<TempoStep> Warp(
        TrackAnalysis from, double startAt, double outRatio,
        TrackAnalysis to, double cueIn, double ratio, double fade, double inNative,
        int pieces, MixPolicy policy)
    {
        var one = new[] { new TempoStep(0, cueIn, ratio) };
        if (pieces <= 1 || fade <= 0 || outRatio <= 0)
            return one;

        var outs = from.BeatTimes;
        var ins = to.BeatTimes;
        int i = FirstBeatAt(outs, startAt), j = FirstBeatAt(ins, cueIn);
        if (i < 0 || j < 0)
            return one;

        int beats = BeatsBetween(from, startAt, startAt + fade * outRatio);
        int per = beats / pieces;
        if (per < 4)
            return one;                                  // a piece shorter than a bar is not a piece

        double octave = to.Bpm > 0 && inNative > 0 ? to.Bpm / inNative : 1;
        var steps = new List<TempoStep>(pieces);

        for (int s = 0; s < pieces; s++)
        {
            int a = s * per, z = s == pieces - 1 ? beats : a + per;
            int inA = j + (int)Math.Round(a * octave), inZ = j + (int)Math.Round(z * octave);
            if (i + z >= outs.Count || inZ >= ins.Count)
                return one;

            double heard = (outs[i + z] - outs[i + a]) / outRatio;
            double covers = ins[inZ] - ins[inA];
            if (heard <= 0 || covers <= 0)
                return one;

            double want = covers / heard;
            if (Math.Abs(want - 1) > policy.MaxTempoPull)
                return one;

            steps.Add(new TempoStep((outs[i + a] - startAt) / outRatio, ins[inA], want));
        }

        return steps;
    }

    /// <summary>
    /// How far the two records' grids actually drift apart inside the changeover, in the seconds the
    /// room hears. This is the worst gap at any beat of the record coming in, measured from the beat of
    /// the record playing that it was cued against.
    ///
    /// <para>The comparison is beat for beat, and that is what makes it an honest measure. The record
    /// coming in is cued on a line and held until the record playing reaches one (see Dj.BeginFade). So
    /// the two start at the same instant. The nth beat of one is then the nth beat of the other for the
    /// whole changeover. Any other method, such as nearest beat, or a tempo compared with a tempo,
    /// measures the tracker's jitter as well as the drift. That answers a question nobody asked.</para>
    ///
    /// <para>It is measured at the incoming record's own octave, for <see cref="SpanLock"/>'s reason. A
    /// record read an octave from the count passes two of its own beats for each one of the mix's.
    /// Pairing them one for one would report a changeover running at half speed as one that is falling
    /// apart.</para>
    /// </summary>
    /// <returns>The worst gap in seconds. This is zero where there is not enough grid on both sides to
    /// have an opinion. That is the same silence <see cref="SpanLock"/> keeps, and for the same reason:
    /// a reading that cannot be taken is not a reading of zero.</returns>
    private static double GridDrift(
        TrackAnalysis from, double startAt, double outRatio,
        TrackAnalysis to, double cueIn, IReadOnlyList<TempoStep> steps, double fade, double inNative)
    {
        if (fade <= 0 || outRatio <= 0 || steps.Count == 0)
            return 0;

        var outs = from.BeatTimes;
        var ins = to.BeatTimes;
        int i = FirstBeatAt(outs, startAt), j = FirstBeatAt(ins, cueIn);
        if (i < 0 || j < 0)
            return 0;

        double octave = to.Bpm > 0 && inNative > 0 ? to.Bpm / inNative : 1;
        double until = startAt + fade * outRatio;

        double worst = 0;
        int counted = 0, piece = 0;
        for (int k = 0; i + k < outs.Count && outs[i + k] <= until + 1e-9; k++)
        {
            int landing = j + (int)Math.Round(k * octave);
            if (landing >= ins.Count)
                break;

            double heard = (outs[i + k] - startAt) / outRatio;

            // Which piece of the pull this beat is played under, and where the record coming in has got
            // to by the end of it. Each piece starts where the last one left off. So a beat is timed
            // from the start of its own piece, not from the cue point.
            while (piece + 1 < steps.Count && heard >= steps[piece + 1].AtSeconds - 1e-9)
                piece++;

            var step = steps[piece];
            if (step.Ratio <= 0)
                return 0;

            double arrives = step.AtSeconds + (ins[landing] - step.CueSeconds) / step.Ratio;
            worst = Math.Max(worst, Math.Abs(arrives - heard));
            counted++;
        }

        return counted >= 4 ? worst : 0;
    }

    /// <summary>Where a record's grid first reaches a point, or -1 when it never does.</summary>
    private static int FirstBeatAt(IReadOnlyList<double> times, double from)
    {
        for (int i = 0; i < times.Count; i++)
            if (times[i] >= from - 1e-9)
                return i;

        return -1;
    }

    /// <summary>How many of a record's own beats fall in a stretch of it.</summary>
    private static int BeatsBetween(TrackAnalysis grid, double from, double to)
    {
        int count = 0;
        foreach (double at in grid.BeatTimes)
        {
            if (at < from - 1e-9) continue;
            if (at > to + 1e-9) break;
            count++;
        }

        return Math.Max(0, count - 1);
    }

    /// <summary>Where a record's grid is <paramref name="beats"/> beats after a point, or null when it
    /// runs out of grid before it gets there.</summary>
    private static double? BeatFrom(TrackAnalysis grid, double from, int beats)
    {
        var times = grid.BeatTimes;
        for (int i = 0; i < times.Count; i++)
        {
            if (times[i] < from - 1e-9)
                continue;

            int landing = i + beats;
            return landing < times.Count ? times[landing] : null;
        }

        return null;
    }

    private static TransitionPlan Blend(
        MixTrack incoming, double startAt, double mixBpm,
        List<string>? trace = null, MixOverrides? overrides = null)
    {
        // With no grid on either side there is nothing to hold a long overlap together. A shorter one
        // is honest about that.
        bool anyGrid = incoming.Analysis is { CanBeatMatch: true };
        double fade = anyGrid ? BlendSeconds : CutSeconds * 2;
        double cueIn = MixInPoint(incoming, fade);

        if (overrides is { CueInSeconds: { } asked })
        {
            trace?.Add($"asked to cue at {MixInputs.Sec(asked)} instead of {MixInputs.Sec(cueIn)}");
            cueIn = Math.Clamp(asked, 0, Math.Max(0, incoming.DurationSeconds));
        }

        trace?.Add(
            anyGrid
                ? $"blend over {fade:F1}s: the incoming record has a grid, the pair have no tempo in common"
                : $"cut over {fade:F1}s: no grid on the record coming in");

        return new TransitionPlan
        {
            Incoming = incoming,
            Style = anyGrid ? TransitionStyle.Blend : TransitionStyle.Cut,
            StartAtSeconds = startAt,
            CueInSeconds = incoming.Analysis?.BarAtOrAfter(cueIn) ?? cueIn,
            TempoRatio = 1,
            FadeSeconds = fade,
            ReleaseSeconds = 0,
            MixBpm = mixBpm,
            Trace = trace ?? [],
            Overrides = overrides is { IsEmpty: false } ? overrides : null,
            Arc = overrides?.Arc,
        };
    }

    // ── The added moves ───────────────────────────────────────────────────────
    //
    // Six changeovers taken from how other rooms mix. Each one is a shaping of a changeover that this
    // file already knows how to make. The tempo meet, the drift budget, the halving, the clamp that
    // keeps a fade on the record, and the snap back onto a four are not written again below. Matched
    // re-enters Plan with the style taken off and the length asked for. What comes back is an ordinary
    // beat-matched plan. Every one of those decisions has already been taken and traced. The move then
    // changes the two or three numbers that make it the move it is.
    //
    // So a technique cannot quietly disagree with the planner about what is possible. When the pair
    // cannot hold a layer, what comes back is the blend the planner would have made anyway. That is
    // said out loud in the trace. It is not a thirty-two bar layer that drifts apart in front of the
    // room.

    /// <summary>Whether this is one of the six moves added from other rooms, as opposed to the moves
    /// the app started with.</summary>
    private static bool IsAddedMove(TransitionStyle style) =>
        style is TransitionStyle.Slam or TransitionStyle.BassSwap or TransitionStyle.FilterFade
            or TransitionStyle.DoubleDrop or TransitionStyle.EchoOut or TransitionStyle.Spinback;

    /// <summary>
    /// The technique a DJ would use on this pair, without planning it. This is what the workbench shows
    /// beside a changeover, and what <see cref="MixPolicy.Techniques"/> makes the planner act on.
    /// </summary>
    public static MixTechniques.Choice Suggest(
        MixTrack outgoing, double outgoingBpm, MixTrack incoming, double luck = 0.5)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);

        bool canMatch = outgoing.CanBeatMatch && incoming.CanBeatMatch && outgoingBpm > 0;
        return MixTechniques.Best(outgoing, incoming, outgoingBpm, canMatch, luck);
    }

    /// <summary>One of the six, planned.</summary>
    private static TransitionPlan Shaped(
        TransitionStyle style, MixTrack outgoing, double outgoingBpm, MixTrack incoming,
        double startAt, int wantBars, bool canMatch, MixPolicy policy, MixOverrides? overrides,
        List<string> trace)
    {
        // Three of the six are two records played together, and two records cannot be played together
        // on tempos that will not meet. So this is refused rather than relabelled, the same as an
        // asked-for beat-match.
        if (!canMatch && style is TransitionStyle.BassSwap or TransitionStyle.FilterFade or TransitionStyle.DoubleDrop)
        {
            trace.Add($"{style} needs the two on one tempo, and these two have none: blended instead");
            return Blend(incoming, startAt, outgoingBpm, trace, overrides);
        }

        return style switch
        {
            TransitionStyle.Slam => Slam(outgoing, outgoingBpm, incoming, startAt, overrides, trace),
            TransitionStyle.BassSwap => Layered(outgoing, outgoingBpm, incoming, startAt, wantBars, policy, overrides, trace),
            TransitionStyle.FilterFade => Filtered(outgoing, outgoingBpm, incoming, startAt, wantBars, policy, overrides, trace),
            TransitionStyle.DoubleDrop => Dropped(outgoing, outgoingBpm, incoming, startAt, wantBars, policy, overrides, trace),
            TransitionStyle.EchoOut => Gesture(TransitionStyle.EchoOut, EchoBars, outgoing, outgoingBpm, incoming, startAt, wantBars, canMatch, policy, overrides, trace),
            TransitionStyle.Spinback => Gesture(TransitionStyle.Spinback, SpinBars * 2, outgoing, outgoingBpm, incoming, startAt, wantBars, canMatch, policy, overrides, trace),
            _ => Blend(incoming, startAt, outgoingBpm, trace, overrides),
        };
    }

    /// <summary>
    /// The ordinary beat-matched changeover that a technique is a shaping of. It is the same
    /// arithmetic, asked for at the length and from the point the move wants.
    ///
    /// <para>It re-enters <see cref="Plan"/> with the style taken off and the technique switched off in
    /// the policy. That is what stops the recursion. What comes back is a beat-matched plan when the
    /// pair can hold one. When they cannot, it is the blend the planner would have made. So every
    /// caller has to check what it got back, and cannot assume.</para>
    /// </summary>
    private static TransitionPlan Matched(
        MixTrack outgoing, double outgoingBpm, MixTrack incoming,
        MixOverrides? overrides, MixPolicy policy, List<string> trace,
        int? bars = null, double? startAt = null)
    {
        var inner = (overrides ?? new MixOverrides()) with
        {
            Style = null,
            Policy = policy with { Techniques = false },
            FadeBars = bars ?? overrides?.FadeBars,
            StartAtSeconds = startAt ?? overrides?.StartAtSeconds,
            Arc = null,
        };

        var plan = Plan(outgoing, outgoingBpm, incoming, inner, trace);

        // What a hand actually asked for, put back. The inner call was given a request that the planner
        // wrote for itself. A plan that recorded that as an override would claim that a DJ asked for
        // something nobody asked for.
        return plan with { Overrides = overrides is { IsEmpty: false } ? overrides : null };
    }

    /// <summary>
    /// The cut on the one: the record playing is closed and the new one is opened on the same downbeat.
    ///
    /// <para>Nothing is matched and nothing overlaps. The bar line is the whole of the technique. It is
    /// what separates this from <see cref="TransitionStyle.Cut"/>, which is what the app does when
    /// there is no line to land on. The bar line is already settled by the time this is reached.
    /// <see cref="Plan"/> moves every changeover back to one of the outgoing record's fours before it
    /// asks what kind of changeover it is.</para>
    /// </summary>
    private static TransitionPlan Slam(
        MixTrack outgoing, double outgoingBpm, MixTrack incoming, double startAt,
        MixOverrides? overrides, List<string> trace)
    {
        var to = incoming.Analysis;
        double bpm = outgoingBpm > 0 ? outgoingBpm : outgoing.Bpm;

        // The bar where the record's own instruments come in, taken to the four it belongs to. There is
        // no fade for an intro to sit under here. So cueing before the record starts would open the
        // set's one loud moment onto whatever silence the file begins with.
        double cueIn = to is not null ? to.PhraseAtOrBefore(MixInFinder.WhereItStarts(to)) : 0;
        if (overrides is { CueInSeconds: { } asked })
        {
            trace.Add($"asked to cue at {MixInputs.Sec(asked)} instead of {MixInputs.Sec(cueIn)}");
            cueIn = Math.Clamp(asked, 0, Math.Max(0, incoming.DurationSeconds));
        }

        trace.Add(
            $"cut on the one at {MixInputs.Sec(startAt)}: {SlamSeconds * 1000:F0} ms across, " +
            $"nothing overlapped, new record from {MixInputs.Sec(cueIn)}");

        return new TransitionPlan
        {
            Incoming = incoming,
            Style = TransitionStyle.Slam,
            StartAtSeconds = startAt,
            CueInSeconds = cueIn,

            // Its own speed. A record that is about to be alone in the room has nothing left to agree
            // with. Pulling it onto a tempo that stops existing on the same downbeat would play the
            // record wrong for no reason.
            TempoRatio = 1,
            OutgoingTempoRatio = 1,
            FadeSeconds = SlamSeconds,
            ReleaseSeconds = 0,
            MixBpm = incoming.Bpm > 0 ? incoming.Bpm : bpm,
            BarSeconds = bpm > 0 ? MixBarSeconds(bpm) : 0,
            FadeBars = 0,
            Trace = trace,
            Overrides = overrides is { IsEmpty: false } ? overrides : null,
            Arc = overrides?.Arc,
        };
    }

    /// <summary>
    /// The bass swap: three phrases of the two records layered, with the bottom end changing hands in
    /// one step in the middle.
    ///
    /// <para>The step is the technique. A ramped bass swap is two half-present kicks for the length of
    /// the ramp. That is the thing the move exists to avoid. The guides agree that one bassline plays
    /// at a time, and that the change happens on a line. Here that line is the middle of the
    /// changeover, and it is planned to fall on a phrase line.</para>
    /// </summary>
    private static TransitionPlan Layered(
        MixTrack outgoing, double outgoingBpm, MixTrack incoming, double startAt, int wantBars,
        MixPolicy policy, MixOverrides? overrides, List<string> trace)
    {
        // Three phrases of the outgoing record (see BassSwapPhrases). This is a count of phrases, not a
        // fixed number of bars. The move's arc is written in thirds. Each third has to be one phrase,
        // so the swap lands on a phrase line.
        int perPhrase = outgoing.Analysis?.BarsPerPhrase ?? TrackAnalysis.PhraseBeats / 4;
        int bars = overrides is { FadeBars: > 0 } ? wantBars : BassSwapPhrases * perPhrase;

        var plan = Matched(outgoing, outgoingBpm, incoming, overrides, policy, trace, bars: bars, startAt: startAt);

        if (!MixTechniques.IsLocked(plan.Style))
        {
            trace.Add("no layer: the pair would not stay together for one, so the blend stands");
            return plan;
        }

        // Three phrases of both records at full, so the two have to BE one tempo. They cannot be two
        // tempos near enough to hold for a fade whose ends nobody hears. There are no such ends here.
        // The record coming in is at full from the end of the first phrase, and the old one stays at
        // full until the start of the third. A drift that the ordinary changeover would have budgeted
        // for is a flam right through the middle of this one. See TempoLockTolerance.
        if (plan.TempoMismatch > TempoLockTolerance)
        {
            trace.Add(
                $"no bass swap: {plan.TempoMismatch:P4} apart after both pulls, and three phrases of " +
                $"two records at full needs them locked to one tempo");
            return plan;
        }

        // Every clamp in Plan halves the count. A halved bass swap is not a shorter bass swap. It is
        // three sections that are not phrases, with the bottom end changing hands somewhere inside a
        // bar. The move needs its whole length, or it is not the move. So what stands instead is the
        // ordinary blend that the planner already made.
        if (plan.FadeBars != bars)
        {
            trace.Add(
                $"no bass swap: it needs {bars} bars, {BassSwapPhrases} phrases of {perPhrase}, and " +
                $"this changeover was cut to {plan.FadeBars}");
            return plan;
        }

        trace.Add(
            $"bass swap: {BassSwapPhrases} phrases of {perPhrase} bars, the record coming in over the " +
            $"first with its bottom shut, the bass across the second, the old record away over the third");

        return plan with
        {
            Style = TransitionStyle.BassSwap,
            Arc = overrides?.Arc ?? SwapArc(),
        };
    }

    /// <summary>
    /// The filter fade: the record playing is high-passed out of the mix while the new one arrives
    /// whole underneath it.
    ///
    /// <para>Only the levels are planned here. The sweep itself is ridden a block at a time by the
    /// <see cref="Dj"/>. The reason is the same as for the roll-out's filter. An automation curve that
    /// is not driven by the audio it shapes drifts away from that audio.</para>
    /// </summary>
    private static TransitionPlan Filtered(
        MixTrack outgoing, double outgoingBpm, MixTrack incoming, double startAt, int wantBars,
        MixPolicy policy, MixOverrides? overrides, List<string> trace)
    {
        var plan = Matched(outgoing, outgoingBpm, incoming, overrides, policy, trace, bars: wantBars, startAt: startAt);

        if (!MixTechniques.IsLocked(plan.Style))
        {
            trace.Add("no filter fade: there is no matched changeover to filter, so the blend stands");
            return plan;
        }

        trace.Add(
            $"filter fade: the record playing climbs out over {plan.FadeBars} bars, " +
            $"fader only finishing what the filter started");

        return plan with
        {
            Style = TransitionStyle.FilterFade,
            Arc = overrides?.Arc ?? FilterArc(),
        };
    }

    /// <summary>
    /// The double drop: the two records are lined up so their drops land on the same beat.
    ///
    /// <para>This is the one changeover that is planned backwards. Everywhere else, the cue point is
    /// chosen so that the record coming in has something to play under the one going out. Here it is
    /// chosen so that one measured moment on this record lands on one measured moment on that one. The
    /// rest of the plan follows from that.</para>
    ///
    /// <para>Both drops land halfway through the changeover, not at the end of it. So the two records
    /// go through the moment together, and the old one leaves over the phrase after. That is what a
    /// double drop is. A changeover that finished on the drop would put the drop at the same instant as
    /// the old record disappearing. The room would then hear a handover, not two tunes hitting at
    /// once.</para>
    ///
    /// <para>The incoming cue is NOT taken back to a bar line. It is the one cue point in the app that
    /// is measured from something other than the grid. Snapping it would move the drop off the beat it
    /// was aimed at, and that is the whole point of the move.</para>
    /// </summary>
    private static TransitionPlan Dropped(
        MixTrack outgoing, double outgoingBpm, MixTrack incoming, double startAt, int wantBars,
        MixPolicy policy, MixOverrides? overrides, List<string> trace)
    {
        var from = outgoing.Analysis;
        var to = incoming.Analysis;

        TransitionPlan Instead(string why)
        {
            trace.Add($"no double drop: {why}");
            return Matched(outgoing, outgoingBpm, incoming, overrides, policy, trace, bars: wantBars, startAt: startAt);
        }

        if (to is null || to.DropSeconds.Count == 0)
            return Instead("nothing measured a drop on the record coming in");

        // The record playing needs one of its own drops to swap. Aiming only the record coming in would
        // land its drop on an ordinary bar of the other record. That is a well-timed entrance, and not
        // this move.
        if (from is null || from.DropSeconds.Count == 0)
            return Instead("the record playing has no measured drop to swap");

        double bpm = outgoingBpm > 0 ? outgoingBpm : outgoing.Bpm;
        double barSeconds = bpm > 0 ? MixBarSeconds(bpm) : 0;
        if (barSeconds <= 0)
            return Instead("no tempo to count the switch in");

        // Which of the record's own drops. The phrase mode has already chosen one and hands it in. See
        // DropSwap, which only takes drops that fall on a phrase line. Otherwise the nearest drop is
        // used, one that is within reach of the planned point. Getting to a drop three minutes early
        // throws away half the record.
        double? aimed = policy.PhraseBlend ? startAt : null;
        if (aimed is null)
        {
            double end = outgoing.AudibleEndSeconds;
            foreach (double drop in from.DropSeconds)
            {
                if (drop <= 0 || drop > end)
                    continue;
                if (Math.Abs(drop - startAt) > DoubleDropReachSeconds)
                    continue;
                if (aimed is { } best && Math.Abs(best - startAt) <= Math.Abs(drop - startAt))
                    continue;
                aimed = drop;
            }
        }

        if (aimed is not { } switchAt)
            return Instead(
                $"none of the {from.DropSeconds.Count} drops on the record playing is within " +
                $"{DoubleDropReachSeconds:F0}s of {MixInputs.Sec(startAt)}");

        // The record coming in is cued AT its own drop, not a changeover before it. Nothing of it is
        // heard until the switch. So an earlier cue buys nothing, and it costs everything it skips. A
        // deck rolling silently for eight bars is eight bars of the new record thrown away before
        // anyone hears a note of it.
        double cue = 0;
        foreach (double drop in to.DropSeconds)
            if (drop > 0) { cue = drop; break; }

        if (cue <= 0)
            return Instead("the record coming in has no drop past its first sample");

        if (overrides is { CueInSeconds: { } askedCue })
        {
            trace.Add($"asked to cue at {MixInputs.Sec(askedCue)} instead of the drop at {MixInputs.Sec(cue)}");
            cue = Math.Clamp(askedCue, 0, Math.Max(0, incoming.DurationSeconds));
        }

        // One beat, and that is the whole changeover. There is no overlap in this move. The record
        // playing stops on its drop, and the record coming in starts on its own. The beat is there so
        // the two are joined, not butted together.
        double fade = DoubleDropSwitchBeats * (barSeconds / 4);

        trace.Add(
            $"double drop: the record playing stops on its drop at {MixInputs.Sec(switchAt)} and the " +
            $"record coming in starts on its own at {MixInputs.Sec(cue)}, " +
            $"{DoubleDropSwitchBeats:F0} beat across ({fade * 1000:F0} ms)");

        return new TransitionPlan
        {
            Incoming = incoming,
            Style = TransitionStyle.DoubleDrop,
            StartAtSeconds = switchAt,
            CueInSeconds = cue,

            // Neither record is pulled. They are never both playing, so there is nothing for either to
            // agree with. Bending a record onto a tempo that stops existing on the same beat would play
            // it wrong for no reason at all. The cut on the one works the same way.
            TempoRatio = 1,
            OutgoingTempoRatio = 1,
            TempoMismatch = 0,
            FadeSeconds = fade,
            ReleaseSeconds = 0,
            MixBpm = incoming.Bpm > 0 ? incoming.Bpm : bpm,
            BarSeconds = barSeconds,
            FadeBars = 0,

            // No curve to draw. A changeover one beat wide has no shape that a hand could put a point
            // on, and there is no crossing in it to shape.
            Arc = overrides?.Arc,
            Trace = trace,
            Overrides = overrides is { IsEmpty: false } ? overrides : null,
        };
    }

    /// <summary>How far from where the changeover was going to be a drop may be, and still be the one
    /// it is moved onto. This is ninety seconds. That is far enough to reach the drop a long record is
    /// built around. It is near enough that the set does not sit through another two minutes to get
    /// there.</summary>
    private const double DoubleDropReachSeconds = 90;

    /// <summary>
    /// The two moves that are gestures rather than mixes: the echo out and the spinback.
    ///
    /// <para>They are planned together because they are the same plan. Both leave the record playing on
    /// a bar line rather than fading it. Both put the record coming in on a downbeat, a bar or two
    /// later, with nothing over it. Neither needs the two tempos to meet. That is most of why a DJ
    /// reaches for them. What differs between them happens in the booth. One is an echo thrown after
    /// the fader. The other is a bar of record pulled backwards.</para>
    /// </summary>
    private static TransitionPlan Gesture(
        TransitionStyle style, int defaultBars,
        MixTrack outgoing, double outgoingBpm, MixTrack incoming, double startAt, int wantBars,
        bool canMatch, MixPolicy policy, MixOverrides? overrides, List<string> trace)
    {
        var to = incoming.Analysis;
        int bars = overrides is { FadeBars: > 0 } ? Math.Max(1, wantBars) : defaultBars;

        // The bar where the new record's own instruments come in, on one of its fours. It lands dry and
        // alone, so there is no intro here to be played under anything.
        double cueIn = to is not null ? to.PhraseAtOrBefore(MixInFinder.WhereItStarts(to)) : 0;
        bool byHand = overrides is { CueInSeconds: not null };

        if (canMatch)
        {
            // Matched wherever the two can be. Nothing is laid over anything, so the match buys one
            // thing only. The new record arrives already counting at the tempo the room was. Over two
            // bars, the drift budget allows almost any pair.
            var plan = Matched(outgoing, outgoingBpm, incoming, overrides, policy, trace, bars: bars, startAt: startAt);
            if (MixTechniques.IsLocked(plan.Style))
            {
                trace.Add($"{style}: {plan.FadeBars} bars of {plan.BarSeconds:F2}s, new record from {MixInputs.Sec(byHand ? plan.CueInSeconds : cueIn)}");
                return plan with
                {
                    Style = style,
                    CueInSeconds = byHand ? plan.CueInSeconds : cueIn,

                    // A gesture has its own shape and no fader curve to draw over it, in the same way
                    // that a roll-out has none.
                    Arc = null,
                };
            }

            trace.Add($"{style} unmatched after all: the two grids would not hold even {bars} bars together");
        }

        // No tempo in common. This is the case the guides recommend both of these moves for: a big
        // tempo change with nothing to fade over. The record playing keeps its own speed and is left on
        // its own bar line. The new record starts after it, also at its own speed.
        double bpm = outgoingBpm > 0 ? outgoingBpm : outgoing.Bpm;
        double barSeconds = bpm > 0 ? MixBarSeconds(bpm) : BlendSeconds / 2;

        if (byHand)
            cueIn = Math.Clamp(overrides!.CueInSeconds!.Value, 0, Math.Max(0, incoming.DurationSeconds));

        trace.Add(
            $"{style} on its own tempo: {bars} bars of {barSeconds:F2}s at {bpm:F1}, " +
            $"new record from {MixInputs.Sec(cueIn)} at its own {incoming.Bpm:F1}");

        return new TransitionPlan
        {
            Incoming = incoming,
            Style = style,
            StartAtSeconds = startAt,
            CueInSeconds = cueIn,
            TempoRatio = 1,
            OutgoingTempoRatio = 1,
            FadeSeconds = bars * barSeconds,
            ReleaseSeconds = 0,
            MixBpm = incoming.Bpm > 0 ? incoming.Bpm : bpm,
            BarSeconds = barSeconds,
            FadeBars = bars,
            Trace = trace,
            Overrides = overrides is { IsEmpty: false } ? overrides : null,
        };
    }

    /// <summary>
    /// How many phrases a bass swap takes, and what happens in each. There are three, and the
    /// choreography is the whole of the move:
    ///
    /// <list type="number">
    /// <item>the record coming in is brought up to full with its bottom end shut off, so what arrives
    /// is its top and its middle over the old record's whole arrangement;</item>
    /// <item>the bass changes hands across the phrase, rising on the new record and falling on the old
    /// one, so that on the phrase line at the end of it the old record has no bottom left;</item>
    /// <item>and the old record, now the thinner of the two, is faded away above it.</item>
    /// </list>
    ///
    /// <para>Three phrases, and not a length in bars. The shape only works if each part is a phrase.
    /// The room counts the swap arriving on a phrase line. A swap that lands two bars into a phrase
    /// sounds like a mistake, not a move.</para>
    /// </summary>
    public const int BassSwapPhrases = 3;

    /// <summary>
    /// How much bottom end a record has to carry, where the mix meets it, before the blend cannot
    /// simply lay another record over it.
    ///
    /// <para>Measured across this library: over 326 read tracks, the median bass reading is 0.80 on
    /// <see cref="EnergyProfile.Rescale"/>'s scale. A quarter of them are under 0.61, and a quarter are
    /// over 0.87. The bar is that median. So it is roughly the heavier half of records, and roughly a
    /// quarter of PAIRS. That is often enough to be a changeover the set actually makes. It is rare
    /// enough that it stays the answer to a question, and does not become the house style.</para>
    ///
    /// <para>The level is used, not the bass's share of the whole. The share was measured too, and it
    /// separates nothing. Across the entire library the share runs from 0.59 to 0.79 between the first
    /// and third quartiles, because nearly every record is bass-led. How MUCH bottom end there is
    /// varies about four to one over the same tracks. That is what decides whether two of them will
    /// fit.</para>
    /// </summary>
    public const double BassHeavy = 0.80;

    /// <summary>
    /// How far the room has to be going UP for a drop swap to be the right idea, on
    /// <see cref="TrackAnalysis.Intensity"/>'s scale.
    ///
    /// <para>The value is one twentieth. The move takes the record playing off on the biggest moment it
    /// has left, and puts another record's biggest moment in its place. That only sounds like a lift if
    /// the record arriving is harder. Swapping a drop for a smaller one throws away the better of the
    /// two. The margin is small, because "higher" is the whole test. The margin only exists so that two
    /// records that measure the same do not flip on noise.</para>
    /// </summary>
    public const double DropSwapLift = 0.05;

    /// <summary>How far from a phrase line a drop may sit and still count as landing on one. This is
    /// half a bar. The drop reading is already snapped to a bar line, so this checks whether that bar
    /// is a phrase line. It does not measure anything else.</summary>
    private const double DropOnPhraseBars = 0.5;

    /// <summary>
    /// Where a drop swap would start, or null when this pair has no such changeover.
    ///
    /// <para>Three things have to hold. All three are about the records, not about the arithmetic. The
    /// record playing has a drop that falls on one of its own phrase lines. There is a whole
    /// changeover's room in front of that drop. And the record coming in is the harder of the two (see
    /// <see cref="DropSwapLift"/>). The record coming in also has to have a drop of its own to put
    /// there. <see cref="Dropped"/> checks that, and refuses if it is missing.</para>
    ///
    /// <para>The drop has to be on a phrase line, not only on a bar. The swap is the end of a phrase.
    /// The room has been counting towards it since the changeover began. A drop that lands three bars
    /// into a phrase arrives while everybody is still counting.</para>
    /// </summary>
    private static double? DropSwap(
        MixTrack outgoing, double outgoingBpm, MixTrack incoming, MixPolicy policy,
        double startAt, List<string> trace)
    {
        var from = outgoing.Analysis;
        var to = incoming.Analysis;
        if (from is null || to is null || from.DropSeconds.Count == 0 || to.DropSeconds.Count == 0)
            return null;

        // Only where the room is going up. This is judged where the mix meets each record, like
        // everything else here. A changeover meets an outro and an intro. It does not meet two
        // averages.
        double leaving = outgoing.IntensityAt(MixOutPoint(outgoing, policy));
        double arriving = incoming.IntensityAt(MixInPoint(incoming));
        if (arriving < leaving + DropSwapLift)
            return null;

        double bar = from.Bpm > 0 && outgoingBpm > 0
            ? MixBarSeconds(Meet(from.Bpm, to.Bpm, outgoingBpm, policy).OutReached)
            : 0;
        if (bar <= 0)
            return null;

        double fade = policy.OutroFadeBars * bar;
        double slack = DropOnPhraseBars * bar;
        double end = outgoing.AudibleEndSeconds;

        foreach (double drop in from.DropSeconds)
        {
            if (drop - fade < 0 || drop > end)
                continue;

            // The drop has to BE a phrase line, not merely be near one.
            if (Math.Abs(drop - from.MeasuredPhraseAtOrBefore(drop)) > slack)
                continue;

            trace.Add(
                $"drop swap: the record coming in is harder ({arriving:F2} against {leaving:F2}) and " +
                $"the drop at {MixInputs.Sec(drop)} is on a phrase line, so the records change there");
            return drop;
        }

        return null;
    }

    /// <summary>Whether both records carry enough bass to be laid over each other whole, which would
    /// give two basslines rather than one. The result is traced either way. It is the whole reason a
    /// changeover came back as a bass swap and not as a blend.</summary>
    private static bool BothHeavyDownLow(
        TrackAnalysis from, TrackAnalysis to, double startAt, MixTrack incoming, List<string> trace)
    {
        if (from.BassCurve.Count == 0 || to.BassCurve.Count == 0)
            return false;                       // read before the bands were measured

        double leaving = TrackShape.LevelAt(from.BassCurve, startAt);
        double arriving = TrackShape.LevelAt(to.BassCurve, MixInPoint(incoming));

        bool both = leaving >= BassHeavy && arriving >= BassHeavy;
        trace.Add(
            both
                ? $"both heavy down low ({leaving:F2} and {arriving:F2} against {BassHeavy:F2}): a bass swap rather than a blend"
                : $"not both heavy down low ({leaving:F2} and {arriving:F2} against {BassHeavy:F2}): the blend stands");

        return both;
    }

    /// <summary>
    /// The bass swap, on the phrase: one phrase to arrive over, one to hand the bottom end across, and
    /// one to leave in. It is written in thirds, because each third is one of those phrases.
    ///
    /// <para>The bass ramps across the whole of the middle phrase rather than stepping. A step is the
    /// right answer when the swap is a moment and not a stretch. A double drop hands the bottom end
    /// over on one beat, because that is how wide the whole move is. A step is the wrong answer here.
    /// The swap IS the middle phrase, and it has a whole phrase to happen in. What the room hears is
    /// one bassline the whole way through, changing which record it belongs to.</para>
    /// </summary>
    private static FadeArc SwapArc() => new()
    {
        // Up over the first phrase, and full from there. What makes this the bass swap, and not a long
        // blend, is that the new record is WHOLE and merely bottomless while the two run together.
        Incoming =
        [
            new ArcPoint(0, 0),
            new ArcPoint(1 / 3.0, 1),
            new ArcPoint(1, 1),
        ],

        // At full for two phrases, then away over the third. It keeps its level right through the swap.
        // The bottom end leaving is what thins it. Taking the fader down at the same time would be two
        // hands doing one job.
        Outgoing =
        [
            new ArcPoint(0, 1),
            new ArcPoint(2 / 3.0, 1),
            new ArcPoint(1, 0),
        ],

        BassSwapStart = 1 / 3.0,
        BassSwapEnd = 2 / 3.0,
    };

    /// <summary>How far under full a layered record sits until it takes the bottom end. The value is
    /// 0.95, which is under a decibel. That is inaudible as a level. It is enough for the two curves to
    /// cross where the swap is, and not where the fader ride ends.</summary>
    private const double LayerUnder = 0.95;

    /// <summary>
    /// The levels of a filter fade. The old record holds its level while the sweep climbs through it.
    /// The filter is what takes it away. The fader only takes away the last of the top end.
    /// </summary>
    private static FadeArc FilterArc() => new()
    {
        Incoming =
        [
            new ArcPoint(0, 0),
            new ArcPoint(0.25, LayerUnder),
            new ArcPoint(0.5, LayerUnder),
            new ArcPoint(0.55, 1),
            new ArcPoint(1, 1),
        ],
        Outgoing =
        [
            new ArcPoint(0, 1),
            new ArcPoint(0.4, 1),
            new ArcPoint(0.8, 0.45),
            new ArcPoint(1, 0),
        ],

        // Earlier than the middle third of the classic swap, and a ramp rather than a step. The sweep
        // is already taking the bottom off this record from the first bar. A shelf stepping down under
        // a filter that has already done half the job is a step nobody can hear anyway.
        BassSwapStart = 0.15,
        BassSwapEnd = 0.4,
    };

    /// <summary>
    /// The levels of a double drop. Both records are whole into the moment, the bottom end changing
    /// hands on the drop itself, and the old record leaving over the phrase after it.
    /// </summary>
    /// <summary>
    /// The drop swap's levels: the record playing is taken off across the beat or two in front of the
    /// drop, so the drop the room gets on that phrase line is the new record's and not its own.
    ///
    /// <para>A cut and not a fade. What the moment is worth depends on nothing else being in it. The
    /// bottom end goes with it. Two basslines through a drop is the one thing that every guide to this
    /// move warns against.</para>
    ///
    /// <para>The new record is up and whole well before. It has to be there when its own drop arrives.
    /// Until then it plays under the old record's last phrase, which is what its own build is
    /// for.</para>
    /// </summary>
    /// <summary>
    /// The earliest second a changeover out of this record could begin. This is where a set has to
    /// start watching for it. It is NOT the same question as <see cref="MixOutPoint"/>.
    ///
    /// <para><b>Two questions.</b> A beat-matched blend STARTS at the mix-out point, so one reading can
    /// answer both questions. The phrase changeovers finish there instead. A phrase blend is laid out
    /// backwards from the outro, so both records are in their bodies for the whole of it.
    /// <see cref="MixOutFinder"/> may move it back by up to eight more phrases, looking for somewhere
    /// nobody is singing. At club tempo that is two and a half minutes between where the changeover
    /// begins and where the set was looking.</para>
    ///
    /// <para><b>Why it matters.</b> A set that waits for the outro can let the needle pass the mark the
    /// workbench drew, while the other deck is still empty. A start that has already gone cannot be
    /// waited for. The mix would then land on the next downbeat instead: no lead-in, and nowhere near
    /// its mark.</para>
    ///
    /// <para>Erring early on purpose. Arming sooner costs a decoder held open a little longer, and
    /// nothing else. <see cref="Dj"/> still holds the fade to the bar line the plan named. Arming late
    /// cannot be recovered from at all.</para>
    /// </summary>
    public static double ArmFrom(MixTrack playing, MixOverrides? asked)
    {
        ArgumentNullException.ThrowIfNull(playing);

        var policy = asked?.Policy ?? MixPolicy.Default;
        double outAt = MixOutPoint(playing, asked?.Policy);

        // The same question Matched asks itself. See the wantsPhrase switch there. A named style
        // settles it either way. Otherwise the policy decides.
        bool phrase = asked?.Style switch
        {
            TransitionStyle.PhraseBlend or TransitionStyle.BassSwap or TransitionStyle.DoubleDrop => true,
            TransitionStyle.BeatMatched => false,
            _ => policy.PhraseBlend,
        };

        if (!phrase || playing.Analysis is not { Phrases.IsKnown: true } grid)
            return outAt;

        double phraseSeconds = grid.PhraseSeconds;
        if (phraseSeconds <= 0)
            return outAt;

        // However long the changeover itself will run. The longest of the phrase moves is used where
        // nobody has said, because being early is free and being late is not. A mode that cannot make
        // the bass swap only has the blend's own length to be ready for.
        int phrases = policy.PhraseMoves ? Math.Max(policy.BlendPhrases, BassSwapPhrases) : policy.BlendPhrases;
        double fade = asked is { FadeBars: > 0 } counted && grid.BarSeconds > 0
            ? counted.FadeBars.Value * grid.BarSeconds
            : phrases * phraseSeconds;

        return Math.Max(0, outAt - fade - MixOutFinder.PhrasesBack * phraseSeconds);
    }

    /// <summary>
    /// Where a record starts being mixed out of: its outro, or a sensible tail when the analyser found
    /// none. On a long record, it is wherever the record can be left before then.
    ///
    /// <para>A record over <see cref="MixOutFinder.LongRecordSeconds"/> is not played to its outro if
    /// it has a better place to leave. Seven minutes of one track is a set standing still. The record
    /// itself says where to leave. That is a stretch with the singing stopped and the drums carrying
    /// on. See <see cref="MixOutFinder"/>. A record that has no such stretch is played out as
    /// before.</para>
    /// </summary>
    public static double MixOutPoint(MixTrack track, MixPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        policy ??= MixPolicy.Default;

        // How long the record PLAYS, not how long its file is. A file can carry minutes of dead air
        // after the music. Examples are a rip padded to a side's length, or a track with a long silent
        // tail. The grid is carried to the end of the file on purpose, so the silence has bar lines in
        // it, and it looks exactly like more record. A changeover measured from the file would be
        // planned into that silence, and the set would stand there waiting for a record that had
        // finished.
        double duration = track.AudibleEndSeconds;
        if (duration <= 0)
            return 0;

        // This is asked for on every block while a record plays on its own. The DJ uses it to watch for
        // the outro. Underneath it sits MixOutFinder, a sliding scan of the whole vocal curve, with a
        // sorted copy taken on each call. Twenty calls a second is real work on the one thread with a
        // deadline. The answer depends only on the reading, the record's length and the policy in
        // force. So it is kept against the reading, and worked out again only when an edit brings a new
        // reading, or when a knob is turned.
        var analysis = track.Analysis;
        if (analysis is null)
            return Landing(null, duration, policy);

        if (_mixOut.TryGetValue(analysis, out var memo) &&
            Math.Abs(memo.Duration - duration) < 1e-9 && memo.Policy.Equals(policy))
            return memo.At;

        double at = Landing(analysis, duration, policy);
        _mixOut.AddOrUpdate(analysis, new MixOutMemo(duration, policy, at));
        return at;
    }

    /// <summary>The arithmetic of <see cref="MixOutPoint"/>, uncached.</summary>
    private static double Landing(TrackAnalysis? analysis, double duration, MixPolicy policy)
    {
        double outro = analysis?.OutroStartSeconds ?? 0;
        if (outro <= 0 || outro >= duration)
            outro = duration - MinTailSeconds;

        // Never earlier than half way, and never in the last second. An outro reading that came back
        // wrong must not cut a record in half.
        outro = Math.Clamp(outro, Math.Min(duration * 0.5, duration - 1), Math.Max(0, duration - 1));

        // The early way out is not held to that floor. Going early is the whole point of it. It is not
        // a guess about where the record ends. It is a measurement of where the record stops singing.
        double at = outro;
        if (analysis is not null &&
            MixOutFinder.Find(analysis, FadeRoom(analysis, policy)) is { } early && early < outro)
            at = early;

        // An extended record hands over around the three minute mark, whatever its outro says. The way
        // out the record offered is kept when it comes first, because it is a better place to leave
        // than an arbitrary three minutes. The ceiling only applies to records that would otherwise run
        // to seven or eight minutes.
        if (duration > policy.ExtendedRecordSeconds && at > policy.ExtendedMixOutSeconds)
            at = policy.ExtendedMixOutSeconds;

        return at;
    }

    /// <summary>One record's mix-out point, remembered against the reading it was measured from. The
    /// memory is weak, so a reading that has left every crate takes its memo with it.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TrackAnalysis, MixOutMemo> _mixOut = new();

    private sealed record MixOutMemo(double Duration, MixPolicy Policy, double At);

    /// <summary>Roughly how long a changeover out of this record will run. An early way out has to
    /// leave room for that. The figure is near enough to choose a stretch with. The length actually
    /// used is settled by <see cref="Plan"/>, once the record coming in is known too.</summary>
    private static double FadeRoom(TrackAnalysis analysis, MixPolicy policy) =>
        analysis.Bpm > 0
            ? policy.OutroFadeBars * MixBarSeconds(analysis.Bpm)
            : BlendSeconds;

    /// <summary>
    /// Where the needle goes down on a record coming in: far enough before its drop that the drop lands
    /// as the changeover finishes.
    ///
    /// <para>The intro is not something to skip past. It is what the record is mixed in ON. It is
    /// written to be sparse, so that it can sit under the record it follows. A DJ who starts the new
    /// record at its drop has nothing to fade over. Cueing <paramref name="fadeSeconds"/> earlier puts
    /// the intro under the outgoing record for exactly as long as the two run together. So the old one
    /// leaves and the new one drops on the same bar line. That is what makes a changeover sound like a
    /// mix rather than like two records overlapping. When the intro is the length of the fade, both
    /// records come in on their own first bar.</para>
    ///
    /// <para>With no fade to cover it, for example when a record is restarted, this is the drop itself.
    /// There is nothing for an intro to play under.</para>
    /// </summary>
    public static double MixInPoint(MixTrack track, double fadeSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(track);
        double intro = track.Analysis?.IntroEndSeconds ?? 0;
        double cue = Math.Max(0, intro - Math.Max(0, fadeSeconds));
        double duration = track.DurationSeconds;
        if (duration <= 0)
            return cue;
        return Math.Clamp(cue, 0, Math.Max(0, duration * 0.4));
    }
}
