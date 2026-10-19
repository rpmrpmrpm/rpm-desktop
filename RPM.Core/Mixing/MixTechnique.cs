namespace RPM.Core.Mixing;

/// <summary>
/// The kind of record a technique was learned on.
///
/// <para>This is not a tag on a file. Nothing in the library says what a record is. Naming genres here
/// does not classify anything. A changeover is a convention, not the result of arithmetic, and the
/// conventions differ a lot between rooms. Sixteen bars of two records layered on top of each other is
/// the normal move in progressive house. In drum and bass the same two records get eight bars and a
/// decisive cut. A catalogue that does not say which room a move came from will recommend the wrong
/// move with confidence.</para>
/// </summary>
public enum MixGenre
{
    House,
    TechHouse,
    Techno,
    ProgressiveHouse,
    Trance,
    MelodicHouse,
    DrumAndBass,
    Dubstep,
    UkGarage,
    HipHop,
    Disco,
    Edm,
    AfroHouse,
    Amapiano,
    OpenFormat,
}

/// <summary>What a technique needs to have been measured before it can be attempted at all.</summary>
[Flags]
public enum MixNeeds
{
    /// <summary>Nothing. A record with no reading at all can still be cut into.</summary>
    Nothing = 0,

    /// <summary>A grid on the record going out, so the move can be timed to a bar line.</summary>
    OutgoingGrid = 1,

    /// <summary>A grid on the record coming in, so the two can be laid over each other.</summary>
    IncomingGrid = 2,

    /// <summary>Both tempos close enough to meet inside <see cref="MixPolicy.MaxTempoPull"/>.</summary>
    OneTempo = 4,

    /// <summary>Where the beat comes back after a breakdown, on the record coming in.</summary>
    IncomingDrop = 8,

    /// <summary>The three band curves, so the bottom end can be handed over knowingly.</summary>
    Bands = 16,

    /// <summary>Both records steady enough to be held on a bent tempo for a long changeover. See
    /// <see cref="MixTrack.IsSteadyEnoughToHold"/>.</summary>
    Steady = 32,
}

/// <summary>
/// One way of getting from one record to the next, as a DJ would describe it: what the hands do, how
/// long it takes, the rooms it belongs to, and what has to have been measured for it to be possible.
/// </summary>
/// <param name="Style">The plan this technique comes out as. See <see cref="TransitionStyle"/>.</param>
/// <param name="Name">What a DJ calls it.</param>
/// <param name="Move">What actually happens, in one sentence.</param>
/// <param name="Bars">How long it runs, in bars of the mix. Zero for the moves that happen on a single
/// beat.</param>
/// <param name="FromBpm">The bottom of the tempo range the move is normally heard in.</param>
/// <param name="ToBpm">The top of it.</param>
/// <param name="Genres">The rooms it came from, most typical first.</param>
/// <param name="Needs">What has to have been read off both records first.</param>
/// <param name="Source">Where the description was read from. A claim about a room can then be checked
/// against someone who plays in that room, not only against this file.</param>
public sealed record MixTechnique(
    TransitionStyle Style,
    string Name,
    string Move,
    int Bars,
    double FromBpm,
    double ToBpm,
    IReadOnlyList<MixGenre> Genres,
    MixNeeds Needs,
    string Source)
{
    /// <summary>True when this move belongs to no tempo in particular. The cut, the fade, the cut on
    /// the one and the echo out are made at every tempo. Their point is often that the two records have
    /// no tempo in common at all.</summary>
    public bool AnyTempo => FromBpm <= 0 && ToBpm <= 0;

    /// <summary>True when this move is normally heard at <paramref name="bpm"/>. The band is generous,
    /// because the tempo of a record is a hint about its room, not a fact about it.</summary>
    public bool SuitsTempo(double bpm) =>
        AnyTempo || bpm <= 0 || (bpm >= FromBpm - 4 && bpm <= ToBpm + 4);

    public override string ToString() =>
        $"{Name} ({Bars} bars, {(AnyTempo ? "any tempo" : $"{FromBpm:F0}-{ToBpm:F0} BPM")})";
}

