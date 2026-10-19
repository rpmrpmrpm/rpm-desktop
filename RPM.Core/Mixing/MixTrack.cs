using RPM.Core.Analysis;
using RPM.Core.Lyrics;

namespace RPM.Core.Mixing;

/// <summary>
/// A record in the DJ's crate: the file, its name, and what the analyser found in it. The DJ decides
/// everything from this: what to play next, when to bring it in, and how far to pull its tempo.
/// </summary>
public sealed record MixTrack
{
    /// <summary>The file to decode. This may be a URL for a track stored in the cloud.</summary>
    public required string Source { get; init; }

    /// <summary>The track's identity as the rest of the app knows it (its library path). The player's
    /// "this row is playing" highlight compares against this value.</summary>
    public required string Path { get; init; }

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }

    /// <summary>What the analyser found. Null until the track has been through
    /// <see cref="TrackAnalyzer"/>. The DJ can still play an unanalysed record, but cannot beat-match
    /// it.</summary>
    public TrackAnalysis? Analysis { get; init; }

    public bool IsAnalyzed => Analysis is not null;

    /// <summary>
    /// This record's lyric sheet, if the crate looked for one. Null means nobody looked.
    /// <see cref="LyricSheet.Empty"/> means someone looked and found none. These are different facts,
    /// so they are kept apart.
    ///
    /// <para>The sheet is fetched when the crate is built, not while the record plays. Fetching a sheet
    /// is a network round trip, and the DJ needs it before the changeover it will decide. A lookup
    /// started when the fade opens comes too late.</para>
    /// </summary>
    public LyricSheet? Sheet { get; init; }

    /// <summary>
    /// Where someone sings on this record. The sheet is used where there is one. Otherwise the middle
    /// of the record's own spectrum is used. <see cref="VocalMap.Unknown"/> is returned where neither
    /// has been read.
    ///
    /// <para>This is worked out when it is asked for, not stored. The caller keeps the answer. The band
    /// reading scans the whole curve, so ask once per changeover and keep the result in a local
    /// variable. See <see cref="VocalMap.For"/> for why a missing sheet does not mean silence.</para>
    /// </summary>
    public VocalMap Voices => VocalMap.For(Analysis, Sheet);

    /// <summary>
    /// True when a trained model read this record rather than the DSP passes.
    ///
    /// <para>The chooser cares most about the tempo octave and the bar phase. These are the two
    /// readings that the DSP is least sure of, and the two the model was brought in for. By the time
    /// they reach this point both are stored numbers. Nothing downstream can tell a confident wrong
    /// octave from a right one. This is the only value that says which kind of reading it is.</para>
    /// </summary>
    public bool IsModelled => Analysis?.Modelled == true;

    /// <summary>
    /// How long this record plays.
    ///
    /// <para>Both numbers are measurements, but of different things. Where they differ by more than
    /// <see cref="SameRecordSeconds"/>, the tag wins. The tag was read from this file. The analysis
    /// holds the length of the audio that was analysed. A reading taken from the shared pool under a
    /// name that turned out to fit two records is a reading of a different record. Believing it gives a
    /// timeline longer than the file. Bars are then counted over audio nobody has, and a changeover can
    /// be timed past the end of the music. Within the tolerance the analysis is preferred, because it
    /// is the better of the two. It is measured from the samples, not from a file header.</para>
    /// </summary>
    public double DurationSeconds =>
        Analysis?.DurationSeconds is > 0 and var measured
            ? TaggedDuration > 0 &&
              Math.Abs(measured - TaggedDuration) > TrackAnalysis.SameRecordSeconds
                ? TaggedDuration
                : measured
            : TaggedDuration;

    /// <summary>Duration from the library's tags. It is used until the analyser gives a real one, if it
    /// ever does.</summary>
    public double TaggedDuration { get; init; }

    public double Bpm => Analysis?.Bpm ?? 0;
    public MusicalKey Key => Analysis?.Key ?? MusicalKey.Unknown;
    public double Intensity => Analysis?.Intensity ?? 0;

    // ── the rest of what was measured ──
    //
    // The three above are the ones the chooser asks for. They are the only ones it can ask for without
    // going through Analysis itself. The analyser measures much more than three numbers about a record.
    // What follows makes the rest available to the DJ on the same terms. So whether a signal is used is
    // a decision that someone takes, not a side effect of which properties happened to be reachable.
    // Each one answers the way an unread record answers: nothing measured, and the reader falls back to
    // what it did before these were here.

    /// <summary>How sure the analyser was of the tempo, 0 to 1.</summary>
    public double BpmConfidence => Analysis?.BpmConfidence ?? 0;

    /// <summary>How clearly the "one" stands out from the other beats of the bar, 0 to 1. Under
    /// <see cref="TrackAnalysis.MinDownbeatConfidence"/> the four phases were effectively tied. The bar
    /// line is then a guess that happened to be right.</summary>
    public double DownbeatConfidence => Analysis?.DownbeatConfidence ?? 0;

    /// <summary>How many beats this record counts to a bar. Four, unless it was measured as
    /// otherwise.</summary>
    public int BeatsPerBar => Analysis?.BeatsPerBar ?? 4;

    /// <summary>How steadily the record is played, 0 to 1. See <see cref="TrackShape.TempoStability"/>.
    /// Zero means nobody measured it. That is not the same as a record that wanders. Use
    /// <see cref="IsSteadyEnoughToHold"/> rather than reading the number.</summary>
    public double TempoStability => Analysis?.TempoStability ?? TrackShape.Unmeasured;

    /// <summary>Whether this record can be held on a bent tempo for the length of a long changeover.
    /// True for a record that nobody measured. Many records are in that state.</summary>
    public bool IsSteadyEnoughToHold => Analysis?.IsSteadyEnoughToHold ?? true;

    /// <summary>Where the record's phrases fall. A changeover that lands on a phrase sounds intended.
    /// One that lands on an arbitrary bar sounds like it slipped.</summary>
    public PhraseGrid Phrases => Analysis?.Phrases ?? default;

    /// <summary>Where the beat comes back after a breakdown. These are the best places in this record
    /// to bring another record in. The list is empty for a record with no breakdown.</summary>
    public IReadOnlyList<double> Drops => Analysis?.DropSeconds ?? [];

    /// <summary>How loud the record is at the point where a changeover meets it as the incoming record,
    /// on the scale of <see cref="Intensity"/>. Zero when unmeasured.</summary>
    public double IntroLevel => Analysis?.IntroLevel ?? 0;

    public double OutroLevel => Analysis?.OutroLevel ?? 0;

    /// <summary>
    /// Where this record stops being audible: the measured end, or the file's own end when nothing
    /// measured it. A changeover may not be timed past this point.
    ///
    /// <para>The measurement is used only when it is valid: measured, positive, and inside the file. A
    /// reading can carry a duration of zero (a stub, or a row taken from the pool before the file was
    /// seen). The track then reports zero for how long it plays. That is not "no idea" to the code that
    /// uses it. It means the record has no room to mix out of.</para>
    /// </summary>
    public double AudibleEndSeconds =>
        Analysis is { HasEdges: true, AudioEndSeconds: > 0 and var end } && end < DurationSeconds
            ? end
            : DurationSeconds;

    /// <summary>Whether the grid at this point is a line carried on from the last beat that was heard,
    /// rather than beats somebody heard. False everywhere on a record whose beat span was never
    /// measured.</summary>
    public bool IsExtrapolatedAt(double seconds) => Analysis?.IsExtrapolatedAt(seconds) == true;

    /// <summary>How hard the record is hitting at a given point, 0 to 1. Compare
    /// <see cref="Intensity"/>, which is the whole track reduced to one number.</summary>
    public double IntensityAt(double seconds) => Analysis?.IntensityAt(seconds) ?? Intensity;

    /// <summary>True when there is enough to beat-match on: a tempo, a confident one, and a grid to
    /// align to.</summary>
    public bool CanBeatMatch => Analysis?.CanBeatMatch == true;

    public string Display => string.IsNullOrWhiteSpace(Artist) ? (Title ?? Path) : $"{Artist} - {Title}";
}
