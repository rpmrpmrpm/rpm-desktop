using System.Globalization;
using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Holds <see cref="BeatDbn"/> to madmom, the implementation it is a port of.
///
/// <para>Reading a Viterbi port against its source does not settle whether it is right: the state
/// space, the transition weights, where the observation splits a beat and whether the first frame
/// gets a transition are each an off-by-one away from a different set of beats, and every one of
/// those still produces beats that look reasonable. So the reference decides. Assets/dbn-reference.txt
/// holds curves built in Python, the beats madmom found in them, and a hash of the path it took to
/// find them; these tests replay the same numbers and demand the same answers.</para>
///
/// <para>The frame-zero convention is the easiest to get wrong: madmom's search transitions into the
/// first frame rather than starting in it, and reading it the other way still produces beats that
/// look right on six of the nine curves.</para>
///
/// <para>Regenerate it with scripts/make_dbn_reference.py, which documents how to get a Python that
/// madmom will install on.</para>
/// </summary>
public class BeatDbnTests
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
        var (logits, expected, _) = Reference.Value[name];

        var beats = BeatDbn.Beats(logits, FrameRate);

        Assert.Equal(expected.Length, beats.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], beats[i], Tolerance);
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
        var (logits, _, expected) = Reference.Value[name];

        Assert.Equal(expected, Fingerprint(BeatDbn.Path(logits, FrameRate)));
    }

    /// <summary>FNV-1a over the states, little-endian, as scripts/make_dbn_reference.py computes it.</summary>
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

    [Fact]
    public void The_state_space_is_the_one_madmom_builds()
    {
        // 1449 states over intervals of 14..55 frames, which is 215..55 BPM at fifty frames a
        // second. Recorded in the reference so a change to the range cannot pass unnoticed.
        Assert.Equal(States.Value, BeatDbn.StateCount(FrameRate));
    }

    [Fact]
    public void A_record_with_no_beats_in_it_gives_no_answer_rather_than_a_wrong_one()
    {
        Assert.Empty(BeatDbn.Beats([], FrameRate));
        Assert.Empty(BeatDbn.Beats([1f, 2f], 0));
    }

    private static readonly Lazy<int> States = new(() =>
        int.Parse(Lines().First(l => l.StartsWith("states ", StringComparison.Ordinal))[7..],
                  CultureInfo.InvariantCulture));

    private static readonly Lazy<Dictionary<string, (float[] Logits, double[] Beats, ulong Path)>> Reference =
        new(Load);

    private static IEnumerable<string> Lines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "dbn-reference.txt");
        Assert.True(File.Exists(path), $"No DBN reference at {path}. See scripts/make_dbn_reference.py.");
        return File.ReadLines(path);
    }

    private static Dictionary<string, (float[], double[], ulong)> Load()
    {
        var cases = new Dictionary<string, (float[], double[], ulong)>(StringComparer.Ordinal);
        string? name = null;
        float[]? logits = null;
        double[]? beats = null;

        foreach (var line in Lines())
        {
            if (line.StartsWith("case ", StringComparison.Ordinal))
                name = line[5..].Trim();
            else if (line.StartsWith("logits ", StringComparison.Ordinal))
                logits = [.. Numbers(line[7..]).Select(v => (float)v)];
            else if (line.StartsWith("beats ", StringComparison.Ordinal))
                beats = [.. Numbers(line[6..])];
            else if (line.StartsWith("path ", StringComparison.Ordinal) && name is not null)
                cases[name] = (logits ?? [], beats ?? [],
                               ulong.Parse(line[5..].Trim(), CultureInfo.InvariantCulture));
        }

        Assert.NotEmpty(cases);
        return cases;
    }

    private static IEnumerable<double> Numbers(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture));
}
