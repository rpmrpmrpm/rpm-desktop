using System;
using System.ComponentModel;
using RPM.Desktop.Presentation.RPaMp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The now-playing seek bar. It sits under the album name in the art chrome. It reads the shared
/// <see cref="RPaMpModel"/> (set as its DataContext) for the live progress from 0 to 1 and the "-mm:ss"
/// remaining time. The user seeks by pressing or dragging the knob anywhere along the strip. It is hidden,
/// and does not take pointer input, whenever there is nothing to seek.</summary>
public sealed partial class SeekBar : UserControl
{
    // Knob heights. At rest the knob spans the strip, with its foot on the art's foot. It is a little
    // taller on hover and during a drag.
    private const double KnobRest = 14;
    private const double KnobHover = 16;

    private RPaMpModel? _model;
    private bool _dragging;
    private double _dragFrac;

    public SeekBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookModel();
        Loaded += (_, _) => { HookModel(); UpdateAll(); };
        Unloaded += (_, _) => Unhook();
    }

    private void HookModel()
    {
        var next = DataContext as RPaMpModel;
        if (ReferenceEquals(next, _model))
            return;
        Unhook();
        _model = next;
        if (_model is not null)
            _model.PropertyChanged += OnModelChanged;
        UpdateAll();
    }

    private void Unhook()
    {
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RPaMpModel.Progress) or nameof(RPaMpModel.RemainingText)
            or nameof(RPaMpModel.ScrubberEnabled) or nameof(RPaMpModel.ScrubberVisible)
            or nameof(RPaMpModel.Duration)
            or nameof(RPaMpModel.Position))
            UpdateAll();
    }

    /// <summary>Whether this bar can take keyboard focus and be stepped with Left and Right (see
    /// <see cref="StepBy"/>). It is off by default, so the bars in the main window stay out of its tab
    /// order. Pegasus turns it on for its own bars. A bar that takes keys is a tab stop only while it is
    /// shown and can seek. So focus never lands on a bar that is hidden or refuses to move.
    /// </summary>
    public bool TakesKeys
    {
        get => _takesKeys;
        set
        {
            _takesKeys = value;
            UpdateAll();
        }
    }

    private bool _takesKeys;

    /// <summary>Refreshes the visibility, the remaining-time readout and the marks from the model.
    ///
    /// Every assignment here is guarded. Position notifications arrive about 2.5 times a second while
    /// playing, but the readout changes only once a second. Setting the same text again still invalidates
    /// the text measure. That starts a full layout pass for the window, which stalled frame pacing for
    /// about 400 ms. Writing only what has changed keeps each tick cheap.</summary>
    private void UpdateAll()
    {
        // Shown and draggable are two different questions. A DJ mix has a position worth watching but
        // nothing to seek into. So the bar stays up and keeps filling while it refuses the pointer. Hiding
        // it would also hide the readout.
        var shown = _model?.ScrubberVisible == true;
        var enabled = _model?.ScrubberEnabled == true;
        double opacity = shown ? 1 : 0;
        if (Root.Opacity != opacity) Root.Opacity = opacity;
        if (Root.IsHitTestVisible != enabled) Root.IsHitTestVisible = enabled;

        var tabStop = _takesKeys && shown && enabled;
        if (IsTabStop != tabStop) IsTabStop = tabStop;

        if (!Previewing && _model is not null)
        {
            var remaining = _model.RemainingText;
            if (RemainText.Text != remaining) RemainText.Text = remaining;
        }

        Layout();
    }

    private double _lastX = double.NaN, _lastW, _lastH;
    private ScaleTransform? _elapsed;      // elapsed run, scaled not resized
    private TranslateTransform? _knob;     // playhead, translated not repositioned

    private void Bar_SizeChanged(object sender, SizeChangedEventArgs e) => Layout();

    /// <summary>Sets how far the remaining-time readout is inset from the bar's right edge. The host sets
    /// this so the time clears the spinning record's shadow. The time sits higher than the track, where the
    /// disc bulges past the bar's own right edge. The lift that puts it beside the album name is
    /// kept.</summary>
    public void SetRemainInset(double px) => RemainText.Margin = new Thickness(0, -24, Math.Max(0, px), 0);

    /// <summary>Whether the remaining-time readout is shown. It sits twenty-four pixels above the strip,
    /// beside the album name. That works when the album name is on the left in the open chrome. It collides
    /// when the drawer is parked and the track's three lines have moved onto the bar. When parked, the
    /// words win. They say what is playing, and the strip still shows how far through it is.
    /// </summary>
    public bool ShowRemaining
    {
        get => RemainText.Visibility == Visibility.Visible;
        set => RemainText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Places the track, the elapsed run and the knob for the current fraction. While a scrub is
    /// in progress, the fraction is the drag preview. Otherwise it is the model's live progress.
    ///
    /// The playhead moves by TRANSFORM, never by size or Canvas position. Writing Width or Canvas.Left on
    /// each position tick invalidates measure and arrange. That layout pass runs often enough to cost the
    /// visualiser a frame on every tick. A transform only affects composition. So the playhead still moves
    /// on every tick, but the layout tree is never dirtied. The geometry below does touch layout, so it is
    /// written only when the bar itself resizes.</summary>
    private void Layout()
    {
        var w = Bar.ActualWidth;
        var h = Bar.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        if (w != _lastW || h != _lastH)
        {
            _lastW = w; _lastH = h;
            _lastX = double.NaN;   // geometry moved: re-apply the playhead below whatever it reads now

            // The knob's foot is flush with the foot of the bar, which sits on the foot of the album art.
            // The line runs through the centre of the knob.
            var knobTop = h - KnobRest;
            var lineTop = knobTop + KnobRest / 2 - TrackLine.Height / 2;

            Canvas.SetLeft(TrackLine, 0);
            Canvas.SetTop(TrackLine, lineTop);
            TrackLine.Width = w;

            // The full-width line is scaled down to the elapsed fraction. Scaling is cheap. Resizing is
            // not.
            Canvas.SetLeft(ElapsedLine, 0);
            Canvas.SetTop(ElapsedLine, lineTop);
            ElapsedLine.Width = w;
            ElapsedLine.RenderTransform = _elapsed ??= new ScaleTransform();

            Canvas.SetLeft(Knob, -Knob.Width / 2);   // The transform carries it from here.
            // Grows upward on hover. The foot stays on the foot of the art.
            Canvas.SetTop(Knob, h - Knob.Height);
            Knob.RenderTransform = _knob ??= new TranslateTransform();
        }

        var frac = Math.Clamp(Previewing ? _dragFrac : (_model?.Progress ?? 0), 0, 1);
        var x = frac * w;

        // Sub-pixel moves are invisible. Skipping them keeps even the transform writes off the quiet ticks.
        if (Math.Abs(x - _lastX) < 0.5)
            return;
        _lastX = x;

        if (_elapsed is not null) _elapsed.ScaleX = frac;
        if (_knob is not null) _knob.X = x;
    }

    // ── scrub ──

    private double FracFromPointer(PointerRoutedEventArgs e)
    {
        var px = e.GetCurrentPoint(Bar).Position.X;
        return Bar.ActualWidth > 0 ? Math.Clamp(px / Bar.ActualWidth, 0, 1) : 0;
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_model?.ScrubberEnabled != true)
            return;
        _keyTo = double.NaN;   // the pointer takes over from a key preview that was never committed
        _dragging = true;
        _dragFrac = FracFromPointer(e);
        Root.CapturePointer(e.Pointer);
        Knob.Height = KnobHover;
        ShowPreview();
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragFrac = FracFromPointer(e);
        ShowPreview();
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);
        Knob.Height = KnobRest;
        if (_model is not null)
            _model.SeekTo(_dragFrac * _model.Duration, "the seek bar, released");
        UpdateAll();
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Knob.Height = KnobHover;
        Layout();
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
            return;
        Knob.Height = KnobRest;
        Layout();
    }

    // ── keys ──
    //
    // Left and Right on a remote, passed on by Pegasus (PegasusChrome.Adjust). A key press moves the knob
    // and the countdown only. The seek is made when the key is released (CommitStep). A held key repeats
    // about thirty times a second. A seek on each repeat would restart the decoder thirty times a second.

    /// <summary>Where the keys have put the playhead, in seconds. NaN while no key preview is
    /// showing.</summary>
    private double _keyTo = double.NaN;

    /// <summary>The record on the deck when the key preview started. A preview is committed only to the
    /// record it was made on.</summary>
    private object? _keyFor;

    /// <summary>Whether the knob is showing a preview (from the pointer or the keys) instead of the
    /// playhead.</summary>
    private bool Previewing => _dragging || !double.IsNaN(_keyTo);

    /// <summary>Moves the preview by <paramref name="seconds"/> from where it is, or from the playhead when
    /// there is no preview. It returns false when nothing moved. That happens when the bar cannot seek, or
    /// when the preview is already at that end.
    /// </summary>
    public bool StepBy(double seconds)
    {
        if (_dragging || _model is not { ScrubberEnabled: true } model || model.Duration <= 0)
            return false;

        if (!ReferenceEquals(_keyFor, model.Deck))
            _keyTo = double.NaN;   // a preview left over from another record

        var from = double.IsNaN(_keyTo) ? model.Position : _keyTo;
        var to = Math.Clamp(from + seconds, 0, model.Duration);
        if (Math.Abs(to - from) < 0.01)
            return false;

        _keyTo = to;
        _keyFor = model.Deck;
        _dragFrac = to / model.Duration;
        Knob.Height = KnobHover;
        ShowPreview();
        return true;
    }

    /// <summary>Called when the key is released. It seeks to the preview, if there is one and the record it
    /// was made on is still on the deck. Otherwise the preview is dropped.</summary>
    public void CommitStep()
    {
        if (double.IsNaN(_keyTo))
            return;

        var to = _keyTo;
        var sameRecord = _model is not null && ReferenceEquals(_keyFor, _model.Deck);
        _keyTo = double.NaN;
        _keyFor = null;
        Knob.Height = KnobRest;

        if (sameRecord)
            _model!.SeekTo(to, "the seek bar, arrow keys");
        UpdateAll();
    }

    /// <summary>While dragging, the knob and the countdown follow the drag fraction, not the model. The
    /// playhead follows the pointer and makes the seek only on release.</summary>
    private void ShowPreview()
    {
        if (_model is not null)
        {
            var rem = TimeSpan.FromSeconds(Math.Max(0, _model.Duration * (1 - _dragFrac)));
            RemainText.Text = $"-{(int)rem.TotalMinutes:00}:{rem.Seconds:00}";
        }
        Layout();
    }
}
