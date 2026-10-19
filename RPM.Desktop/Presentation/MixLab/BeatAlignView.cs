#if DEBUG
#if RPM_SKIA
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Analysis;
using RPM.Core.Mixing;
using RPM.Core.Splitting;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>
/// The two records under one needle, zoomed in to about two bars. The deck playing is on top, the deck
/// coming in is below, and the same instant of time runs down the middle of both.
///
/// <para>The decks above cannot show this. On a whole record across a strip, one pixel covers several
/// hundred milliseconds. The question this answers is whether the two grids fall on the same beat right
/// now, and that needs a much finer scale. Two records a fiftieth of a second apart make a flam that
/// anybody can hear, and nobody can see it on the full-size picture.</para>
///
/// <para>Both lanes are drawn on ONE clock, the mix's. Each deck has its own seconds. The record coming
/// in is played at a pulled speed, so one of its seconds is not one second of the mix. If each deck
/// were drawn against its own ruler, two grids could look as if they agree while the room hears them
/// apart. So each record's beats are placed by where they fall in the sound that leaves the
/// speakers.</para>
/// </summary>
public sealed partial class BeatAlignView : Grid
{
    /// <summary>How much of the mix is on screen: two bars at 120 BPM. At this width one millimetre is
    /// about a fifth of a second, so a beat out of place is easy to see. The beats before and after it
    /// are also on screen, which gives context.</summary>
    private const double SpanSeconds = 4;

    private const float LaneGap = 2;
    private const float RulerHeight = 12;

    private readonly MixLabSurface? _surface;

    private readonly SKPaint _fill = new() { IsAntialias = true };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _smooth = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKFont _tiny = new() { Size = 10 };

    private Lane _top, _bottom;
    private double _span = SpanSeconds;

    /// <summary>The changes the page made to the two needles before it handed them over. They are shown
    /// so that the picture can be checked. See <see cref="ShowWorkings"/>.</summary>
    private double _lag, _liveRaw = -1, _cuedRaw = -1;

    /// <summary>One deck's half of the picture: the record it is playing, where its needle is, and the
    /// speed it is playing at.</summary>
    private readonly record struct Lane(
        string Name, MixTrack? Track, WavePeaks? Peaks, double At, double Ratio, bool Live)
    {
        /// <summary>Whether there is enough here to draw anything about.</summary>
        public bool Has => Track is not null && At >= 0;
    }

    public BeatAlignView()
    {
        // The names are set here, so they are never null. A Lane is a struct. Until Show is first
        // called, both lanes are default(Lane) with a null name. The render loop can run before
        // anything has been handed to it, and that null name reached SKCanvas.DrawText on the very
        // first frame.
        _top = new Lane("Playing", null, null, -1, 1, true);
        _bottom = new Lane("Coming in", null, null, -1, 1, false);

        MinHeight = 150;
        if (SKCanvasElement.IsSupportedOnCurrentPlatform())
        {
            _surface = new MixLabSurface { Draw = Render };
            Children.Add(_surface);
        }

        SizeChanged += (_, _) => _surface?.Invalidate();
        Unloaded += (_, _) =>
        {
            _fill.Dispose();
            _stroke.Dispose();
            _smooth.Dispose();
            _tiny.Dispose();
        };
    }

    /// <summary>How wide a window is on screen, in seconds of the mix.</summary>
    public double Span
    {
        get => _span;
        set
        {
            double want = Math.Clamp(value, 0.5, 32);
            if (Math.Abs(want - _span) < 1e-6)
                return;
            _span = want;
            _surface?.Invalidate();
        }
    }

    /// <summary>
    /// Both decks at once, from the render loop.
    /// </summary>
    /// <param name="liveAt">Where the live deck's needle is, in that record's own seconds.</param>
    /// <param name="liveRatio">What the live deck is pulled to, so its seconds can be turned into the
    /// mix's.</param>
    public void Show(
        MixTrack? live, WavePeaks? livePeaks, double liveAt, double liveRatio,
        MixTrack? cued, WavePeaks? cuedPeaks, double cuedAt, double cuedRatio)
    {
        _top = new Lane("Playing", live, livePeaks, liveAt, liveRatio <= 0 ? 1 : liveRatio, true);
        _bottom = new Lane("Coming in", cued, cuedPeaks, cuedAt, cuedRatio <= 0 ? 1 : cuedRatio, false);
        _surface?.Invalidate();
    }

    /// <summary>
    /// The numbers behind the reading above: each deck's needle as the booth reported it, and the
    /// output lag that was taken off it.
    ///
    /// <para>They are shown here because a gap on this strip can have two causes that look the same on
    /// the picture. Either the decks really are apart, or the two needles were corrected by different
    /// amounts before they were drawn. The lag is converted into each record's own seconds, using that
    /// record's own pull. A deck that the page thinks is not playing is not corrected at all. A gap
    /// caused by the mix needs a fix to the mix. A gap caused by the drawing needs a fix to the
    /// drawing. Showing the numbers saves guessing which one it is.</para>
    /// </summary>
    public void ShowWorkings(double lagSeconds, double liveRaw, double cuedRaw)
    {
        _lag = lagSeconds;
        _liveRaw = liveRaw;
        _cuedRaw = cuedRaw;
    }