/// <summary>
/// Every changeover the app knows how to make, next to the room it was learned in.
///
/// <para>The moves the app started with are in this list too. They are described in the same terms as
/// the newer ones. That is deliberate. The value of a catalogue is that a move can be compared with its
/// alternatives. A list of only the new moves would say nothing about when the plain sixteen-bar blend
/// is still the right answer. It is the right answer most of the time, in most rooms.</para>
///
/// <para>Nothing here decides anything by itself. <see cref="TransitionPlanner"/> makes the plan and
/// <see cref="Dj"/> plays it. This class says which plan a person would have asked for and why.
/// <see cref="Best"/> is that judgement written down, so it can be disagreed with. The planner consults
/// it only when <see cref="MixPolicy.Techniques"/> is turned on. See
/// <see cref="TransitionPlanner.Suggest"/>. A set that never asked for any of this plays exactly as
/// before.</para>
///
/// <para>Each entry names its sources. The starting point was Pioneer DJ's survey of how each genre is
/// mixed (blog.pioneerdj.com, "We uncover the mixing techniques behind every major genre"). The
/// mechanics of the individual moves come from Native Instruments' and Digital DJ Pool's transition
/// guides, Serato's note on post-fader echo, and DJ TechTools' history of the rewind.</para>
/// </summary>
public static class MixTechniques
{
    /// <summary>The whole catalogue, in the order a DJ would learn them.</summary>
    public static IReadOnlyList<MixTechnique> All { get; } =
    [
        new(TransitionStyle.BeatMatched, "Beat-matched blend",
            "Both records on one tempo, dropped together on a bar line, and the fader crossed over sixteen bars with the bottom end changing hands in the middle of it.",
            TransitionPlanner.OutroFadeBars, 118, 145,
            [MixGenre.House, MixGenre.Techno, MixGenre.ProgressiveHouse, MixGenre.Trance, MixGenre.MelodicHouse, MixGenre.Edm],
            MixNeeds.OutgoingGrid | MixNeeds.IncomingGrid | MixNeeds.OneTempo,
            "Pioneer DJ, house and trance; Digital DJ Pool, 'single phrase beatmatch', 16-32 bars."),

        new(TransitionStyle.PhraseBlend, "Phrase blend",
            "Both records on one tempo, started on the outgoing record's own phrase line and run for two whole phrases, with neither the intro nor the outro mixed over.",
            TransitionPlanner.OutroFadeBars * 3, 118, 145,
            [MixGenre.House, MixGenre.Techno, MixGenre.ProgressiveHouse, MixGenre.Trance, MixGenre.MelodicHouse],
            MixNeeds.OutgoingGrid | MixNeeds.IncomingGrid | MixNeeds.OneTempo | MixNeeds.Steady,
            "Digital DJ Pool, 'phrase mixing': line the changeover up with the 8- or 16-bar phrase rather than with the outro."),

        new(TransitionStyle.Blend, "Fade",
            "The two records simply cross over. No tempo is met, so nothing is held together beyond the first bar line.",
            0, 0, 0,
            [MixGenre.Disco, MixGenre.OpenFormat, MixGenre.HipHop],
            MixNeeds.Nothing,
            "Native Instruments, 'the fade': no beatmatching, faders only."),

        new(TransitionStyle.Cut, "Cut",
            "The old record's tail plays out under a second of the new one. For a record with nothing to hold a longer move.",
            0, 0, 0,
            [MixGenre.HipHop, MixGenre.OpenFormat, MixGenre.Disco],
            MixNeeds.Nothing,
            "Pioneer DJ, hip-hop: sharp contrasts, mix quickly and often."),

        new(TransitionStyle.RollOut, "Loop roll out",
            "The last bar goes round and round for a phrase, tightening to a beat and then half a beat, under a closing filter, and the new record rises through the last of it.",
            TransitionPlanner.RollOutBars, 120, 150,
            [MixGenre.TechHouse, MixGenre.Techno, MixGenre.Edm, MixGenre.Amapiano],
            MixNeeds.OutgoingGrid,
            "Pioneer DJ, tech house: 'quick cuts at phrase changes, echo-outs, stutter and loop rolls'."),

        new(TransitionStyle.Slam, "Cut on the one",
            "The old record is closed and the new one opened on the same downbeat. One beat wide, no overlap, nothing under it.",
            0, 0, 0,
            [MixGenre.HipHop, MixGenre.TechHouse, MixGenre.OpenFormat, MixGenre.UkGarage],
            MixNeeds.OutgoingGrid,
            "Digital DJ Pool, 'the cut on the one': count the final phrase, close A and open B on the next one, no effects."),

        new(TransitionStyle.BassSwap, "Bass swap",
            "Three phrases: the new record arrives over the first with its bottom end shut off, the bass changes hands across the second, and the old record is faded away above it over the third.",
            TransitionPlanner.BassSwapPhrases * 8, 118, 136,
            [MixGenre.ProgressiveHouse, MixGenre.MelodicHouse, MixGenre.House, MixGenre.Trance, MixGenre.AfroHouse, MixGenre.TechHouse],
            MixNeeds.OutgoingGrid | MixNeeds.IncomingGrid | MixNeeds.OneTempo | MixNeeds.Bands | MixNeeds.Steady,
            "Native Instruments and Digital DJ Pool, 'the bass swap' over 8-16 bars, one bassline at a time; Pioneer DJ, progressive house: layers held for several phrases."),

        new(TransitionStyle.FilterFade, "Filter fade",
            "The old record is high-passed away under the new one: its bottom, then its body, climbs out of the mix while the new record arrives whole.",
            TransitionPlanner.OutroFadeBars, 120, 140,
            [MixGenre.House, MixGenre.Techno, MixGenre.TechHouse, MixGenre.MelodicHouse],
            MixNeeds.OutgoingGrid | MixNeeds.IncomingGrid | MixNeeds.OneTempo,
            "Native Instruments, 'the filter fade': high-pass the outgoing deck for two busy records at one tempo; Pioneer DJ, melodic house: smooth filter rides."),

        new(TransitionStyle.DoubleDrop, "Double drop",
            "The two records are lined up so their drops land on the same beat. The old one is cut across the two beats in front of it, so what the room gets on that line is the new record's drop on a clear floor.",
            TransitionPlanner.OutroFadeBars, 138, 180,
            [MixGenre.DrumAndBass, MixGenre.Dubstep, MixGenre.Edm, MixGenre.Trance],
            MixNeeds.OutgoingGrid | MixNeeds.IncomingGrid | MixNeeds.OneTempo | MixNeeds.IncomingDrop | MixNeeds.Steady,
            "Pioneer DJ, drum and bass: 'the double drop, aligning two tunes for simultaneous drops'; Beatmatch Guru on counting the phrase back from each drop."),

        new(TransitionStyle.EchoOut, "Echo out",
            "On the bar line the old record is thrown into a beat-synced echo and its fader shut; the tail rings out alone for a bar and the new record lands dry on the next one.",
            TransitionPlanner.EchoBars, 0, 0,
            [MixGenre.AfroHouse, MixGenre.HipHop, MixGenre.DrumAndBass, MixGenre.UkGarage, MixGenre.OpenFormat, MixGenre.Amapiano],
            MixNeeds.OutgoingGrid,
            "Pioneer DJ, Afro house: 'echo, fade, return'; Digital DJ Pool, 'the echo out', for big tempo changes and records with no clean outro; Serato: keep the echo post-fader so the tail survives the fader."),

        new(TransitionStyle.Spinback, "Spinback",
            "The phrase is played to its end and then ripped backwards, slowing as it goes, and the new record is slammed in on the next one.",
            TransitionPlanner.SpinBars * 2, 128, 180,
            [MixGenre.DrumAndBass, MixGenre.UkGarage, MixGenre.Dubstep, MixGenre.HipHop],
            MixNeeds.OutgoingGrid,
            "Pioneer DJ, drum and bass and garage: 'spinbacks and wheelups, perennial'; DJ TechTools, 'Reload! A history of the rewind'; Digital DJ Pool, 'the spinback/brake' — use sparingly."),
    ];

