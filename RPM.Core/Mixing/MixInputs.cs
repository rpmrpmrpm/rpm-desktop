using System.Globalization;
using RPM.Core.Analysis;

namespace RPM.Core.Mixing;

/// <summary>How much of a signal the mixing code actually reads.</summary>
public enum MixUse
{
    /// <summary>The planner reads it. A changeover is different when this value changes.</summary>
    Used,

    /// <summary>Read in some places but not in others where it clearly belongs. This is the case to
    /// watch: a signal that is half wired up is easy to believe is fully wired up.</summary>
    Partly,

    /// <summary>Measured, stored, shared between machines, and read by nothing. This catalogue exists
    /// to show these.</summary>
    Unused,
}

/// <summary>
/// One thing the analyser found out about a record, as the mixing code sees it: what it says, whether
/// anybody measured it, and whether any of the mixing decisions depend on it.
/// </summary>
/// <param name="Name">The name of the signal. It matches the field on
/// <see cref="TrackAnalysis"/>.</param>
/// <param name="Group">The part of the reading it belongs to. Rows are grouped by this on
/// screen.</param>
/// <param name="Value">What it says for this record, already written out as text.</param>
/// <param name="Measured">False when nobody measured it. Every reader must treat that as its own
/// answer, not as the bottom of the scale.</param>
/// <param name="Use">Whether the mixing code reads it.</param>
/// <param name="Note">Where it is read, or what it would be used for if something read it.</param>
public sealed record MixSignal(
    string Name,
    string Group,
    string Value,
    bool Measured,
    MixUse Use,
    string Note);

/// <summary>
/// Everything the analyser knows about a record, with a note on whether the DJ reads each item.
///
/// <para>The verdicts here are maintained by hand. A signal changes from <see cref="MixUse.Unused"/> to
/// <see cref="MixUse.Used"/> when someone wires it up and updates this list. That is one small change.
/// Each verdict names the file that reads the signal, so a claim of "used" can be checked with one
/// search. A claim of "unused" is shown to be false as soon as a search finds a reader.</para>
/// </summary>
public static class MixInputs
{
    private const string Tempo = "Tempo";
    private const string Grid = "Grid";
    private const string Cues = "Cues";
    private const string Edges = "Edges";
    private const string Shape = "Shape";
    private const string Bands = "Bands";
    private const string Level = "Level";

