using System.Diagnostics;
using RPM.Core.Recording;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs.Controls;

/// <summary>A stereo peak and RMS level meter, driven by <see cref="MeterFrame"/> values from a live
/// capture. The fill shows RMS. A bright marker holds the recent peak and falls back over time. The dot
/// turns red and stays lit when the signal clips. A user watches these readings before recording a
/// vinyl record.
/// </summary>
public sealed partial class PeakMeterControl : UserControl
{
    // The visible span in dBFS: -60 (bottom) to 0 (full scale).
    private const float MinDb = -60f;

    private readonly PeakHold _holdL = new();
    private readonly PeakHold _holdR = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;

    private static readonly Brush ClipOn = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x4D, 0x4D));
    private static readonly Brush ClipOff = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private bool _clippedL, _clippedR;

    public PeakMeterControl()
    {
        InitializeComponent();
    }

    private static double Fraction(float db) => Math.Clamp((db - MinDb) / (0f - MinDb), 0, 1);

    /// <summary>Colour ramp for the fill: frosted white up to the safe range, amber close to 0 dB, and
    /// red above that. Cyan is not used, to match the frosted buttons and steps.</summary>
    private static Brush FillBrush(float peakDb) => new SolidColorBrush(
        peakDb >= -1f ? Color.FromArgb(0xFF, 0xFF, 0x4D, 0x4D)
        : peakDb >= -6f ? Color.FromArgb(0xFF, 0xFF, 0xD4, 0x66)
        : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    /// <summary>Passes a new reading to the meter. Call it on the UI thread only.</summary>
    public void Update(MeterFrame frame)
    {
        if (frame.Channels.Count == 0) return;

        double now = _clock.Elapsed.TotalSeconds;
        double elapsed = now - _lastTick;
        _lastTick = now;

        var left = frame.Channels[0];
        var right = frame.Channels.Count > 1 ? frame.Channels[1] : frame.Channels[0];

        Apply(LTrack, LFill, LPeak, LClip, left, _holdL, elapsed, ref _clippedL);
        Apply(RTrack, RFill, RPeak, RClip, right, _holdR, elapsed, ref _clippedR);
    }

    private static void Apply(Border track, Rectangle fill, Rectangle peak, Border clip,
        ChannelLevel level, PeakHold hold, double elapsed, ref bool latched)
    {
        double w = track.ActualWidth;
        if (w <= 0) return;

        fill.Width = w * Fraction(level.RmsDb);
        fill.Fill = FillBrush(level.PeakDb);

        float holdDb = hold.Update(level.PeakDb, elapsed);
        double px = Math.Clamp(w * Fraction(holdDb) - 1, 0, Math.Max(0, w - 2));
        peak.Margin = new Thickness(px, 0, 0, 0);

        if (level.Clipped) latched = true;    // Once set, the clip indicator stays on until Reset is called
        clip.Background = latched ? ClipOn : ClipOff;
    }

    /// <summary>Sets the bars to zero and clears the clip indicators.</summary>
    public void Reset()
    {
        _holdL.Reset();
        _holdR.Reset();
        _clippedL = _clippedR = false;
        LFill.Width = RFill.Width = 0;
        LPeak.Margin = RPeak.Margin = new Thickness(0);
        LClip.Background = RClip.Background = ClipOff;
    }
}