    /// <summary>What is known about one style.</summary>
    public static MixTechnique For(TransitionStyle style) =>
        All.FirstOrDefault(t => t.Style == style) ?? All[0];

    /// <summary>
    /// Whether this changeover's levels are a curve a hand can draw. See <see cref="FadeArc"/>.
    ///
    /// <para>This is false for the three that have a choreography instead of a fade. They are the
    /// roll-out's loop under a closing filter, the echo's channel shut on the bar line, and the rewind,
    /// where the record going out is at full until a hand stops it. A drawn curve over any of those
    /// would be two hands on one fader. So the workbench does not offer one.</para>
    /// </summary>
    public static bool FollowsAnArc(TransitionStyle style) =>
        style is not (TransitionStyle.RollOut or TransitionStyle.EchoOut or TransitionStyle.Spinback);

    /// <summary>
    /// Whether this changeover holds the two records on one tempo. Every move built on a blend has to
    /// check this before it can be built.
    ///
    /// <para>There are two styles, not one, since the phrase blend was named apart from the
    /// beat-matched blend. They are the same lock laid out against different things. So code that asks
    /// whether the pair really met must check both. Code that asks which changeover this is should
    /// check one.</para>
    /// </summary>
    public static bool IsLocked(TransitionStyle style) =>
        style is TransitionStyle.BeatMatched or TransitionStyle.PhraseBlend;

