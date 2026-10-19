#if DEBUG
#if RPM_SKIA
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Analysis;
using RPM.Core.Mixing;
using Windows.UI;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>
/// One deck of the workbench: what is on it, its timeline, its own transport and its own full set of
/// edit controls. Both decks are this class. The record playing and the record coming in are equal
/// citizens, so both can be edited.
/// </summary>
public sealed partial class MixDeckView : StackPanel
{
    /// <summary>The picture. The page drives its playhead from the frame clock and its content from the
    /// slow tick; the view only builds and arranges it.</summary>
    public MixLabTimeline Timeline { get; } = new();

    private readonly TextBlock _name = Label(12, bold: true, dim: true);
    private readonly TextBlock _title = Label(14, bold: true);
    // The corner of the deck shows the tempo it is running at, and what the deck is doing. Size and
    // colour tell these apart, so the reader does not need to read a sentence. A DJ glances at this
    // while a record plays.
    private readonly TextBlock _tempo = Label(17, bold: true);
    private readonly TextBlock _unit = Label(9, dim: true);
    private readonly TextBlock _pitch = Label(11, dim: true);

    /// <summary>The tempo this deck is heading for once the changeover runs. The outgoing deck is moved
    /// to it over four bars, not in one step. So before a mix starts, the two decks show different
    /// numbers, and both numbers are correct. This shows that they are heading for the same
    /// tempo.</summary>
    private readonly TextBlock _target = Label(11, dim: true);

