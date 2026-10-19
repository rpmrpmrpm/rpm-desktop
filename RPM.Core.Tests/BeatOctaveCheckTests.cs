using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// The check that asks a second reading of the same curve which octave the record is in.
///
/// <para>Which octave a search settles on is the one thing it is genuinely uncertain about, and the
/// evidence that decides it is already in the curve and thrown away: the frames the model gave a
/// middling score to. A record counted at half speed has one of those halfway between every pair of
/// beats, all the way through; a record that is really at that tempo does not.</para>
///
/// <para>What is held here is what must be true <em>whichever way the check fires</em>, because
/// whether it fires on a given record is a judgement measured on a corpus rather than a property to
/// assert (see bench/RESULTS.md: it is worth 1.5 points of Accuracy1 on GiantSteps Tempo and nothing
/// at all on GTZAN). What it must never do is any of the things below.</para>
/// </summary>
public class BeatOctaveCheckTests
{
    private const double Rate = MelSpectrogram.FrameRate;

    /// <summary>A curve as the model emits one: a peak on every beat, and optionally a fainter one
    /// halfway between: the "maybe" that says the record may really be twice as fast.</summary>
    private static float[] Curve(int frames, double bpm, float onBeat = 4f, float between = -4f)
    {
        var logits = new float[frames];
        Array.Fill(logits, -4f);

        double period = 60.0 * Rate / bpm;
        for (double at = 0; at < frames; at += period)
        {
            int on = (int)Math.Round(at);
            if (on < frames)
                logits[on] = onBeat;

            int half = (int)Math.Round(at + period / 2);
            if (between > -4f && half < frames)
                logits[half] = between;
        }

        return logits;
    }

    /// <summary>Bar lines every fourth beat of the curve.</summary>
    private static float[] Bars(int frames, double bpm)
    {
        var logits = new float[frames];
        Array.Fill(logits, -4f);

        double period = 60.0 * Rate / bpm;
        int n = 0;
        for (double at = 0; at < frames; at += period, n++)
            if (n % 4 == 0 && (int)Math.Round(at) < frames)
                logits[(int)Math.Round(at)] = 4f;

        return logits;
    }

    private static double Period(IReadOnlyList<double> beats)
    {
        if (beats.Count < 2)
            return 0;

        var gaps = beats.Zip(beats.Skip(1), (a, b) => b - a).Order().ToArray();
        return gaps[gaps.Length / 2];
    }

    public static TheoryData<double, float> Records()
    {
        var data = new TheoryData<double, float>();
        foreach (double bpm in new[] { 64.0, 96.0, 128.0, 174.0, 200.0 })
            foreach (float between in new[] { -4f, -0.2f, 0.5f, 2f })
                data.Add(bpm, between);
        return data;
    }

    [Theory]
    [MemberData(nameof(Records))]
    public void A_record_is_only_ever_moved_by_an_octave(double bpm, float between)
    {
        // The check may move a reading or leave it, and which it does is a judgement. What it may
        // not do is arrive at a tempo that is not a power of two away from the one it was given:
        // that would not be a correction, it would be a second opinion about the beat itself.
        var beat = Curve(3000, bpm, between: between);
        var bars = Bars(3000, bpm);

        double before = Period(BeatModelDecoder.Bars(beat, bars).BeatTimes);
        double after = Period(BeatModelDecoder.Counted(beat, bars).BeatTimes);

        Assert.True(before > 0 && after > 0);

        double octaves = Math.Log2(before / after);
        Assert.Equal(Math.Round(octaves), octaves, 3);
        Assert.InRange(Math.Abs(octaves), 0, 2);
    }

    [Theory]
    [MemberData(nameof(Records))]
    public void Every_bar_line_is_still_one_of_the_beats(double bpm, float between)
    {
        // The one thing the search knows better than anything else here. Halving a grid throws away
        // every other beat, and throwing away the wrong other one puts every bar line in the record
        // on a beat that is no longer there, and then nothing downstream can count a bar at all.
        var reading = BeatModelDecoder.Counted(Curve(3000, bpm, between: between), Bars(3000, bpm));

        foreach (double bar in reading.DownbeatTimes)
            Assert.Contains(bar, reading.BeatTimes);
    }

    [Theory]
    [MemberData(nameof(Records))]
    public void The_beats_stay_in_order_and_evenly_spaced(double bpm, float between)
    {
        // Doubling inserts a beat between every pair and halving drops every other one, and both are
        // easy to write in a way that leaves a grid that steps backwards or stutters.
        var beats = BeatModelDecoder.Counted(Curve(3000, bpm, between: between), Bars(3000, bpm)).BeatTimes;

        Assert.True(beats.Count >= 8);
        double period = Period(beats);
        for (int i = 1; i < beats.Count; i++)
        {
            Assert.True(beats[i] > beats[i - 1], $"beat {i} is not after beat {i - 1}");
            Assert.InRange(beats[i] - beats[i - 1], period * 0.5, period * 2.5);
        }
    }

    [Fact]
    public void A_record_with_nothing_between_its_beats_is_left_exactly_as_it_was()
    {
        // A clean record has no maybes to count, and the check must not invent a tempo out of a
        // record's own silence.
        foreach (double bpm in new[] { 96.0, 128.0, 174.0 })
        {
            var beat = Curve(3000, bpm);
            var bars = Bars(3000, bpm);

            Assert.Equal(BeatModelDecoder.Bars(beat, bars).BeatTimes,
                         BeatModelDecoder.Counted(beat, bars).BeatTimes);
        }
    }

    [Fact]
    public void A_reading_too_short_to_have_a_tempo_is_not_moved()
    {
        // Two frames is not a record and there is no octave to be right or wrong about, so whatever
        // the reader made of it comes back untouched rather than doubled into nonsense.
        Assert.Empty(BeatModelDecoder.Counted([], []).BeatTimes);
        Assert.Equal(BeatModelDecoder.Bars([1f, 2f], [1f, 2f]).BeatTimes,
                     BeatModelDecoder.Counted([1f, 2f], [1f, 2f]).BeatTimes);
    }
}