    /// <summary>
    /// Whether this changeover has a length counted in bars, so that asking for a number of bars means
    /// something.
    ///
    /// <para>This is false for the cut, the blend and the cut on the one. Their length is a fixed
    /// number of seconds, not a count of the record. The cut and the blend are what the app does when
    /// there is no grid worth counting on. The cut on the one is thirty milliseconds wide by
    /// definition. See <see cref="TransitionPlanner.SlamSeconds"/>. All three come back with
    /// <see cref="TransitionPlan.FadeBars"/> at zero. A workbench that offers to set the bars of one of
    /// them is offering a number that nothing will read.</para>
    /// </summary>
    public static bool CountsBars(TransitionStyle style) =>
        style is not (TransitionStyle.Cut or TransitionStyle.Blend or TransitionStyle.Slam);

    /// <summary>Every technique heard in a room that plays at this tempo, in catalogue order. An
    /// unmeasured tempo matches everything, because a record nobody read is not evidence of a
    /// room.</summary>
    public static IReadOnlyList<MixTechnique> AtTempo(double bpm) =>
        [.. All.Where(t => t.SuitsTempo(bpm))];

    /// <summary>Every technique the given room uses.</summary>
    public static IReadOnlyList<MixTechnique> InGenre(MixGenre genre) =>
        [.. All.Where(t => t.Genres.Contains(genre))];

    /// <summary>
    /// The rooms a record at this tempo is most likely to come from, most likely first.
    ///
    /// <para>This is a guess, and the only one available. Nothing in the library says what a record is.
    /// It is a useful guess, because tempo is the one thing a genre is strict about. A tempo of 174
    /// almost always means drum and bass. A tempo of 112 is not techno. In the middle of the range the
    /// guess is useless, because house, tech house and the styles built on them sit on top of each
    /// other. Read it as "which conventions could apply here", never as "what this record is".</para>
    /// </summary>
    public static IReadOnlyList<MixGenre> RoomsAt(double bpm) => bpm switch
    {
        <= 0 => [],
        < 100 => [MixGenre.HipHop, MixGenre.OpenFormat],
        < 108 => [MixGenre.HipHop, MixGenre.Disco, MixGenre.OpenFormat],
        < 118 => [MixGenre.Amapiano, MixGenre.HipHop, MixGenre.Disco],
        < 123 => [MixGenre.Disco, MixGenre.House, MixGenre.AfroHouse],
        < 128 => [MixGenre.House, MixGenre.TechHouse, MixGenre.AfroHouse, MixGenre.MelodicHouse],
        < 132 => [MixGenre.TechHouse, MixGenre.Techno, MixGenre.House, MixGenre.ProgressiveHouse],
        < 138 => [MixGenre.Techno, MixGenre.TechHouse, MixGenre.UkGarage, MixGenre.Trance],
        < 145 => [MixGenre.Trance, MixGenre.Dubstep, MixGenre.Techno, MixGenre.Edm],
        < 160 => [MixGenre.Edm, MixGenre.Trance],
        < 186 => [MixGenre.DrumAndBass],
        _ => [MixGenre.DrumAndBass, MixGenre.OpenFormat],
    };

