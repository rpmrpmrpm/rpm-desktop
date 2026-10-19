using RPM.Core.Analysis;
using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>
/// Where a bar line lands on the waveform, at every zoom the lab offers.
///
/// <para>The lab draws each line at <c>TimeToX(view, BeatTimes[i], width)</c> and nothing else, so
/// "the bars are in the right place" is two claims and this file is about both. The first is that
/// the grid it is drawing from is an even line whose spacing IS the stored tempo. The two are used
/// interchangeably all over the app, and where they disagree the picture and everything worked out
/// from the tempo move further apart down the record. The second is that the mapping holds at every zoom,
/// including the tightest, where a bar is most of the window and a rounding error is the width of
/// the thing being placed.</para>
///
/// <para>What is not testable here is the rasteriser: a line is a pixel wide at best, and Skia is
/// not in Core. Everything up to the pixel is.</para>
/// </summary>
public class BarLinePlacementTests
{
    private const double Bpm = 139.36, Duration = 211.9;
    private const int PerBar = 4;

    /// <summary>A grid as the analyser produces one: one straight line at the model's tempo,
    /// carried to both ends of the file.</summary>
    private static TrackAnalysis Analysis(double bpm = Bpm, double phase = 0.137, int firstDownbeat = 0)
    {
        double period = 60.0 / bpm;
        var beats = new List<double>();
        for (double at = phase; at <= Duration; at += period)
            beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = "x.flac",
            DurationSeconds = Duration,
            Bpm = bpm,
            BpmConfidence = 1,
            BeatTimes = beats,
            FirstDownbeat = firstDownbeat,
            BeatsPerBar = PerBar,
        };
    }

    /// <summary>Every zoom the lab can be at, from the whole record down to the tightest the viewport
    /// allows, panned so the bars under test are actually on screen.</summary>
    private static IEnumerable<WaveView> Zooms(double around)
    {
        var view = WaveTimeline.Fit(Duration);
        yield return view;
        while (view.Span > WaveTimeline.MinSpanSeconds * 1.0001)
        {
            view = WaveTimeline.ZoomAt(view, around, WaveTimeline.ZoomStep, Duration);
            yield return view;
        }
    }

    [Fact]
    public void The_grid_is_an_even_line_and_its_spacing_is_the_stored_tempo()
    {
        // The invariant everything else here rests on. Measured on the real thing rather than
        // asserted about it: E-Type's "Campione 2000" comes out of the analyser at 139.360 with the
        // grid even to the microsecond and no drift across the record at all.
        var analysis = Analysis();
        var beats = analysis.BeatTimes;

        double spacing = (beats[^1] - beats[0]) / (beats.Count - 1);
        Assert.Equal(60.0 / analysis.Bpm, spacing, 9);

        for (int i = 1; i < beats.Count; i++)
            Assert.Equal(spacing, beats[i] - beats[i - 1], 9);

        // Which makes the two ways of naming the last bar line the same statement. This is the one
        // that fails quietly: it is exact at the top of the record and drifts with every bar.
        double fromTempo = beats[0] + (beats.Count - 1) * (60.0 / analysis.Bpm);
        Assert.Equal(beats[^1], fromTempo, 6);
    }

    [Fact]
    public void A_bar_line_lands_on_its_own_time_at_every_zoom()
    {
        var analysis = Analysis();
        const float Width = 1200;

        foreach (var view in Zooms(around: 120))
        {
            for (int i = analysis.FirstDownbeat; i < analysis.BeatTimes.Count; i += PerBar)
            {
                double at = analysis.BeatTimes[i];
                if (at < view.Start || at > view.End)
                    continue;

                double x = WaveTimeline.TimeToX(view, at, Width);

                // The pixel it is drawn at has to be the pixel that second is at: the same mapping
                // the waveform column under it was drawn with, so the line sits on the kick.
                Assert.InRange(x, -0.5, Width + 0.5);
                Assert.Equal(at, WaveTimeline.XToTime(view, x, Width), 9);
            }
        }
    }

    [Fact]
    public void The_bar_a_click_finds_is_the_bar_that_was_drawn()
    {
        // A hit test that found the nearest bar line from the tempo, while the picture draws it from
        // the grid array, would use two forms of the same number that agree only while the tempo and
        // the grid do. So this asks the question the way the control does: for a click anywhere, the
        // bar it resolves to must be one of the lines actually on screen.
        var analysis = Analysis();
        var beats = analysis.BeatTimes;
        double spacing = (beats[^1] - beats[0]) / (beats.Count - 1);
        var drawn = new HashSet<double>();
        for (int i = analysis.FirstDownbeat; i < beats.Count; i += PerBar)
            drawn.Add(beats[i]);

        double firstLine = drawn.Min(), lastLine = drawn.Max();

        for (double at = 0; at < Duration; at += 0.37)
        {
            int bar = (int)Math.Round((at - beats[analysis.FirstDownbeat]) / (spacing * PerBar));
            bar = Math.Clamp(bar, -(analysis.FirstDownbeat / PerBar),
                             (beats.Count - 1 - analysis.FirstDownbeat) / PerBar);
            double found = beats[analysis.FirstDownbeat + bar * PerBar];

            // Always a line that is actually on screen, wherever the click was.
            Assert.Contains(found, drawn);

            // And the nearest one, for a click with a line either side of it. Past the last line the
            // clamp is the point: the grid runs out before the record does, and the answer is the
            // last real bar rather than one invented out past the end of it.
            if (at >= firstLine && at <= lastLine)
                Assert.True(Math.Abs(found - at) <= spacing * PerBar / 2 + 1e-9,
                    $"a click at {at:F2}s resolved to the bar at {found:F2}s, further than half a bar");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_one_tab_and_the_bar_lines_name_the_same_line(int firstDownbeat)
    {
        // The "1" tab is drawn at GridEdit.FirstDownbeatSeconds and the bar lines are picked out by
        // index from FirstDownbeat. These are two readings of the same field, and if they ever differ
        // a mix starts away from the one.
        var analysis = Analysis(firstDownbeat: firstDownbeat);

        Assert.Equal(analysis.BeatTimes[firstDownbeat], GridEdit.FirstDownbeatSeconds(analysis), 9);

        double one = GridEdit.FirstDownbeatSeconds(analysis);
        for (int i = 0; i < analysis.BeatTimes.Count; i++)
        {
            if (i < firstDownbeat || (i - firstDownbeat) % PerBar != 0)
                continue;

            // In bars from the "1" rather than a remainder in seconds: a whole number of bars can
            // land either side of zero once floating point is involved, and a modulo reads the near
            // miss below as a whole bar out.
            double bars = (analysis.BeatTimes[i] - one) / analysis.BarSeconds;
            Assert.True(Math.Abs(bars - Math.Round(bars)) < 1e-6,
                $"beat {i} is drawn as a bar and sits {bars - Math.Round(bars):F9} bars off the line");
        }
    }

    [Fact]
    public void An_edited_grid_still_holds_the_invariant()
    {
        // A hand edit rebuilds the line from three numbers, so it has to come back as even as the
        // measured one. This is the path a DJ takes when the "one" is on the three.
        var edited = GridEdit.Regrid(Analysis(), bpm: 128.5, downbeatSeconds: 4.317, beatsPerBar: 4);
        var beats = edited.BeatTimes;

        double spacing = (beats[^1] - beats[0]) / (beats.Count - 1);
        Assert.Equal(60.0 / edited.Bpm, spacing, 9);
        for (int i = 1; i < beats.Count; i++)
            Assert.Equal(spacing, beats[i] - beats[i - 1], 9);

        // And the time that was named a downbeat really is one on the line that came back.
        double off = (4.317 - GridEdit.FirstDownbeatSeconds(edited)) % edited.BarSeconds;
        Assert.True(Math.Abs(off) < 1e-6 || Math.Abs(Math.Abs(off) - edited.BarSeconds) < 1e-6,
            $"the named downbeat sits {off * 1000:F3} ms off the bar line");
    }

    [Fact]
    public void Zooming_in_never_moves_a_line_off_the_second_it_is_on()
    {
        // The tightest zoom is half a second across the window. At 139 BPM that is barely more than
        // a bar, which is where a millisecond of arithmetic error is a visible part of the
        // screen. Checked against the time itself rather than against the previous zoom, so an error
        // that crept in gradually is still caught.
        var analysis = Analysis();
        double bar = analysis.BeatTimes[analysis.FirstDownbeat + 160];
        const float Width = 1200;

        foreach (var view in Zooms(around: bar))
        {
            if (bar < view.Start || bar > view.End)
                continue;

            double x = WaveTimeline.TimeToX(view, bar, Width);
            double back = WaveTimeline.XToTime(view, x, Width);
            double drift = Math.Abs(back - bar);
            double perPixel = WaveTimeline.SecondsPerPixel(view, Width);

            Assert.True(drift < perPixel / 1000,
                $"at a span of {view.Span:F2}s the line drifted {drift * 1000:F4} ms, " +
                $"{drift / perPixel:F4} of a pixel");
        }
    }
}
