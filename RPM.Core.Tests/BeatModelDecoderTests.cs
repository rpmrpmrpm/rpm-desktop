using System.Globalization;
using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Replays what the model actually said about a real record, and checks the beats that come out are
/// the ones the reference implementation derived from those same numbers.
///
/// <para>This is the test that shows the port matches its source. The model itself is not involved
/// (the logits in the fixture were recorded from it once), so what is under test is only the part
/// that is ported from Python: how a record is cut into windows, which of each window's answers are
/// kept, and how a curve becomes a list of beats. Every one of those steps is a decision that moves
/// beats rather than raising an error when it is wrong, and a beat moved by two frames breaks a
/// beat-match.</para>
///
/// <para>Assets/beat-decoder-reference.txt holds the first sixty seconds of a real track: the
/// per-window model output, and the beats and bar lines the reference produced from it. Regenerate
/// it with scripts/make_decoder_reference.py.</para>
/// </summary>
public class BeatModelDecoderTests
{
    private sealed record Fixture(
        int Frames,
        int[] Starts,
        double[] Beats,
        double[] Downbeats,
        Dictionary<int, (float[] Beat, float[] Downbeat)> Windows);

    private static Fixture Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "beat-decoder-reference.txt");
        int frames = 0;
        int[] starts = [];
        double[] beats = [], downbeats = [];
        var beatChunks = new Dictionary<int, float[]>();
        var downbeatChunks = new Dictionary<int, float[]>();

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var parts = line.Split(' ');
            switch (parts[0])
            {
                case "frames":
                    frames = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "starts":
                    starts = parts.Skip(1).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    break;
                case "beats":
                    beats = Numbers(parts.Skip(1));
                    break;
                case "downbeats":
                    downbeats = Numbers(parts.Skip(1));
                    break;
                case "chunk":
                    int n = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    var values = parts.Skip(3)
                        .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    (parts[2] == "beat" ? beatChunks : downbeatChunks)[n] = values;
                    break;
            }
        }

        var windows = starts
            .Select((start, i) => (start, pair: (beatChunks[i], downbeatChunks[i])))
            .ToDictionary(x => x.start, x => x.pair);

        return new Fixture(frames, starts, beats, downbeats, windows);
    }

    private static double[] Numbers(IEnumerable<string> parts) =>
        parts.Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

    [Fact]
    public void Cuts_the_record_into_the_same_windows()
    {
        var fixture = Load();

        // The windows have to line up before anything else can: a window half a second off reads
        // the model's answer for one part of the record as though it were about another.
        Assert.Equal(fixture.Starts, BeatModelDecoder.WindowStarts(fixture.Frames));
    }

    [Fact]
    public void Finds_the_beats_the_reference_found()
    {
        var fixture = Load();
        var reading = BeatModelDecoder.Decode(fixture.Frames, start => fixture.Windows[start]);

        Assert.Equal(fixture.Beats.Length, reading.BeatTimes.Count);
        for (int i = 0; i < fixture.Beats.Length; i++)
            Assert.Equal(fixture.Beats[i], reading.BeatTimes[i], 4);
    }

    [Fact]
    public void Finds_the_bar_lines_the_reference_found()
    {
        var fixture = Load();
        var reading = BeatModelDecoder.Decode(fixture.Frames, start => fixture.Windows[start]);

        Assert.Equal(fixture.Downbeats.Length, reading.DownbeatTimes.Count);
        for (int i = 0; i < fixture.Downbeats.Length; i++)
            Assert.Equal(fixture.Downbeats[i], reading.DownbeatTimes[i], 4);
    }

    /// <summary>
    /// A track is stopped between windows, not at the end of it.
    ///
    /// <para>This is what makes a playlist swap or a stopped set take effect: without it, cancelling
    /// reaches the decode and then waits for every remaining window of the track that is already in
    /// the model, which is seconds of machine time spent on a record nobody is going to hear.
    /// The read count is what is actually being asserted; the exception only proves it stopped.</para>
    /// </summary>
    [Fact]
    public void Stops_between_windows_when_it_is_cancelled()
    {
        var fixture = Load();
        using var stopping = new CancellationTokenSource();

        int windowsRead = 0;

        Assert.Throws<OperationCanceledException>(() =>
            BeatModelDecoder.Decode(fixture.Frames, start =>
            {
                windowsRead++;
                stopping.Cancel();          // stopped after the first one
                return fixture.Windows[start];
            }, stopping.Token));

        Assert.Equal(1, windowsRead);
        Assert.True(fixture.Starts.Length > 1, "a fixture of one window would prove nothing");
    }

    [Fact]
    public void Reads_every_window_when_it_is_not_cancelled()
    {
        var fixture = Load();
        int windowsRead = 0;

        BeatModelDecoder.Decode(fixture.Frames, start =>
        {
            windowsRead++;
            return fixture.Windows[start];
        }, CancellationToken.None);

        Assert.Equal(fixture.Starts.Length, windowsRead);
    }

    [Fact]
    public void Every_bar_line_is_also_a_beat()
    {
        var fixture = Load();
        var reading = BeatModelDecoder.Decode(fixture.Frames, start => fixture.Windows[start]);

        // What the whole grid downstream assumes: bar lines are counted as positions in the beat
        // list, so one that is not a beat cannot be counted at all.
        Assert.All(reading.DownbeatTimes, at => Assert.Contains(at, reading.BeatTimes));
        Assert.Equal(reading.DownbeatTimes.Count, reading.DownbeatTimes.Distinct().Count());
    }

    [Fact]
    public void A_real_reading_fits_a_grid_that_can_be_mixed()
    {
        var fixture = Load();
        var reading = BeatModelDecoder.Decode(fixture.Frames, start => fixture.Windows[start]);
        var grid = ModelBeatGrid.Fit(reading, durationSeconds: 60);

        Assert.True(grid.IsUsable);
        // The whole track reads 76.9 BPM over its full length; the first minute of it should agree.
        Assert.InRange(grid.Bpm, 70, 82);
        Assert.InRange(grid.BeatsPerBar, 2, 8);
    }

    [Fact]
    public void Neighbouring_peaks_group_the_way_the_reference_groups_them()
    {
        // Frames 10, 11 and 12 all clear the threshold and are all locally largest. The reference
        // measures each against the running average of the group, which has reached 10.5 by the
        // time 12 is considered, so 12 is a frame and a half away and begins a group of its own.
        var logits = new float[20];
        logits[10] = logits[11] = logits[12] = 5;

        var times = BeatModelDecoder.PickPeaks(logits);
        Assert.Equal([10.5 / MelSpectrogram.FrameRate, 12 / MelSpectrogram.FrameRate], times);
    }

    [Fact]
    public void Frames_no_window_reached_produce_nothing()
    {
        var logits = new float[100];
        Array.Fill(logits, BeatModelDecoder.Unread);
        logits[50] = 3;

        Assert.Equal([50 / MelSpectrogram.FrameRate], BeatModelDecoder.PickPeaks(logits));
    }
}