    /// <summary>Whether the pair in hand has been measured well enough for this move, and what is
    /// missing when it has not.</summary>
    /// <param name="canMeet">Whether the two tempos can be brought together inside the pull the policy
    /// allows. This is the planner's own answer, because the planner is the only place that arithmetic
    /// lives.</param>
    public static string? WhatIsMissing(
        MixTechnique technique, MixTrack outgoing, MixTrack incoming, bool canMeet)
    {
        ArgumentNullException.ThrowIfNull(technique);
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);

        var needs = technique.Needs;

        if (needs.HasFlag(MixNeeds.OutgoingGrid) && !outgoing.CanBeatMatch)
            return "the record playing has no grid to time it to";
        if (needs.HasFlag(MixNeeds.IncomingGrid) && !incoming.CanBeatMatch)
            return "the record coming in has no grid";
        if (needs.HasFlag(MixNeeds.OneTempo) && !canMeet)
            return "the two tempos will not meet";
        if (needs.HasFlag(MixNeeds.IncomingDrop) && incoming.Drops.Count == 0)
            return "the record coming in has no measured drop to land on";
        if (needs.HasFlag(MixNeeds.Bands) &&
            (outgoing.Analysis is not { HasBands: true } || incoming.Analysis is not { HasBands: true }))
            return "the bands were not read, so the bottom end cannot be handed over knowingly";
        if (needs.HasFlag(MixNeeds.Steady) && (!outgoing.IsSteadyEnoughToHold || !incoming.IsSteadyEnoughToHold))
            return "one of the two wanders too much to be held that long";

