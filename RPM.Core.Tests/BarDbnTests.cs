using System.Globalization;
using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Holds <see cref="BarDbn"/> to madmom, the implementation it is a port of, called the way
/// <c>beat_this</c> calls it.
///
/// <para>Reading a Viterbi port against its source does not settle whether it is right, and this
/// search has more places to be wrong than the beat one: the bar's first beat is reached from its
/// last rather than from nothing, the observation is three-way and its first column is beat
/// <em>minus</em> bar line, half the tempo transitions are dropped before the rest are normalised,
/// and the initial distribution is a different constant for a bar of three than for a bar of four,
/// which is what decides between them on a record the search finds ambiguous. Every one of those
/// still produces bar lines that look reasonable.</para>
///
/// <para>So the reference decides. Assets/bar-dbn-reference.txt holds curves built in Python, what
/// madmom made of them, and a hash of the path it took; these tests replay the same numbers and
/// demand the same answers. Regenerate it with scripts/make_bar_dbn_reference.py, which documents
/// how to get a Python that madmom will install on.</para>
/// </summary>
public class BarDbnTests
{
    private const double FrameRate = 50;

    /// <summary>Half a frame. The times are frame indices over the frame rate on both sides, so a
    /// port that agrees at all agrees exactly; this only keeps the text format's rounding out.</summary>
    private const double Tolerance = 0.01;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Reference.Value.Keys)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Finds_the_beats_madmom_found(string name)
    {
        var c = Reference.Value[name];

        var reading = BarDbn.Read(c.BeatLogits, c.BarLogits, FrameRate);

        Assert.Equal(c.Beats.Length, reading.BeatTimes.Length);
        for (int i = 0; i < c.Beats.Length; i++)
            Assert.Equal(c.Beats[i], reading.BeatTimes[i], Tolerance);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Finds_the_bar_lines_madmom_found(string name)
    {
        var c = Reference.Value[name];

        var reading = BarDbn.Read(c.BeatLogits, c.BarLogits, FrameRate);

        Assert.Equal(c.Bars.Length, reading.DownbeatTimes.Length);
        for (int i = 0; i < c.Bars.Length; i++)
            Assert.Equal(c.Bars[i], reading.DownbeatTimes[i], Tolerance);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Counts_the_bar_the_way_madmom_counted_it(string name)
    {
        var c = Reference.Value[name];

        Assert.Equal(c.BeatsPerBar, BarDbn.Read(c.BeatLogits, c.BarLogits, FrameRate).BeatsPerBar);
    }

    /// <summary>
    /// What each bar length scored, which is all the evidence for one over the other. Held to
    /// nine decimal places rather than to a tolerance: the two are within half a nat of each other
    /// on three of these curves, so a port that is merely close would pick the wrong bar and every
    /// fade in the mix would be the wrong length.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Scores_each_bar_length_the_way_madmom_scored_it(string name)
    {
        var c = Reference.Value[name];

        var (_, _, scores) = BarDbn.Score(c.BeatLogits, c.BarLogits, FrameRate);

        Assert.Equal(c.LogProbabilities.Length, scores.Length);
        for (int i = 0; i < scores.Length; i++)
            Assert.Equal(c.LogProbabilities[i], scores[i], 6);
    }

    /// <summary>
    /// The same beats can be arrived at by different paths, and a search that agrees on the beats
    /// but not on the route will disagree on some other record. So the whole path is
    /// held too, as a hash of every state at every frame.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Walks_the_path_madmom_walked(string name)
    {
        var c = Reference.Value[name];

        var (path, _, _) = BarDbn.Score(c.BeatLogits, c.BarLogits, FrameRate);

        Assert.Equal(c.Path, Fingerprint(path));
    }

    /// <summary>FNV-1a over the states as four little-endian bytes each, which is what
    /// scripts/make_bar_dbn_reference.py hashes a Viterbi path with.</summary>
    private static ulong Fingerprint(int[] path)
    {
        ulong value = 0xCBF29CE484222325;
        foreach (int state in path)
        {
            foreach (byte b in BitConverter.GetBytes(state))
                value = (value ^ b) * 0x100000001B3;
        }

        return value;
    }

    /// <summary>The same, over eight-byte values: what the script hashes everything else with.</summary>
    private static ulong Fingerprint(IEnumerable<long> values)
    {
        ulong value = 0xCBF29CE484222325;
        foreach (long item in values)
        {
            foreach (byte b in BitConverter.GetBytes(item))
                value = (value ^ b) * 0x100000001B3;
        }

        return value;
    }

    [Fact]
    public void The_state_space_is_the_one_madmom_builds()
    {
        // A beat's 1449 states over intervals of 14..55 frames, repeated once per beat of the bar.
        // Recorded in the reference so a change to the range cannot pass unnoticed.
        foreach (var (beatsPerBar, states) in States.Value)
            Assert.Equal(states, BarDbn.StateCount(FrameRate, beatsPerBar));
    }

    [Fact]
    public void Each_state_reads_the_density_madmom_points_it_at()
    {
        // Off the beat, on a beat, or on a bar line. Getting one state of one interval wrong still
        // produces beats, in slightly the wrong places on slow records, which is the kind of fault
        // that survives every test that only looks at the output.
        foreach (var (beatsPerBar, expected) in Reads.Value)
            Assert.Equal(expected, Fingerprint(BarDbn.Reads(FrameRate, beatsPerBar).Select(r => (long)r)));
    }

    [Fact]
    public void The_transition_model_has_the_shape_madmom_builds()
    {
        // Every transition, destination by destination, in the order the search considers them. The
        // path fingerprint cannot pin this down on its own: a search takes the first of two equally
        // good predecessors, so a port that enumerates them the other way round breaks ties the
        // other way round and agrees on nine records out of ten. It is also where the dropped
        // transitions show: half of them are below numpy's spacing(1) and are removed rather than
        // made small, and a port that keeps them has 1764 per beat here instead of 9148 per bar.
        foreach (var (beatsPerBar, expected) in Transitions.Value)
        {
            var mine = BarDbn.Transitions(FrameRate, beatsPerBar).ToArray();
            Assert.Equal(expected.Count, mine.Length);
            Assert.Equal(expected.Shape,
                Fingerprint(mine.SelectMany(t => new[] { (long)t.To, (long)t.From })));
        }
    }

    [Fact]
    public void The_transition_weights_are_the_ones_madmom_computes()
    {
        // Held as a sum rather than one by one, and deliberately. Python's logarithm and .NET's
        // disagree in the last bit on three of the 6861, which is a difference of 1e-12 in a number
        // near -29 and is nothing; asserting each would fail on that. The sum does not move under
        // it, and moves by thousands under any change to the formula, the lambda or the
        // normalisation.
        foreach (var (beatsPerBar, expected) in Transitions.Value)
        {
            double total = BarDbn.Transitions(FrameRate, beatsPerBar).Sum(t => t.LogProbability);
            Assert.Equal(expected.Weight, total, 6);
        }
    }

    [Fact]
    public void Every_bar_line_is_one_of_the_beats()
    {
        // What this search is for, and what snapping a separately-picked bar line onto the nearest
        // beat could only approximate. It has to hold on every curve, including the noise.
        foreach (var c in Reference.Value.Values)
        {
            var reading = BarDbn.Read(c.BeatLogits, c.BarLogits, FrameRate);
            foreach (double bar in reading.DownbeatTimes)
                Assert.Contains(bar, reading.BeatTimes);
        }
    }

    [Fact]
    public void Bar_lines_are_a_whole_number_of_bars_apart()
    {
        // The other thing the state space guarantees and a majority vote over picked peaks does
        // not: consecutive bar lines are exactly the bar length apart in beats, so a fade measured
        // in bars is the length it says it is wherever in the record it starts.
        foreach (var (name, c) in Reference.Value)
        {
            if (name == "noise")
                continue;

            var reading = BarDbn.Read(c.BeatLogits, c.BarLogits, FrameRate);
            var beats = reading.BeatTimes;

            var at = reading.DownbeatTimes.Select(b => Array.IndexOf(beats, b)).ToArray();
            for (int i = 1; i < at.Length; i++)
                Assert.Equal(0, (at[i] - at[i - 1]) % reading.BeatsPerBar);
        }
    }

    [Fact]
    public void A_record_with_no_beats_in_it_gives_no_answer_rather_than_a_wrong_one()
    {
        Assert.Empty(BarDbn.Read([], [], FrameRate).BeatTimes);
        Assert.Empty(BarDbn.Read([1f, 2f], [1f, 2f], 0).BeatTimes);

        // Nothing anywhere near the threshold: the whole record is trimmed away before the search.
        var quiet = Enumerable.Repeat(-20f, 500).ToArray();
        Assert.Empty(BarDbn.Read(quiet, quiet, FrameRate).BeatTimes);
        Assert.Equal(0, BarDbn.Read(quiet, quiet, FrameRate).BeatsPerBar);
    }

    private static readonly Lazy<Dictionary<int, int>> States = new(() =>
        Parts("states ").ToDictionary(Key, p => int.Parse(p[1], CultureInfo.InvariantCulture)));

    private static readonly Lazy<Dictionary<int, ulong>> Reads = new(() =>
        Parts("reads ").ToDictionary(Key, p => ulong.Parse(p[1], CultureInfo.InvariantCulture)));

    private static readonly Lazy<Dictionary<int, (int Count, ulong Shape, double Weight)>> Transitions =
        new(() => Parts("transitions ").ToDictionary(
            Key,
            p => (int.Parse(p[1], CultureInfo.InvariantCulture),
                  ulong.Parse(p[2], CultureInfo.InvariantCulture),
                  double.Parse(p[3], CultureInfo.InvariantCulture))));

    /// <summary>The beats-per-bar a header line is about, which is always its first word.</summary>
    private static int Key(string[] parts) => int.Parse(parts[0], CultureInfo.InvariantCulture);

    /// <summary>The words of every header line carrying a tag. Read inside a Lazy, so the file is
    /// walked once per fingerprint rather than once per test.</summary>
    private static IEnumerable<string[]> Parts(string tag) =>
        Lines()
            .Select(l => Field(l, tag))
            .Where(v => v is not null)
            .Select(v => v!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

    private sealed record Case(
        float[] BeatLogits, float[] BarLogits, int BeatsPerBar,
        double[] Beats, double[] Bars, ulong Path, double[] LogProbabilities);

    private static readonly Lazy<Dictionary<string, Case>> Reference = new(Load);

    private static IEnumerable<string> Lines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "bar-dbn-reference.txt");
        Assert.True(File.Exists(path),
            $"No bar DBN reference at {path}. See scripts/make_bar_dbn_reference.py.");
        return File.ReadLines(path);
    }

    private static Dictionary<string, Case> Load()
    {
        var cases = new Dictionary<string, Case>(StringComparer.Ordinal);
        string? name = null;
        float[] beatLogits = [], barLogits = [];
        double[] beats = [], bars = [];
        int perBar = 0;
        ulong path = 0;

        foreach (var line in Lines())
        {
            if (Field(line, "case ") is { } value)
                name = value.Trim();
            else if (Field(line, "beat_logits ") is { } bl)
                beatLogits = [.. Numbers(bl).Select(v => (float)v)];
            else if (Field(line, "bar_logits ") is { } dl)
                barLogits = [.. Numbers(dl).Select(v => (float)v)];
            else if (Field(line, "per_bar ") is { } pb)
                perBar = int.Parse(pb.Trim(), CultureInfo.InvariantCulture);
            else if (Field(line, "beats ") is { } b)
                beats = [.. Numbers(b)];
            else if (Field(line, "bars ") is { } d)
                bars = [.. Numbers(d)];
            else if (Field(line, "path ") is { } p)
                path = ulong.Parse(p.Trim(), CultureInfo.InvariantCulture);
            else if (Field(line, "logprob ") is { } lp && name is not null)
                cases[name] = new Case(beatLogits, barLogits, perBar, beats, bars, path, [.. Numbers(lp)]);
        }

        Assert.NotEmpty(cases);
        return cases;
    }

    /// <summary>The rest of a line after its tag, or null when the line is not that tag. "bars " and
    /// "bar_logits " both begin with "bar", so the tags have to be matched whole.</summary>
    private static string? Field(string line, string tag) =>
        line.StartsWith(tag, StringComparison.Ordinal) ? line[tag.Length..] : null;

    private static IEnumerable<double> Numbers(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture));
}
