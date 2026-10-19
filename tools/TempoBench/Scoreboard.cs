using RPM.Core.Analysis;

namespace TempoBench;

/// <summary>The results of one track's readings, next to the corpus's reference tempo.</summary>
/// <param name="Fitted">The tempo the app would store: <see cref="TrackAnalysis.Bpm"/>, which is the
/// model's beats after <see cref="ModelBeatGrid"/> has fitted a grid to them.</param>
/// <param name="Raw">The same model's beats used directly, as the median of the gaps between them.
/// The difference between this and <paramref name="Fitted"/> is the only part of the number that
/// comes from RPM, not from Beat This.</param>
/// <param name="Unfolded">The same fitting with the fold off: <see cref="ModelBeatGrid.Fit"/>,
/// which is <see cref="ModelBeatGrid.Against"/> without the fold and without the shift onto the
/// onsets. The shift changes phase, not tempo, so against a tempo reference this is what RPM would
/// store if it did not change the octave.</param>
/// <param name="Beats">How many beats the model found.</param>
/// <param name="ShortGaps">Gaps between consecutive model beats shorter than six tenths of the
/// median: two detections of one beat. <see cref="TempoWindows"/> numbers beats by rounding the gap
/// to whole periods with a minimum step of one, so it cannot merge these: each one adds a beat that
/// does not exist and makes the fitted tempo too fast.</param>
/// <param name="LongGaps">Gaps longer than 1.6 medians: beats the model missed. The numbering
/// handles these, by rounding the step up to two or three.</param>
/// <param name="Constant">One tempo for the whole record, fitted by <see cref="ConstantFit"/>.</param>
internal readonly record struct Reading(
    string Stem, double Reference, double Fitted, double Raw, double Unfolded,
    double Confidence, double Seconds, int Beats = 0, int ShortGaps = 0, int LongGaps = 0,
    double Constant = 0, double Scatter = 0, double OnTheLine = 0, int Merged = 0,
    Drift.Result FittedDrift = default, Drift.Result ConstantDrift = default,
    Drift.Result RawDrift = default,
    Drift.Result BarSpan = default, Drift.Result ChangeoverSpan = default,
    double SplitHalf = 0,
    int BeatsPerBar = 0, double BarConfidence = 0);

/// <summary>
/// Tallies readings into the three accuracies, and prints them the way the published tables do.
/// </summary>
internal sealed class Scoreboard(string name)
{
    private readonly List<Reading> _readings = [];

    public string Name => name;
    public int Count => _readings.Count;

    public void Add(Reading reading) => _readings.Add(reading);

    /// <summary>Tracks for which the analyser gave no tempo. They count as wrong (the app cannot
    /// beat-match a record with no tempo), but they are shown separately, because they need a
    /// different fix from a wrong octave.</summary>
    public int Unread => _readings.Count(r => r.Fitted <= 0);

    public double Acc0(Func<Reading, double> of) =>
        Share(r => TempoAccuracy.MatchesExactly(of(r), r.Reference));

    public double Acc1(Func<Reading, double> of) =>
        Share(r => TempoAccuracy.Matches(of(r), r.Reference));

    public double Acc2(Func<Reading, double> of) =>
        Share(r => TempoAccuracy.MatchesAtAnyOctave(of(r), r.Reference));

    /// <summary>How many tracks were wrong by each octave factor, worst first. This shows the cause:
    /// many at 0.5 means the reader detects half-time, many at 0 means the reader is wrong.</summary>
    public IEnumerable<(double Factor, int Count)> Octaves(Func<Reading, double> of) =>
        _readings
            .Select(r => TempoAccuracy.OctaveOf(of(r), r.Reference))
            .Where(f => f != 1.0)
            .GroupBy(f => f)
            .Select(g => (Factor: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count);

    private double Share(Func<Reading, bool> passes) =>
        _readings.Count == 0 ? 0 : (double)_readings.Count(passes) / _readings.Count;

    public static double Fitted(Reading r) => r.Fitted;
    public static double Raw(Reading r) => r.Raw;
    public static double Unfolded(Reading r) => r.Unfolded;
    public static double Constant(Reading r) => r.Constant;

    public IReadOnlyList<Reading> Readings => _readings;

    public string Row(Func<Reading, double> of, string label) =>
        $"  {label,-22} {Acc1(of),8:P2} {Acc2(of),9:P2} {Acc0(of),8:P2}";

    /// <summary>
    /// A grid's drift across the corpus: the median track, and how many are above what a changeover
    /// allows. Only the tracks whose grids describe the same record are counted; see
    /// <see cref="Drift.Result.IsUsable"/>.
    /// </summary>
    public string DriftRow(Func<Reading, Drift.Result> of, string label, double budgetMs)
    {
        var usable = _readings.Select(of).Where(d => d.IsUsable).ToList();
        if (usable.Count == 0)
            return $"  {label,-22} no beats to compare against";

        var changeover = usable.Select(d => d.ChangeoverMs).Order().ToList();
        var jitter = usable.Select(d => d.JitterMs).Order().ToList();
        double over = (double)usable.Count(d => d.ChangeoverMs > budgetMs) / usable.Count;

        return $"  {label,-22} {changeover[changeover.Count / 2],9:F0} {changeover[(int)(0.9 * changeover.Count)],9:F0} " +
               $"{jitter[jitter.Count / 2],8:F1} {over,10:P1} {usable.Count,7}";
    }
}