        return null;
    }

    /// <summary>The technique chosen, and why. The planner writes this into its trace. A screen can
    /// show it beside the changeover.</summary>
    /// <param name="Technique">The move.</param>
    /// <param name="Why">The reason it beat the others, in a DJ's terms.</param>
    /// <param name="Rooms">The rooms the tempo suggests. The reason was judged against these.</param>
    public sealed record Choice(MixTechnique Technique, string Why, IReadOnlyList<MixGenre> Rooms)
    {
        public override string ToString() =>
            $"{Technique.Name}: {Why}" + (Rooms.Count > 0 ? $" ({string.Join(", ", Rooms.Take(2))})" : "");
    }

    /// <summary>
    /// The move a DJ would make on this pair of records.
    ///
    /// <para>The moves are ordered by how specific the reason is, not by how impressive the move is. A
    /// technique is chosen because something about these two records asks for it. Examples are two
    /// drops that can land together, a record with no outro to fade, or two busy tracks at one tempo.
    /// Where nothing asks for anything in particular, the answer is the sixteen-bar blend. That is what
    /// most changeovers in most rooms actually are. A set that reached for a spinback whenever it could
    /// would sound repetitive.</para>
    ///
    /// <para>The luck is passed in, not generated here, as <see cref="Rolls.Choose"/> does it. The two
    /// moves that are gestures rather than mixes, the spinback and the echo, are held to
    /// <see cref="TheatricalChance"/>. So they stay moments, and do not become part of every
    /// changeover.</para>
    /// </summary>
    /// <param name="outgoingBpm">The tempo the room hears. This is the outgoing record's own tempo,
    /// whatever it has been pulled to.</param>
    /// <param name="canMeet">Whether the planner can bring the two tempos together.</param>
    /// <param name="luck">A number from 0 to 1, asked once per changeover.</param>
    public static Choice Best(
        MixTrack outgoing, MixTrack incoming, double outgoingBpm, bool canMeet, double luck)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);

        var rooms = RoomsAt(outgoingBpm > 0 ? outgoingBpm : outgoing.Bpm);
        Choice? Take(TransitionStyle style, string why)
        {
            var technique = For(style);
            if (WhatIsMissing(technique, outgoing, incoming, canMeet) is not null)
                return null;
            if (!technique.SuitsTempo(outgoingBpm > 0 ? outgoingBpm : outgoing.Bpm))
                return null;
            return new Choice(technique, why, rooms);
        }

        // Nothing to match against, so the pair is cut or faded. Which one applies depends on the
        // record going out, not on the room.
        if (!canMeet || !outgoing.CanBeatMatch || !incoming.CanBeatMatch)
        {
            if (outgoing.CanBeatMatch)
            {
                // A bar line to leave on is all a hand-over needs. The guides recommend the echo for
                // exactly this case: a big tempo change, with no outro to fade. It is still rationed,
                // because a set that answers every tempo gap the same way sounds repetitive.
                if (luck < TheatricalChance && Take(TransitionStyle.EchoOut, "the tempos will not meet and the record playing has a bar line to leave on") is { } echo)
                    return echo;
                if (Take(TransitionStyle.Slam, "the tempos will not meet, so the change is made on the one rather than smeared over a fade") is { } slam)
                    return slam;
            }

            return new Choice(For(TransitionStyle.Blend), "nothing to hold the two together", rooms);
        }

        // Both records have a drop that can be reached. This is the move the room came for. It is
        // judged on the incoming record, because that record's cue point has to move to make it happen.
        if (incoming.Drops.Count > 0 &&
            Take(TransitionStyle.DoubleDrop, "both records have a measured drop and one tempo to land them on") is { } doubleDrop)
            return doubleDrop;

        // A gesture, so it is rationed.
        if (luck < TheatricalChance &&
            Take(TransitionStyle.Spinback, "a high-energy pair in a room where the rewind is part of the language") is { } spin)
            return spin;

        // Both records are still loud where they meet, so there is nothing quiet to blend over.
        // Layering them whole sounds muddy. The filter takes the old one out instead.
        double outAt = outgoing.IntensityAt(TransitionPlanner.MixOutPoint(outgoing));
        double inAt = incoming.IntensityAt(TransitionPlanner.MixInPoint(incoming));
        if (outAt > BusyIntensity && inAt > BusyIntensity &&
            Take(TransitionStyle.FilterFade, "both records are busy where they meet, so the old one is filtered away rather than faded under") is { } filtered)
            return filtered;

        // Two steady records with room to spare suit the long layer. This tempo band is built around
        // that move.
        if (Take(TransitionStyle.BassSwap, "two steady records at one tempo, which is what a long layer needs") is { } layered)
            return layered;

        return new Choice(
            For(TransitionStyle.BeatMatched),
            "nothing about this pair asks for anything else, which is what most changeovers are",
            rooms);
    }

    /// <summary>
    /// How often the two moves that are gestures rather than mixes, the spinback and the echo out, are
    /// taken when they are available. The value is one fifth. The reason is the same as for
    /// <see cref="TransitionPlanner.NoiseChance"/>, which is one quarter. A rewind that comes up again
    /// and again in one set sounds like a machine, not like a DJ.
    /// </summary>
    public const double TheatricalChance = 0.2;

    /// <summary>
    /// Above this value, a record is still going hard where the changeover meets it. The scale is the
    /// one used by <see cref="MixTrack.IntensityAt"/>. A full club record sits around 0.3, and a
    /// breakdown is well under 0.1.
    /// </summary>
    private const double BusyIntensity = 0.22;
}