    // ── the ink ───────────────────────────────────────────────────────────────

    private static readonly SKColor Yellow = new(0xF2, 0xC0, 0x4C);
    private static readonly SKColor Green = new(0x4C, 0xD9, 0x8A);
    private static readonly SKColor Wave = new(0x6F, 0xA8, 0xD8);
    private static readonly SKColor Dim = new(0x8A, 0x90, 0x9A);

    private void Render(SKCanvas canvas, Size area)
    {
        float w = (float)area.Width, h = (float)area.Height;
        canvas.Clear(new SKColor(0x14, 0x16, 0x1A));
        if (w < 8 || h < 8)
            return;

        float lane = (h - RulerHeight - LaneGap) / 2;
        if (lane < 8)
            return;

        DrawLane(canvas, _top, w, RulerHeight, lane);
        DrawLane(canvas, _bottom, w, RulerHeight + lane + LaneGap, lane);
        DrawRuler(canvas, w, h);

        // The needle is drawn once, across both lanes, and not once per lane. The two decks share it.
        // The question here is what each record is doing at the SAME instant. A playhead per lane would
        // make the two lanes look like two separate pictures.
        _smooth.Color = SKColors.White;
        _smooth.StrokeWidth = 1.5f;
        canvas.DrawLine(w / 2, 0, w / 2, h, _smooth);

        DrawGap(canvas, w);
        DrawWorkings(canvas, w, h);
    }

    /// <summary>The reading's own inputs, small, along the bottom. See
    /// <see cref="ShowWorkings"/>.</summary>
    private void DrawWorkings(SKCanvas canvas, float w, float h)
    {
        if (_liveRaw < 0 || !_top.Has)
            return;

        // The same gap, worked out from the needles BEFORE the lag was taken off them. If this reads
        // zero and the gap above does not, the mix is fine and the correction is at fault.
        var rawTop = _top with { At = _liveRaw };
        var rawBottom = _bottom with { At = _cuedRaw };

        string raw = "raw n/a";
        if (_cuedRaw >= 0 && Nearest(rawTop) is { } a && Nearest(rawBottom) is { } b && Beat(rawTop) is { } beat && beat > 0)
        {
            double gap = b - a;
            gap -= Math.Round(gap / beat) * beat;
            raw = $"raw {gap * 1000:+0;-0} ms";
        }

        string text =
            $"{raw}   lag {_lag * 1000:F0} ms   playing {_top.At:F3} x{_top.Ratio:F4}   " +
            $"coming in {(_bottom.Has ? _bottom.At.ToString("F3") : "-")} x{_bottom.Ratio:F4}";

        _fill.Color = Dim;
        canvas.DrawText(text, 4, h - 3, SKTextAlign.Left, _tiny, _fill);
    }

    /// <summary>
    /// One deck: its waveform, its beats and its bar lines. All of them are placed by where they fall
    /// in the MIX, not in the record.
    /// </summary>
    private void DrawLane(SKCanvas canvas, Lane deck, float w, float top, float height)
    {
        _fill.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x06);
        canvas.DrawRect(0, top, w, height, _fill);

        _fill.Color = Dim;
        canvas.DrawText(deck.Name ?? "", 4, top + 11, SKTextAlign.Left, _tiny, _fill);

        if (!deck.Has || deck.Track?.Analysis is not { } analysis)
        {
            _fill.Color = Dim;
            canvas.DrawText(
                deck.Track is null ? "nothing on the deck" : "no grid",
                w / 2 + 6, top + height / 2, SKTextAlign.Left, _tiny, _fill);
            return;
        }

        // Half the window on each side of the needle, in the RECORD's own seconds. The record plays at
        // `Ratio`, so one second of the mix is `Ratio` seconds of the record.
        double half = _span / 2 * deck.Ratio;
        double from = deck.At - half, to = deck.At + half;

        DrawWave(canvas, deck, from, to, w, top, height);

        // The grid. Bar lines run the full height and have letters. Beats are short. The line under the
        // needle is brighter than the rest, because the other lane is compared with it.
        int perBar = Math.Max(1, analysis.BeatsPerBar);
        var beats = analysis.BeatTimes;