    /// <summary>
    /// Every signal on this record, in reading order.
    ///
    /// <para>A record with no analysis still gives the full list, with every row unmeasured. What the
    /// DJ works with when nothing has been read is worth seeing, so each row is shown rather than an
    /// empty box.</para>
    /// </summary>
    public static IReadOnlyList<MixSignal> For(MixTrack? track)
    {
        var a = track?.Analysis;

        return
        [
            // ── tempo ──
            new(nameof(TrackAnalysis.Bpm), Tempo,
                a is { Bpm: > 0 } ? Num(a.Bpm, 2) : "none",
                a is { Bpm: > 0 }, MixUse.Used,
                "TransitionPlanner: the pull, the meeting tempo and the length of a bar."),

            new(nameof(TrackAnalysis.BpmConfidence), Tempo,
                a is null ? "unread" : Num(a.BpmConfidence, 2),
                a is not null, MixUse.Used,
                $"TrackAnalysis.CanBeatMatch: under {Num(TrackAnalysis.MinBeatMatchConfidence, 2)} the records are blended rather than matched."),

            new("DanceLevel", Tempo,
                a is { Bpm: > 0 } ? Num(TempoEstimator.DanceLevel(a.Bpm), 2) : "none",
                a is { Bpm: > 0 }, MixUse.Used,
                "TransitionPlanner: the octave the room counts in, which is what a bar is measured at."),

            // ── grid ──
            new(nameof(TrackAnalysis.BeatTimes), Grid,
                a is null ? "unread" : $"{a.BeatTimes.Count} beats",
                a is { BeatTimes.Count: > 0 }, MixUse.Used,
                "TrackAnalysis.BarAtOrBefore and NextDownbeatAfter: every bar line a changeover lands on."),

            new(nameof(TrackAnalysis.FirstDownbeat), Grid,
                a is null ? "unread" : a.FirstDownbeat.ToString(CultureInfo.InvariantCulture),
                a is not null, MixUse.Used,
                "TrackAnalysis: which of the beats are bar lines."),

            new(nameof(TrackAnalysis.DownbeatConfidence), Grid,
                a is null ? "unread" : Num(a.DownbeatConfidence, 2),
                a is not null, MixUse.Unused,
                a is not null && a.DownbeatConfidence < TrackAnalysis.MinDownbeatConfidence
                    ? "Nothing reads it. On this record the four bar phases were effectively tied, so the bar line the mix is being aligned to is a guess, and the mix is aligned to it just as hard as to a certain one."
                    : "Nothing reads it. It says how much to trust the bar line, and a changeover is timed to that bar line with the same confidence either way."),

            new(nameof(TrackAnalysis.BeatsPerBar), Grid,
                a is null ? "unread" : a.BeatsPerBar.ToString(CultureInfo.InvariantCulture),
                a is not null, MixUse.Partly,
                "Snapping reads it (TrackAnalysis.AlignEvery), and MixInFinder reads it. TransitionPlanner does not: every fade length is four beats of the counted tempo, so a record in three has its bars snapped on three and its fade measured on four."),

            // ── cues ──
            new(nameof(TrackAnalysis.IntroEndSeconds), Cues,
                a is null ? "unread" : Sec(a.IntroEndSeconds),
                a is not null, MixUse.Used,
                "TransitionPlanner.MixInPoint: where the needle goes down on the record coming in."),

            new(nameof(TrackAnalysis.OutroStartSeconds), Cues,
                a is null ? "unread" : Sec(a.OutroStartSeconds),
                a is not null, MixUse.Used,
                "TransitionPlanner.MixOutPoint: where this record starts being mixed out of."),

            // ── edges ──
            new(nameof(TrackAnalysis.AudioStartSeconds), Edges,
                a is { HasEdges: true } ? Sec(a.AudioStartSeconds) : "unmeasured",
                a is { HasEdges: true }, MixUse.Used,
                "MixInFinder, and the clamp that holds the cues inside the audio."),

            new(nameof(TrackAnalysis.AudioEndSeconds), Edges,
                a is { HasEdges: true } ? Sec(a.AudioEndSeconds) : "unmeasured",
                a is { HasEdges: true }, MixUse.Used,
                "TrackAnalysis.AudibleEndSeconds: what a changeover may not be timed past, and the bar lines in dead air that are refused."),

            new(nameof(TrackAnalysis.BeatFirstSeconds), Edges,
                a is { BeatFirstSeconds: > 0 } ? Sec(a.BeatFirstSeconds) : "unmeasured",
                a is { BeatFirstSeconds: > 0 }, MixUse.Unused,
                "Nothing reads it. It says where the tracker actually started holding the beat, as opposed to where the straight line through it claims a beat."),

            new(nameof(TrackAnalysis.BeatLastSeconds), Edges,
                a is { BeatLastSeconds: > 0 } ? Sec(a.BeatLastSeconds) : "unmeasured",
                a is { BeatLastSeconds: > 0 }, MixUse.Unused,
                "Nothing reads it, and nothing calls IsExtrapolatedAt either. A mix-out is very often past it, because the grid is carried to both ends of the record on purpose and the outro is exactly where the tracker stops defending an onset."),

            // ── shape ──
            new(nameof(TrackAnalysis.TempoStability), Shape,
                a is null ? "unread"
                    : a.TempoStability == TrackShape.Unmeasured ? "unmeasured"
                    : Num(a.TempoStability, 2),
                a is { TempoStability: > TrackShape.Unmeasured }, MixUse.Partly,
                "MixTechniques.WhatIsMissing calls IsSteadyEnoughToHold: a record measured loose is refused the two long moves, the bass swap and the double drop. TransitionPlanner itself still does not read it, so the length of an ordinary changeover is decided from the tempo mismatch the clamps could not close, which assumes both records hold their tempo for the whole sixteen bars."),

            new(nameof(TrackAnalysis.Phrases), Shape,
                a is { Phrases.IsKnown: true }
                    ? $"{a.Phrases.Bars} bars from bar {a.Phrases.FirstBar} ({Num(a.Phrases.Confidence, 2)})"
                    : "unmeasured",
                a is { Phrases.IsKnown: true }, MixUse.Partly,
                "MixInFinder reads it, so the next key lands on a phrase. TransitionPlanner counts its own fours from the bar the drums come in on and starts the mix there, which agrees with a measured phrase of 8, 16 or 32 bars but is not read off it: a record whose phrase starts somewhere else is still mixed out of on a four."),

            new(nameof(TrackAnalysis.DropSeconds), Shape,
                a is null ? "unread" : a.DropSeconds.Count == 0 ? "none" : $"{a.DropSeconds.Count}: {Times(a.DropSeconds)}",
                a is { DropSeconds.Count: > 0 }, MixUse.Partly,
                "TransitionPlanner.Dropped reads it: the double drop cues this record so its drop lands on the beat the record playing drops on, and it is the only changeover whose cue point is measured from something other than the grid. Not read by any of the four changeovers a set makes on its own, so a set that never asks for a double drop and never turns MixPolicy.Techniques on still lands its records without reference to where either of them drops."),

            // ── level ──
            new(nameof(TrackAnalysis.Intensity), Level,
                a is null ? "unread" : Num(a.Intensity, 2),
                a is not null, MixUse.Used,
                "DjSetPlanner: what the next record is chosen against."),

            new(nameof(TrackAnalysis.EnergyCurve), Level,
                a is null ? "unread" : $"{a.EnergyCurve.Count} steps",
                a is { EnergyCurve.Count: > 0 }, MixUse.Used,
                "TrackAnalysis.IntensityAt, via DjCrate: the energy under the needle now, rather than the track average."),

            new(nameof(TrackAnalysis.IntroLevel), Level,
                a is { IntroLevel: > 0 } ? Num(a.IntroLevel, 2) : "unmeasured",
                a is { IntroLevel: > 0 }, MixUse.Unused,
                "Nothing reads it. It is how loud the record coming in is at the exact point the changeover meets it, which is what decides whether it arrives under the old record or over it."),

            new(nameof(TrackAnalysis.OutroLevel), Level,
                a is { OutroLevel: > 0 } ? Num(a.OutroLevel, 2) : "unmeasured",
                a is { OutroLevel: > 0 }, MixUse.Unused,
                "Nothing reads it. The other half of the pair above: how loud this record still is where it is being mixed out of."),

            // ── bands ──
            new(nameof(TrackAnalysis.BassCurve), Bands,
                a is null ? "unread" : $"{a.BassCurve.Count} steps",
                a is { HasBands: true }, MixUse.Used,
                "MixOutFinder and MixInFinder: where the beat is, which is where a record can be left and joined."),

            new(nameof(TrackAnalysis.MidCurve), Bands,
                a is null ? "unread" : $"{a.MidCurve.Count} steps",
                a is { HasBands: true }, MixUse.Used,
                "MixInFinder: where this record's own instruments come in."),

            new(nameof(TrackAnalysis.TopCurve), Bands,
                a is null ? "unread" : $"{a.TopCurve.Count} steps",
                a is { HasBands: true }, MixUse.Used,
                "MixInFinder."),

            new(nameof(TrackAnalysis.VocalCurve), Bands,
                a is null ? "unread" : $"{a.VocalCurve.Count} steps",
                a is { HasBands: true }, MixUse.Used,
                "MixOutFinder: the early way out of a long record is a stretch with the singing stopped."),

            // ── key ──
            new(nameof(TrackAnalysis.Key), "Key",
                a is { Key.IsKnown: true } ? a.Key.Camelot ?? a.Key.ToString() : "unknown",
                a is { Key.IsKnown: true }, MixUse.Used,
                "DjSetPlanner: how well the next record sits against this one."),

            new(nameof(TrackAnalysis.KeyConfidence), "Key",
                a is null ? "unread" : Num(a.KeyConfidence, 2),
                a is not null, MixUse.Unused,
                "Nothing reads it. A key read at a tenth of a point counts for exactly as much in the running order as one read at nine tenths."),

            // ── provenance ──
            new(nameof(TrackAnalysis.Modelled), "Read by",
                a is null ? "unread" : a.Modelled ? "model" : "DSP",
                a is not null, MixUse.Used,
                "DjSetPlanner: the model is better at the octave and the bar, which is what the chooser is most often wrong about."),
        ];
    }

    /// <summary>The signals nothing reads. This is what the list was built to answer.</summary>
    public static IReadOnlyList<MixSignal> Unused(MixTrack? track) =>
        [.. For(track).Where(s => s.Use != MixUse.Used)];

    /// <summary>The signals that nothing reads and that somebody measured. A signal that was not
    /// measured on this record costs nothing and proves nothing. An unused signal with a real number in
    /// it is a measurement that is thrown away.</summary>
    public static IReadOnlyList<MixSignal> Wasted(MixTrack? track) =>
        [.. For(track).Where(s => s.Use == MixUse.Unused && s.Measured)];

    private static string Num(double value, int places) =>
        value.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>A time as a DJ reads it off a deck. Always invariant culture. This output goes to
    /// developer screens and to logs that are searched, and a decimal comma in part of it helps
    /// nobody.</summary>
    public static string Sec(double seconds) =>
        seconds <= 0
            ? "0:00.0"
            : string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60)}:{seconds % 60:00.0}");

    private static string Times(IReadOnlyList<double> times) =>
        string.Join(", ", times.Take(6).Select(Sec)) + (times.Count > 6 ? ", ..." : "");
}