    /// <summary>Shown when the two records are further apart in tempo than the pitch faders can close.
    /// The changeover will then run with the two grids drifting apart. This is not a fault. It is what
    /// a beat-match between records that far apart means. It is useful to know this before the mix
    /// starts.</summary>
    private readonly TextBlock _apart = new()
    {
        FontFamily = Fa.Face,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xB3, 0x39)),
    };
    private readonly TextBlock _lamp = new()
    {
        Text = "\uf111",                       // fa-circle
        FontFamily = Fa.Face,
        FontSize = 8,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _state = Label(11, dim: true);

    private readonly Button _play;
    private readonly Button _pause;
    private readonly Button _cue;
    private readonly Button _reject;
    private readonly TextBox _bpm = Condensed.Box(84);

    /// <summary>Shown beside the tempo when the analyser is not sure enough to mix on the record. The
    /// record is still played and still faded. It is just not beat-matched. So this is a note about
    /// what the changeover will be, not a fault.</summary>
    private readonly TextBlock _bpmDoubt = new()
    {
        // fa-triangle-exclamation, written as an escape. A raw private-use character is damaged by the
        // tools that edit this file.
        Text = "\uf071",
        FontFamily = Fa.Face,
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xB3, 0x39)),
    };
    private readonly ComboBox _perBar = Condensed.Drop(64);
    private readonly List<Control> _editable = [];
    private readonly Dictionary<MixLabEdit, RadioButton> _modes = [];

    /// <summary>The row-sized radio: a small ring beside a label left at reading size. See
    /// CompactRadioStyle in Generic.xaml for why it is a template rather than a scale.</summary>
    private static Style? Compact => Condensed.Radio;
    private string _bpmShownFor = "";

    /// <summary>One of the deck's two transport keys was pressed: true for play, false for pause. What
    /// either means (hold the live deck, or audition the cued one) belongs to the page, which knows
    /// which record this is.</summary>
    public event Action<bool>? TransportAsked;

    /// <summary>Take this deck's needle to its mix point.</summary>
    public event Action? CueAsked;

    /// <summary>Put the record cued on this deck back and pick another one.</summary>
    public event Action? RejectAsked;

    /// <summary>Forwarded from the timeline: a click that was not a handle.</summary>
    public event Action<double>? Scrubbed;

    /// <summary>Forwarded from the timeline, with which deck implied by which view raised it.</summary>
    public event Action<MixLabHandle, double>? MarkerMoved;

    public event Action<MixLabHandle>? MarkerDropped;

    /// <summary>A beat on this deck's record was clicked and named the one.</summary>
    public event Action<double>? DownbeatPicked;

    /// <summary>A new tempo or count was typed for this record.</summary>
    public event Action<double, int>? RegridAsked;

    /// <summary>The grid's octave, halved or doubled.</summary>
    public event Action<double>? OctaveAsked;

    /// <summary>The whole grid slid by this many seconds.</summary>
    public event Action<double>? NudgeAsked;

    public MixDeckView(string name)
    {
        Spacing = 6;
        _name.Text = name.ToUpperInvariant();

        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_name, 0);
        header.Children.Add(_name);
        Grid.SetColumn(_title, 1);
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(_title);
        // Tempo big, unit small under its shoulder, then the lamp and the word for what the deck is
        // doing. Right-aligned as one block so the two decks' tempi line up under each other.
        var corner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _unit.Text = "BPM";
        _unit.Margin = new Thickness(0, 4, 0, 0);
        corner.Children.Add(_tempo);
        corner.Children.Add(_unit);
        corner.Children.Add(_pitch);
        corner.Children.Add(_target);
        corner.Children.Add(_apart);
        corner.Children.Add(new Border { Width = 8 });
        corner.Children.Add(_lamp);
        corner.Children.Add(_state);

        Grid.SetColumn(corner, 2);
        header.Children.Add(corner);
        Children.Add(header);

        Children.Add(new Border { Height = 158, CornerRadius = new CornerRadius(4), Child = Timeline });

        Timeline.Scrubbed += at => Scrubbed?.Invoke(at);
        Timeline.Moved += (handle, at) => MarkerMoved?.Invoke(handle, at);
        Timeline.Dropped += handle => MarkerDropped?.Invoke(handle);
        Timeline.DownbeatPicked += at => DownbeatPicked?.Invoke(at);

        // ── transport ──
        //
        // The icons use Font Awesome, the app's own icon font. See the FaIcon style in Generic.xaml. Do
        // not use a Windows system font for them. Such a font is not installed everywhere, and then the
        // platform picks a different glyph for each key.
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        // Play and pause are two keys, not one key that changes its icon. A key that swaps its icon
        // shows what a press would do. Two keys show what the deck is doing now, which is what you look
        // at a deck to find out.
        _play = Icon(Fa.Play, "Play this deck", () => TransportAsked?.Invoke(true));
        _pause = Icon(Fa.Pause, "Pause this deck", () => TransportAsked?.Invoke(false));
        _cue = Icon(Fa.Flag, "Go to mix point", () => CueAsked?.Invoke());
        transport.Children.Add(_play);
        transport.Children.Add(_pause);
        transport.Children.Add(_cue);

        // Only offered on the deck where the next record is cued, and only while the choice is still
        // open. It is not offered during a changeover. See MixLabPage.Refresh.
        _reject = Push("Reject", () => RejectAsked?.Invoke());
        ToolTipService.SetToolTip(_reject, "Put this record back and pick another one");
        _reject.Visibility = Visibility.Collapsed;
        transport.Children.Add(_reject);

        // The four views, in order of how much of the record they show: zoom in one step, zoom out one
        // step, frame the changeover, and show the whole record.
        transport.Children.Add(Icon(Fa.ZoomIn, "Zoom in", Timeline.ZoomIn));
        transport.Children.Add(Icon(Fa.ZoomOut, "Zoom out", Timeline.ZoomStepOut));
        transport.Children.Add(Icon(Fa.Compress, "Fit changeover", Timeline.ZoomToTransition));
        transport.Children.Add(Icon(Fa.Expand, "Fit whole track", Timeline.ZoomOut));

        // Follow the needle: the view moves along with the record. A click in the waveform stops the
        // following, because choosing where to look comes before being moved there. So the button shows
        // the state of the timeline, not only the last press on the button.
        var follow = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
        {
            Content = Glyph(Fa.Crosshairs),
            Padding = new Thickness(9, 5, 9, 5),
        };
        ToolTipService.SetToolTip(follow, "Follow playhead");
        follow.Click += (_, _) => Timeline.Follow = follow.IsChecked == true;
        Timeline.FollowChanged += on => follow.IsChecked = on;
        transport.Children.Add(follow);

        // ── what the pointer may move ──
        //
        // The five labels sit on one line beside eight keys. They are sized to fit that row. They are
        // not WinUI's own radio button, which is built for a settings page.
        transport.Children.Add(new Border { Width = 10 });
        foreach (var (mode, label) in new[]
        {
            (MixLabEdit.None, "Playhead"),
            (MixLabEdit.Grid, "Grid"),
            (MixLabEdit.MixIn, "In"),
            (MixLabEdit.MixOut, "Out"),
            (MixLabEdit.Mix, "Mix"),
        })
        {
            // The radio uses the colour its marks are drawn in. See MixLabTimeline. The waveform
            // already shows which marks a pointer mode turns on. Five radios in one accent colour would
            // repeat that fact but lose the colour, which carries the meaning.
            var ink = MixLabTimeline.InkFor(mode);

            var radio = new RadioButton
            {
                Content = label,
                GroupName = $"mixlab-edit-{name}",
                IsChecked = mode == MixLabEdit.None,
                Style = Compact,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(ink),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, ink.R, ink.G, ink.B)),
            };
            radio.Checked += (_, _) => Timeline.Editing = mode;
            _modes[mode] = radio;
            transport.Children.Add(radio);
        }

        Children.Add(transport);

        // ── the grid, by hand ──
        var grid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        grid.Children.Add(Label(12, dim: true, text: "BPM"));
        _bpm.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                Regrid();
        };
        grid.Children.Add(_bpm);
        grid.Children.Add(_bpmDoubt);
        grid.Children.Add(Push("x2", () => OctaveAsked?.Invoke(2)));
        grid.Children.Add(Push("/2", () => OctaveAsked?.Invoke(0.5)));
        grid.Children.Add(Label(12, dim: true, text: "per bar"));
        foreach (int n in new[] { 2, 3, 4, 5, 6, 7 })
            _perBar.Items.Add(n);
        _perBar.SelectionChanged += (_, _) => Regrid();
        grid.Children.Add(_perBar);

        grid.Children.Add(Label(12, dim: true, text: "downbeat"));
        grid.Children.Add(Push("-50", () => NudgeAsked?.Invoke(-0.050)));
        grid.Children.Add(Push("-10", () => NudgeAsked?.Invoke(-0.010)));
        grid.Children.Add(Push("+10", () => NudgeAsked?.Invoke(+0.010)));
        grid.Children.Add(Push("+50", () => NudgeAsked?.Invoke(+0.050)));
        grid.Children.Add(Label(12, dim: true, text: "ms"));
        Children.Add(grid);

        foreach (var child in grid.Children.OfType<Control>())
            _editable.Add(child);
    }

    /// <summary>What a deck can be doing, which is what its lamp is coloured by.</summary>
    public enum Mood { Idle, Playing, Paused, Cued, Analysing }

    /// <summary>The record's name, the tempo it is running at, and what the deck is doing.</summary>
    /// <param name="bpm">The speed the deck is playing at, pitch included. This is not the tempo the
    /// record is filed as. It moves while a changeover pulls the two decks together. Watching it move
    /// is why it is shown here.</param>
    /// <param name="pitch">The pull, as a ratio. It is shown only when there is one. A deck playing at
    /// its own speed should not show x1.000, because the eye would only skip over it.</param>
    /// <param name="apart">The rate at which the two grids will drift apart, or zero when they
    /// meet.</param>
    public void ShowHeader(
        MixTrack? track, double bpm, double pitch, double target, double apart, string state, Mood mood)
    {
        _title.Text = track?.Display ?? "No track";

        // One decimal place. The tempo in the grid box below is a whole number. That one is the tempo
        // the record is filed as, and it is read once. This one is the speed the deck is playing at
        // now. A tenth is the difference between two decks that have met and two that are still
        // closing.
        bool has = bpm > 0;
        _tempo.Text = has ? bpm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "";
        _unit.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        bool pulled = has && pitch > 0 && Math.Abs(pitch - 1) > 0.0005;
        _pitch.Text = pulled ? $"x{pitch:F3}" : "";
        _pitch.Visibility = pulled ? Visibility.Visible : Visibility.Collapsed;

        bool heading = has && target > 0 && Math.Abs(target - bpm) >= 0.05;
        _target.Text = heading
            ? $"\u2192 {target.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}"
            : "";
        _target.Visibility = heading ? Visibility.Visible : Visibility.Collapsed;

        bool walking = has && apart > 1e-6;
        _apart.Text = walking ? "\uf071" : "";
        _apart.Visibility = walking ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_apart, walking
            ? $"These records are too far apart to beat-match. The grids drift {apart:P2} through the changeover."
            : null);

        _state.Text = state;
        _lamp.Foreground = new SolidColorBrush(mood switch
        {
            Mood.Playing => Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF),   // the app's cyan: this is the record you hear
            Mood.Cued    => Color.FromArgb(0xFF, 0x3E, 0x8E, 0xD0),   // dimmer blue: ready, not sounding
            Mood.Paused  => Color.FromArgb(0xFF, 0xE8, 0xB3, 0x39),   // amber: held by hand
            Mood.Analysing => Color.FromArgb(0xFF, 0x8A, 0x90, 0x9A),  // grey: nothing is known about it yet
            _            => Color.FromArgb(0xFF, 0x4A, 0x50, 0x5A),   // grey: nothing on the deck
        });
        _lamp.Visibility = mood == Mood.Idle && !has ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What the transport keys showed last time. A tick that changes nothing then touches
    /// nothing. Setting a brush repaints the whole window on a renderer that has no dirty
    /// rectangles.</summary>
    private (bool Playing, bool Enabled, string Play, string Pause)? _transportShown;

    /// <summary>
    /// Shows the state of this deck on its two keys. The key that names what the deck is doing is the
    /// lit one.
    ///
    /// <para>Both keys work only while the deck is under the DJ's control (see MixLabPage.Refresh).
    /// Pressing the key that is already lit does nothing. That is how a pair of transport keys
    /// behaves.</para>
    /// </summary>
    public void ShowTransport(bool playing, bool enabled, string playTip, string pauseTip)
    {
        if (_transportShown == (playing, enabled, playTip, pauseTip))
            return;
        _transportShown = (playing, enabled, playTip, pauseTip);

        Light(_play, on: playing);
        Light(_pause, on: !playing);
        Reach(_play, enabled);
        Reach(_pause, enabled);
        ToolTipService.SetToolTip(_play, playTip);
        ToolTipService.SetToolTip(_pause, pauseTip);
    }

    /// <summary>
    /// Which sets of marks this deck can actually move, and whether there is anywhere to cue to.
    ///
    /// <para>A radio that would put the pointer on nothing is not offered. There is no grid to slide on
    /// a record that nobody has read. There are no changeover marks on a deck with no changeover.
    /// Nothing can be moved while a changeover is running. A drag is refused then anyway (see
    /// MixLabPage.Move). A control that accepts a drag and does nothing with it is worse than one that
    /// says it is shut.</para>
    ///
    /// <para>The mode falls back to Playhead when the mode in use stops being offered. So the pointer
    /// is never left armed for marks that are not on the deck.</para>
    /// </summary>
    /// <param name="hasMixMarks">Whether this deck has any of the changeover's own marks on it: the
    /// start and end on the record playing, and the cue point on the one coming in.</param>
    /// <param name="canEditReading">Whether this record's own grid and cue marks may be
    /// corrected.</param>
    /// <param name="canEditMix">Whether the changeover's marks may be moved. This is only true where a
    /// changeover with a shape is going to be made.</param>
    /// <param name="canCue">Whether the cue key has a point to take the needle to.</param>
    /// <param name="canReject">Whether this deck's record can be turned down. This is only true for the
    /// deck that the next record is cued on, and only while nothing is handing over. Null hides the
    /// key. The playing deck gets null, because no choice is left about the record the room is
    /// listening to.</param>
    /// <param name="atCue">Whether the needle is standing on the cue point. The key lights up for this.
    /// It shows where the deck IS, not only where it can be sent.</param>
    public void ShowEdits(
        MixTrack? track, bool canEditReading, bool canEditMix, bool hasMixMarks, bool canCue,
        bool? canReject = null, bool atCue = false)
    {
        _reject.Visibility = canReject is null ? Visibility.Collapsed : Visibility.Visible;
        Reach(_reject, canReject == true);
        Light(_cue, on: atCue && canCue);

        // There are two permissions, not one. The grid, the intro and the outro are the RECORD's
        // reading. They are worth correcting even when no changeover is made from them, because this
        // reading is what is written down and published. The mix marks belong to a changeover. If no
        // changeover is coming, there is nothing to move.
        bool read = track?.Analysis is not null;
        Offer(MixLabEdit.Grid, canEditReading && read);
        Offer(MixLabEdit.MixIn, canEditReading && read);
        Offer(MixLabEdit.MixOut, canEditReading && read);
        Offer(MixLabEdit.Mix, canEditMix && hasMixMarks);

        Reach(_cue, canCue);

        void Offer(MixLabEdit mode, bool on)
        {
            if (!_modes.TryGetValue(mode, out var radio) || radio.IsEnabled == on)
                return;

            radio.IsEnabled = on;
            if (!on && radio.IsChecked == true)
                _modes[MixLabEdit.None].IsChecked = true;
        }
    }

    /// <summary>Fills the grid boxes for this record. This happens once per record. After that, the
    /// boxes belong to whoever is typing in them.</summary>
    public void ShowGrid(MixTrack? track, bool editable)
    {
        foreach (var control in _editable)
            control.IsEnabled = editable && track?.Analysis is not null;

        if (track?.Analysis is not { } analysis)
        {
            if (track is null)
                _bpmShownFor = "";
            return;
        }

        // A reading that the analyser is not sure of is not mixed on. MixTechnique drops every
        // technique that needs a grid, and the records are blended instead. This is said here because
        // otherwise the only sign is a changeover that came out as a fade when a beat-match was
        // expected.
        bool doubted = !analysis.CanBeatMatch;
        _bpmDoubt.Visibility = doubted ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_bpmDoubt, doubted
            ? $"Tempo not certain ({analysis.BpmConfidence:0.00}). This track is blended, not beat-matched."
            : null);

        if (_bpmShownFor != track.Path)
        {
            _bpmShownFor = track.Path;
            // Whole beats, because that is the number used everywhere else a DJ reads it: the record
            // shop, the sleeve and the other deck. The grid keeps its decimals, because a beat-match is
            // timed on them. They are more precise than any number worth showing on screen.
            _bpm.Text = analysis.Bpm.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (_perBar.SelectedItem is not int per || per != analysis.BeatsPerBar)
            _perBar.SelectedItem = analysis.BeatsPerBar;
    }

    /// <summary>Makes the BPM box read the record again on the next tick. This is used after an edit
    /// has been made somewhere else.</summary>
    public void ForgetShownGrid() => _bpmShownFor = "";

    private void Regrid()
    {
        if (!double.TryParse(_bpm.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double bpm) || bpm <= 0)
            return;
        if (_perBar.SelectedItem is not int perBar)
            return;
        RegridAsked?.Invoke(bpm, perBar);
    }

    // ── small furniture ───────────────────────────────────────────────────────

    internal static Button Icon(string glyph, string tip, Action action)
    {
        var button = new Button
        {
            Content = Glyph(glyph),
            Padding = new Thickness(9, 5, 9, 5),
        };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>One icon in the app's own icon font. It is set here, and not left to the FontIcon
    /// default. The default is a Windows system font. Off Windows, every glyph is replaced by another
    /// one, so none of them is the one that was asked for.</summary>
    internal static FontIcon Glyph(string glyph) =>
        new() { Glyph = glyph, FontSize = 14, FontFamily = Fa.Face };

    /// <summary>
    /// Whether a key can be pressed, and how it shows that.
    ///
    /// <para>The opacity is the important part. A button's own Disabled state greys its content
    /// presenter's foreground. But the content of every key here is a FontIcon with its own foreground
    /// colour. That colour is set on the icon itself, which is closer to the glyph than the template
    /// is, so the disabled state never reaches the glyph. Every shut key in the row was drawn at full
    /// strength and looked like a live one. Fading the whole key with its opacity is one line of code,
    /// and nothing inside the key can override it.</para>
    /// </summary>
    internal static void Reach(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase key, bool enabled)
    {
        key.IsEnabled = enabled;
        key.Opacity = enabled ? 1 : Shut;
    }

    /// <summary>What is left of a key that cannot be pressed. Enough to read what it is, not enough to
    /// be mistaken for one that works.</summary>
    private const double Shut = 0.32;

    /// <summary>The app's own blue, for the key that says what is happening right now.</summary>
    private static readonly SolidColorBrush Lit = new(Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF));

    /// <summary>What a key looks like the rest of the time: the ordinary reading ink of this
    /// page.</summary>
    private static readonly SolidColorBrush Unlit = new(Color.FromArgb(0xFF, 0xE6, 0xE9, 0xEE));

    /// <summary>
    /// Puts a key in its active state, or takes it out of that state. The active state is the glyph in
    /// blue, shown while the thing the key controls is happening.
    ///
    /// <para>Only the glyph changes colour, not the button. These keys have no filled state of their
    /// own. A blue plate behind a dark glyph in a row of dark plates is too loud. Blue on the glyph
    /// gives the same information with less noise.</para>
    /// </summary>
    internal static void Light(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase key, bool on, string? glyph = null)
    {
        if (glyph is not null && key.Content is FontIcon icon && icon.Glyph != glyph)
            icon.Glyph = glyph;

        if (key.Content is FontIcon face)
            face.Foreground = on ? Lit : Unlit;
    }

    internal static Button Push(string text, Action action)
    {
        var button = new Button { Content = text, Style = Condensed.Button };
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock Label(double size, bool bold = false, bool dim = false, string text = "") =>
        new()
        {
            Text = text,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = new SolidColorBrush(dim
                ? Color.FromArgb(0xFF, 0x8A, 0x90, 0x9A)
                : Color.FromArgb(0xFF, 0xE6, 0xE9, 0xEE)),
        };
}
#endif
#endif