        for (int i = 0; i < beats.Count; i++)
        {
            double at = beats[i];
            if (at < from) continue;
            if (at > to) break;

            float x = X(at, from, to, w);
            int fromDownbeat = i - analysis.FirstDownbeat;
            bool bar = fromDownbeat >= 0 && fromDownbeat % perBar == 0;

            _stroke.Color = bar ? Yellow.WithAlpha(0xDD) : new SKColor(0xFF, 0xFF, 0xFF, 0x44);
            _stroke.StrokeWidth = bar ? 2 : 1;
            canvas.DrawLine(x, bar ? top : top + height * 0.62f, x, top + height, _stroke);
        }
    }

    private void DrawWave(SKCanvas canvas, Lane deck, double from, double to, float w, float top, float height)
    {
        if (deck.Peaks is not { Count: > 0 } peaks)
            return;

        _fill.Color = Wave.WithAlpha(deck.Live ? (byte)0xCC : (byte)0x99);
        _fill.IsAntialias = false;

        float middle = top + height / 2, halfHigh = height / 2 - 2;
        double step = (to - from) / Math.Max(1, w);

        for (int px = 0; px < (int)w; px++)
        {
            double a = from + px * step, b = a + step;
            if (!peaks.Range(a, b, out float lo, out float hi))
                continue;

            float y0 = middle - Math.Clamp(hi, -1, 1) * halfHigh;
            float y1 = middle - Math.Clamp(lo, -1, 1) * halfHigh;
            canvas.DrawRect(px, y0, 1, Math.Max(1, y1 - y0), _fill);
        }

        _fill.IsAntialias = true;
    }

    /// <summary>The mix's own clock along the top, zeroed on the needle, so that the gap below can be
    /// read against a scale.</summary>
    private void DrawRuler(SKCanvas canvas, float w, float h)
    {
        _stroke.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x22);
        _stroke.StrokeWidth = 1;
        _fill.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x44);

        // A tick every tenth of a second while there is room for them, otherwise every half second.
        double step = _span / w > 0.01 ? 0.5 : 0.1;
        for (double at = -_span / 2; at <= _span / 2; at += step)
        {
            float x = w / 2 + (float)(at / _span * w);
            canvas.DrawLine(x, 0, x, 4, _stroke);
            if (Math.Abs(at) > 1e-9 && Math.Abs(at % (step * 5)) < step / 2)
                canvas.DrawText($"{at:+0.0;-0.0}", x + 2, 10, SKTextAlign.Left, _tiny, _fill);
        }
    }

    /// <summary>
    /// How far apart the two records' nearest beats are, in milliseconds, shown as a number.
    ///
    /// <para>This number is the reason the picture exists. Fifteen milliseconds is where two kicks stop
    /// sounding like one (see <see cref="TransitionPlanner.MaxDriftSeconds"/>). So the reading turns
    /// from green to amber at that point.</para>
    /// </summary>
    private void DrawGap(SKCanvas canvas, float w)
    {
        if (Nearest(_top) is not { } a || Nearest(_bottom) is not { } b)
            return;

        // Both are already in the mix's seconds, measured from each deck's own needle. The difference
        // is what the room hears as one kick or two.
        //
        // The difference is wrapped into half a beat either side, because a beat repeats. Each lane
        // reports the distance to its own NEXT beat. Two records can be in step while their needles
        // fall on either side of a beat (one just before its beat, the other just after its own). The
        // raw distance is then almost a whole beat. Without the wrap, the picture would call the mix
        // badly wrong at the moment it is most nearly right.
        double apart = b - a;
        if (Beat(_top) is { } beat && beat > 0)
            apart -= Math.Round(apart / beat) * beat;

        string text = $"{apart * 1000:+0;-0} ms";

        bool tight = Math.Abs(apart) <= TransitionPlanner.MaxDriftSeconds;
        float width = _tiny.MeasureText(text);
        float left = w / 2 + 8;

        _fill.Color = new SKColor(0, 0, 0, 0xCC);
        canvas.DrawRoundRect(new SKRect(left, 2, left + width + 8, 16), 3, 3, _fill);
        _fill.Color = tight ? Green : Yellow;
        canvas.DrawText(text, left + 4, 12.5f, SKTextAlign.Left, _tiny, _fill);
    }

    /// <summary>How long one beat of this deck lasts, in the MIX's seconds. The gap above is wrapped by
    /// this length. It is measured from the grid on each side of the needle, not from the track's BPM.
    /// So a record with a bent grid is wrapped by the beat it is really playing.</summary>
    private static double? Beat(Lane deck)
    {
        if (!deck.Has || deck.Track?.Analysis is not { BeatTimes.Count: > 1 } analysis)
            return null;

        var beats = analysis.BeatTimes;
        for (int i = 0; i < beats.Count - 1; i++)
            if (beats[i + 1] >= deck.At)
                return (beats[i + 1] - beats[i]) / deck.Ratio;

        return (beats[^1] - beats[^2]) / deck.Ratio;
    }

    /// <summary>Where this deck's nearest beat falls relative to its own needle, in seconds of the mix.
    /// Null when the deck has no grid to read.</summary>
    private static double? Nearest(Lane deck)
    {
        if (!deck.Has || deck.Track?.Analysis is not { BeatTimes.Count: > 0 } analysis)
            return null;

        double best = double.MaxValue;
        foreach (double at in analysis.BeatTimes)
        {
            double gap = at - deck.At;
            if (Math.Abs(gap) < Math.Abs(best))
                best = gap;
            if (gap > Math.Abs(best))
                break;                          // sorted: past the nearest one
        }

        return best == double.MaxValue ? null : best / deck.Ratio;
    }

    private static float X(double at, double from, double to, float w) =>
        (float)((at - from) / Math.Max(1e-9, to - from) * w);
}
#endif
#endif
