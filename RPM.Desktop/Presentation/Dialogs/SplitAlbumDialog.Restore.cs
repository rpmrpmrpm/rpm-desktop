using RPM.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;   // RangeBaseValueChangedEventArgs
using Microsoft.UI.Xaml.Media;
using RPM.Core.Audio;
using RPM.Core.Recording;
using RPM.Core.Splitting;
using RPM.Desktop.Strings;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>The clean-up tool whose panel is open over the waveform.</summary>
internal enum CleanTool { None, Noise, Clicks, Tone }

/// <summary>
/// The clean-up tools: noise reduction, click removal, and bass and treble. Each is opened from the
/// Corrections menu on the waveform's toolbar. Noise reduction has four numbered steps, because the order
/// matters: select, profile, turn on, listen. Click removal is one switch. Bass and treble are two
/// sliders. All three work with their default settings. The sliders are under Show advanced. All three
/// share the play buttons, which play the chain that the cut will use.
///
/// <para>Vinyl only. A file from a disc or a download has no session folder to keep settings in, and
/// nothing that the user recorded to clean up.</para>
///
/// <para>The ten-band curve is not repeated here. It is set in the record window, against a record that
/// is playing, because that is the only way to judge it. It is stored in <c>restore.json</c>. <b>Bass and
/// treble</b> are different, for two reasons. A recorded session can never reach the record window again,
/// because that window makes a new session folder every time it opens. So tone set during recording could
/// only be corrected by editing the file by hand. Also, tone is the LAST thing the chain applies, after
/// the clicks and the hiss are gone. So this is the screen where its effect can actually be heard.</para>
///
/// <para><b>How it is drawn.</b> <see cref="RenderTools"/> works out every step's state from the
/// settings, the editor's selection and track list, and the few flags below. It is called after any of
/// them changes, including from <see cref="Render"/>. No handler sets a badge or enables a button on its
/// own. So the order in which a selection, a finished measurement and a preview arrive does not matter.
/// Whichever is last, the panel is drawn from all of them.</para>
/// </summary>
public sealed partial class SplitAlbumDialog
{
    private RestoreSettings _restore = RestoreSettings.Off;
    private bool _seeding;
    private string? _sessionDir;
    private CancellationTokenSource? _previewCts;

    /// <summary>The tool whose panel is open. None at the start, so the waveform keeps its
    /// height.</summary>
    private CleanTool _openTool = CleanTool.None;

    /// <summary>Whether the open tool shows its sliders. There is one switch for all three tools. A user
    /// who wants the sliders in one tool wants them in the next.</summary>
    private bool _showAdvanced;

    /// <summary>True while a profile is being measured, so the button cannot start a second
    /// one.</summary>
    private bool _measuring;

    /// <summary>True while a stretch is being rendered with the tools applied, for the Listen switch. It
    /// is cleared only by the render that is still current. See <see cref="PlayChangedAsync"/>.</summary>
    private bool _previewBusy;

    /// <summary>What the last profile attempt reported: the length it was taken from, or why it failed.
    /// It is null until a profile has been attempted in this window. After that, the step says whether a
    /// profile exists.</summary>
    private string? _profileNote;

    /// <summary>What the Listen step reports after a preview: that one is being prepared, what the click
    /// repair found, or an error. Null shows the step's instruction.</summary>
    private string? _listenNote;

    /// <summary>The gap that "Select a gap for me" selects, and the track list and analysis it was worked
    /// out from. See <see cref="CurrentGap"/>.</summary>
    private TrackSpan? _gap;
    private IReadOnlyList<EditorTrack>? _gapTracks;
    private SplitAnalysis? _gapAnalysis;

    /// <summary>A step that cannot be done yet is drawn at this opacity, so the order is clear at a
    /// glance.</summary>
    private const double WaitingStepOpacity = 0.45;

    /// <summary>The click slider runs the threshold backwards, so that moving right finds more clicks.
    /// The slider's 0 is this threshold, the gentle end. Its 14 is a threshold of 6, the strong
    /// end.</summary>
    private const double GentlestThreshold = 20;

    private static double StrengthOf(double threshold) => GentlestThreshold - threshold;

    private static double ThresholdOf(double strength) => GentlestThreshold - strength;

    /// <summary>Reads the settings that the record window left behind, and puts them on the controls.
    /// Called once the subject is known.</summary>
    private void InitRestore()
    {
        // The Tools key, its separator and the panel are collapsed in the markup. A file that is not a
        // recording shows them collapsed.
        if (_subject.Vinyl is null)
            return;

        ToolsSeparator.Visibility = Visibility.Visible;
        ToolsButton.Visibility = Visibility.Visible;
        // WaveToolStyle sets a 30 px square for a glyph key. This key also carries a word.
        ToolsButton.Width = double.NaN;

        // The knobs are wired HERE, not in the markup. This is the only way to be sure the handlers
        // cannot run while the dialog is half built.
        //
        // Both wrong ways of doing this have caused a failure. A Value literal assigns during
        // InitializeComponent and raises ValueChanged. Removing the literal is not enough: a Minimum
        // above the control's default of 0 drags the value up to it and raises the event just the same.
        // Each one threw in a handler whose TextBlocks did not exist yet. WinUI reported these as a
        // failure to assign to Value and then to Minimum. Attached after the tree exists, neither can
        // happen. A knob added later gets this for free.
        ReductionSlider.ValueChanged += NoiseKnob_ValueChanged;
        SensitivitySlider.ValueChanged += NoiseKnob_ValueChanged;
        SmoothingSlider.ValueChanged += NoiseKnob_ValueChanged;
        DeclickSlider.ValueChanged += DeclickKnob_ValueChanged;
        ListenSwitch.Toggled += ListenSwitch_Toggled;
        _relistenTimer.Tick += Relisten_Tick;
        BassSlider.ValueChanged += ToneKnob_ValueChanged;
        TrebleSlider.ValueChanged += ToneKnob_ValueChanged;

        _sessionDir = Path.GetDirectoryName(_subject.FilePath);

        try
        {
            if (_sessionDir is not null)
                _restore = RestoreSettings.Read(_sessionDir) ?? RestoreSettings.Off;
        }
        catch (InvalidDataException ex)
        {
            // The file exists but cannot be read. Say so, rather than cutting the record with settings
            // the user did not choose.
            Faults.Note("split", "reading the saved clean up settings", ex);
            _profileNote = Loc.Get("Split_SettingsUnreadable");
            _restore = RestoreSettings.Off;
        }

        RenderRestore();
    }

    /// <summary>Puts the settings on every control, then draws the steps.</summary>
    private void RenderRestore()
    {
        NoiseCheck.IsChecked = _restore.NoiseOn;
        Seed(ReductionSlider, _restore.Noise.ReductionDb);
        Seed(SensitivitySlider, _restore.Noise.Sensitivity);
        Seed(SmoothingSlider, _restore.Noise.FrequencySmoothingBands);
        DeclickCheck.IsChecked = _restore.DeclickOn;
        Seed(DeclickSlider, StrengthOf(_restore.Clicks.Threshold));
        Seed(BassSlider, _restore.Bass);
        Seed(TrebleSlider, _restore.Treble);
        RenderKnobValues();
        RenderTools();
    }

    /// <summary>
    /// Draws the Tools key and the open tool from the current settings and selection. It is safe to call
    /// at any time, and as often as the editor redraws. See the class note.
    /// </summary>
    private void RenderTools()
    {
        if (_subject.Vinyl is null)
            return;

        // Lit while anything will be applied. This is the look the snap key has when it is on, so a
        // closed panel still says that the cut will be cleaned.
        ToolsButton.Background = WaveKeyBrush(lit: !_restore.IsBypass);

        bool open = _openTool != CleanTool.None;
        RestorePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (!open)
            return;

        var (glyph, title) = Describe(_openTool);
        ToolGlyph.Text = glyph;
        ToolTitle.Text = title;
        NoiseTool.Visibility = Shown(_openTool == CleanTool.Noise);
        ClickTool.Visibility = Shown(_openTool == CleanTool.Clicks);
        ToneTool.Visibility = Shown(_openTool == CleanTool.Tone);

        // Bass and treble ARE the tool, so there is nothing for the switch to hide.
        AdvancedButton.Visibility = Shown(_openTool != CleanTool.Tone);
        AdvancedGlyph.Text = _showAdvanced ? "\uf077" : "\uf078";   // chevron up, chevron down
        AdvancedText.Text = Loc.Get(_showAdvanced ? "Split_HideAdvanced" : "Split_ShowAdvanced");
        NoiseAdvanced.Visibility = Shown(_openTool == CleanTool.Noise && _showAdvanced);
        ClickAdvanced.Visibility = Shown(_openTool == CleanTool.Clicks && _showAdvanced);

        // Noise reduction: step 1 selects a quiet part, step 2 takes a profile of it, step 3 turns it on.
        bool hasProfile = _restore.Profile is not null;
        double selected = _state.Selection is { } s ? s.To - s.From : 0;
        bool quietSelected = selected >= QuietStretch.MinSeconds;
        var gap = CurrentGap();

        NoiseSelectBadge.IsDone = quietSelected || hasProfile;
        SelectGapButton.IsEnabled = gap is not null;
        SelectQuietNote.Text =
            quietSelected ? Loc.Format("Split_QuietSelected", selected.ToString("0.0"))
            : _state.HasSelection ? Loc.Get("Split_ProfileTooShort")
            : gap is null && _analysis is not null ? Loc.Get("Split_NoGapHint")
            : Loc.Get("Split_SelectQuietHint");

        NoiseProfileBadge.IsDone = hasProfile;
        NoiseProfileStep.Opacity = quietSelected || hasProfile ? 1 : WaitingStepOpacity;
        TakeProfileButton.IsEnabled = quietSelected && !_measuring;
        ProfileStatusText.Text = _measuring
            ? Loc.Get("Split_ProfileMeasuring")
            : _profileNote ?? Loc.Get(hasProfile ? "Split_ProfileReady" : "Split_NoProfileYet");

        NoiseOnBadge.IsDone = _restore.NoiseApplies;
        NoiseOnStep.Opacity = hasProfile ? 1 : WaitingStepOpacity;
        NoiseCheck.IsEnabled = hasProfile;
        ReductionSlider.IsEnabled = hasProfile;
        SensitivitySlider.IsEnabled = hasProfile;
        SmoothingSlider.IsEnabled = hasProfile;

        // Each reset key is enabled only while its sliders are off their defaults, so a greyed arrow
        // means there is nothing to reset. Records compare by value.
        NoiseResetButton.IsEnabled = hasProfile && _restore.Noise != new NoiseReductionSettings();

        // Click removal. It finds clicks by how far a sample departs from what the signal around it
        // predicts. This needs no measurement of the record's quiet, so it is one switch.
        DeclickSlider.IsEnabled = _restore.DeclickOn;
        ClickResetButton.IsEnabled = _restore.DeclickOn && _restore.Clicks != new ClickRepairSettings();

        ToneResetButton.IsEnabled = Math.Abs(_restore.Bass) >= 0.01 || Math.Abs(_restore.Treble) >= 0.01;

        // Listen. Under noise reduction it is step 4, and it waits on step 3, like the steps before it.
        // The other tools have no steps, so the number and name are hidden.
        bool stepped = _openTool == CleanTool.Noise;
        ListenBadge.Visibility = Shown(stepped);
        ListenTitle.Visibility = Shown(stepped);
        ListenBadge.Opacity = _restore.NoiseApplies ? 1 : WaitingStepOpacity;
        ListenTitle.Opacity = _restore.NoiseApplies ? 1 : WaitingStepOpacity;
        RenderListen();
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A tool's mark and name, for its menu item and the title of its panel.</summary>
    private static (string Glyph, string Title) Describe(CleanTool tool) => tool switch
    {
        CleanTool.Noise => ("\uf8f1", Loc.Get("Split_ToolNoise")),
        CleanTool.Clicks => ("\uf0e7", Loc.Get("Split_ToolClicks")),
        CleanTool.Tone => ("\uf1de", Loc.Get("Split_ToolTone")),
        _ => ("", ""),
    };

    /// <summary>
    /// The quietest gap between two tracks on one side, or null when there is none to offer.
    ///
    /// <para>It is worked out again only when the track list or the analysis is a different object. That
    /// is how every edit and every re-detection shows itself (see <see cref="Render"/>). This runs on
    /// each redraw of the editor, sixty times a second during a scrub. The answer changes only when the
    /// tracks do.</para>
    /// </summary>
    private TrackSpan? CurrentGap()
    {
        if (_analysis is null || _subject.Vinyl is not { } vinyl)
            return null;

        if (!ReferenceEquals(_gapTracks, _state.Tracks) || !ReferenceEquals(_gapAnalysis, _analysis))
        {
            _gapTracks = _state.Tracks;
            _gapAnalysis = _analysis;
            _gap = QuietStretch.Pick(_state.Spans, vinyl.SideJoinSeconds,
                _analysis.EnvelopeDb, _analysis.WindowSeconds);
        }
        return _gap;
    }

    // ── the menu and the panel ──

    /// <summary>Built on each opening, so the "(on)" beside a tool is read from the settings as they are
    /// now.</summary>
    private void ToolsMenu_Opening(object? sender, object e)
    {
        ToolsFlyout.Items.Clear();
        ToolsFlyout.Items.Add(ToolEntry(CleanTool.Noise, _restore.NoiseApplies));
        ToolsFlyout.Items.Add(ToolEntry(CleanTool.Clicks, _restore.DeclickOn));
        ToolsFlyout.Items.Add(ToolEntry(CleanTool.Tone, _restore.ToneApplies));
    }

    private MenuFlyoutItem ToolEntry(CleanTool tool, bool on)
    {
        var (glyph, title) = Describe(tool);
        var item = new MenuFlyoutItem
        {
            Text = on ? Loc.Format("Split_ToolOn", title) : title,
            FontSize = 12,
            Icon = new FontIcon
            {
                Glyph = glyph,
                Style = (Style)Application.Current.Resources["FaMenuIcon"],
            },
        };
        item.Click += (_, _) =>
        {
            _openTool = tool;
            RenderTools();
        };
        return item;
    }

    private void CloseTool_Click(object sender, RoutedEventArgs e)
    {
        _openTool = CleanTool.None;
        RenderTools();
    }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        _showAdvanced = !_showAdvanced;
        RenderTools();
    }

    // ── noise reduction ──

    /// <summary>Selects the quietest gap between two tracks and brings it on screen. The next step can
    /// then take a profile of it, and Play original lets the user hear what was picked.</summary>
    private void SelectGap_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGap() is not { } gap)
            return;
        Apply(_state.WithSelection((gap.Start, gap.End)));
        Wave.EnsureVisible(gap.Start, gap.End);
    }

    private async void TakeProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Selection is not { } selection || selection.To - selection.From < QuietStretch.MinSeconds)
        {
            _profileNote = Loc.Get("Split_ProfileTooShort");
            RenderTools();
            return;
        }

        _measuring = true;
        RenderTools();
        try
        {
            double length = selection.To - selection.From;
            var profile = await VinylRestorer.MeasureProfileAsync(
                _subject.FilePath, selection.From, length, default);
            _restore = _restore with { Profile = profile, NoiseOn = true };
            _profileNote = Loc.Format("Split_ProfileTaken", length.ToString("F1"));
            SettingsChanged();
        }
        catch (Exception ex)
        {
            _profileNote = Loc.Format("Split_ProfileFailed", Faults.Note("split", "taking a noise profile", ex).Say());
        }
        finally
        {
            _measuring = false;
            RenderRestore();
        }
    }

    private void Noise_Click(object sender, RoutedEventArgs e)
    {
        _restore = _restore with { NoiseOn = NoiseCheck.IsChecked == true };
        SettingsChanged();
        RenderTools();
    }

    private void NoiseKnob_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // These values are being put on the knobs. It is not the user turning one. See Seed.
        if (_seeding)
            return;

        RenderKnobValues();
        _restore = _restore with
        {
            Noise = _restore.Noise with
            {
                ReductionDb = ReductionSlider.Value,
                Sensitivity = SensitivitySlider.Value,
                FrequencySmoothingBands = (int)SmoothingSlider.Value,
            },
        };
        SettingsChanged();
        RenderTools();
    }

    /// <summary>Goes back to the defaults of <see cref="NoiseReductionSettings"/>. The profile is kept.
    /// It is a measurement of this record, not a setting.</summary>
    private void NoiseReset_Click(object sender, RoutedEventArgs e)
    {
        _restore = _restore with { Noise = new NoiseReductionSettings() };
        SettingsChanged();
        RenderRestore();
    }

    // ── click removal ──

    private void Declick_Click(object sender, RoutedEventArgs e)
    {
        _restore = _restore with { DeclickOn = DeclickCheck.IsChecked == true };
        SettingsChanged();
        RenderTools();
    }

    private void DeclickKnob_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Seeding, not turning. See Seed.
        if (_seeding)
            return;

        _restore = _restore with
        {
            Clicks = _restore.Clicks with { Threshold = ThresholdOf(DeclickSlider.Value) },
        };

        // What the last preview found was found at the previous strength. It no longer answers for what
        // is on screen. It is cleared, so it is not read as current.
        _listenNote = null;

        SettingsChanged();
        RenderTools();
    }

    private void ClickReset_Click(object sender, RoutedEventArgs e)
    {
        _restore = _restore with { Clicks = new ClickRepairSettings() };
        _listenNote = null;
        SettingsChanged();
        RenderRestore();
    }

    /// <summary>
    /// What the click repair reported on the way past, or nothing when it was not running.
    ///
    /// <para>It is worth a line of its own, because the strength does not mean one fixed amount of
    /// intervention. Measured across two records, the same setting found 0.275% of one and 0.157% of the
    /// other. So the slider says how hard to look, and only this line says what was found.</para>
    /// </summary>
    private static string? DeclickFound(int repairs, double percent)
        => repairs <= 0
            ? null
            : Loc.Format("Split_DeclickFound", repairs.ToString("N0"), percent.ToString("0.00"));

    // ── bass and treble ──

    /// <summary>
    /// The tone shelves. They are applied AFTER the gate (see <c>RestoreChain</c>), so they shape a
    /// recording that already has its clicks and hiss removed.
    ///
    /// <para><c>EqOn</c> is turned on by moving either shelf. The tick for it lives in the record window,
    /// which a recorded session can never reach again. A shelf moved here that stayed switched off would
    /// do nothing, and would not say why.</para>
    /// </summary>
    private void ToneKnob_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_seeding)
            return;

        RenderKnobValues();
        _restore = _restore with
        {
            Bass = BassSlider.Value,
            Treble = TrebleSlider.Value,
            EqOn = true,
        };
        SettingsChanged();
        RenderTools();
    }

    private void ToneReset_Click(object sender, RoutedEventArgs e)
    {
        _restore = _restore with { Bass = 0, Treble = 0 };
        SettingsChanged();
        RenderRestore();
    }

    // ── the knobs ──

    /// <summary>
    /// Puts a saved value on a knob without its ValueChanged handler hearing it.
    ///
    /// <para>Seeding and turning are different things, and the handlers must only ever see turning.
    /// <see cref="RenderRestore"/> writes the noise knobs one after another, and the handler reads all
    /// three back. Without this guard, opening a session would overwrite the saved values with the
    /// controls' defaults and save them to restore.json, because the knobs not yet written still hold
    /// those defaults.</para>
    ///
    /// <para>The guard wraps the assignment, not the whole of <see cref="RenderRestore"/>. The handler is
    /// what reads the other two knobs, and it runs inside the assignment.</para>
    /// </summary>
    private void Seed(Slider knob, double value)
    {
        _seeding = true;
        try { knob.Value = value; }
        finally { _seeding = false; }
    }

    /// <summary>The number beside each slider. It is read from the controls, not from the settings, so it
    /// is right during a drag as well as after one. The click slider has no number. Its ends are labelled
    /// Gentle and Strong instead.</summary>
    private void RenderKnobValues()
    {
        ReductionValueText.Text = $"{ReductionSlider.Value:0} dB";
        SensitivityValueText.Text = $"{SensitivitySlider.Value:0.0} dB";
        SmoothingValueText.Text = $"{SmoothingSlider.Value:0}";
        BassValueText.Text = $"{BassSlider.Value:+0.0;-0.0;0} dB";
        TrebleValueText.Text = $"{TrebleSlider.Value:+0.0;-0.0;0} dB";
    }

    // ── listen ──

    /// <summary>A stretch rendered with the tools applied. It keeps the stretch and the settings it was
    /// rendered from. It is kept while both still match, so flipping the switch back and forth plays the
    /// same file and does not render it again.</summary>
    private sealed record RenderedPreview(string Path, (double From, double To) Span, RestoreSettings Settings);

    private RenderedPreview? _rendered;

    /// <summary>The side of the switch the user chose. While no tool is on, the switch shows Original and
    /// cannot be moved. This choice comes back into force as soon as a tool is turned on.</summary>
    private bool _preferChanges = true;

    /// <summary>The stretch to render when Play was pressed with nothing playing. A flip to Original
    /// during the wait can then play the original at once. Null otherwise.</summary>
    private (double From, double To)? _pendingListen;

    private bool WantChanges => _preferChanges && !_restore.IsBypass;

    private static readonly SolidColorBrush ChosenSide = new(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush OtherSide = new(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

    /// <summary>The Listen row: Play or Stop, the switch with its two words, and the line beside
    /// them.</summary>
    private void RenderListen()
    {
        bool listening = _previewBusy || PreviewPosition() is not null;
        ListenPlayGlyph.Text = listening ? "\uf04d" : "\uf04b";   // stop, play
        ListenPlayText.Text = Loc.Get(listening ? "Split_ListenStop" : "Split_ListenPlay");
        // There is nothing to compare until a tool is on, so Play waits, like its step. The selection can
        // still be heard as it is from the waveform's own play key. While something plays, this button
        // shows Stop.
        ListenPlayButton.IsEnabled = _analysis is not null && (listening || !_restore.IsBypass);

        bool changes = WantChanges;
        ListenSwitch.IsEnabled = !_restore.IsBypass;
        _seeding = true;
        try { ListenSwitch.IsOn = changes; }
        finally { _seeding = false; }
        ListenOriginalLabel.Foreground = changes ? OtherSide : ChosenSide;
        ListenChangesLabel.Foreground = changes ? ChosenSide : OtherSide;

        ListenNote.Text = _listenNote
            ?? Loc.Get(_restore.IsBypass ? "Split_ListenNothingOn" : "Split_ListenHint");
    }

    /// <summary>The selection when there is one. Otherwise fifteen seconds from the cursor.</summary>
    private (double From, double To) ListenSpan()
    {
        if (_state.Selection is { } selection && selection.To - selection.From > 0.2)
            return (selection.From, selection.To);
        double end = Math.Min(_analysis?.Plan.TotalSeconds ?? 0, _state.Cursor + 15);
        return (_state.Cursor, end);
    }

    /// <summary>Plays the stretch in the version the switch shows, or stops whatever is
    /// playing.</summary>
    private void ListenPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_previewBusy || PreviewPosition() is not null)
        {
            StopPreview();
            RenderTools();
            return;
        }

        if (_restore.IsBypass)
            return;
        var span = ListenSpan();
        if (span.To - span.From < 0.2)
            return;
        if (WantChanges)
            _ = PlayChangedAsync(span, span.From);
        else
            ArmPreview(span, span.From, changed: false);
    }

    /// <summary>
    /// The switch moved. While something plays, the sound changes over at the point it has reached.
    /// Otherwise the switch only decides what Play plays next.
    /// </summary>
    private void ListenSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        // RenderListen sets the switch from the settings. The user did not move it.
        if (_seeding)
            return;

        _preferChanges = ListenSwitch.IsOn;

        if (_playTimer.IsEnabled && _state.PlayRegion is { } span && PreviewPosition() is { } at)
        {
            if (_preferChanges)
                _ = PlayChangedAsync(span, at);
            else
            {
                // A render for the changed version may still be running. Its result is no longer wanted.
                // It must not take over from the original when it finishes.
                _previewCts?.Cancel();
                ArmPreview(span, at, changed: false);
            }
        }
        else if (!_preferChanges && _pendingListen is { } pending)
        {
            // Play was pressed for the changed version and it is still being prepared. Play the original
            // now, rather than wait for a version that is no longer asked for.
            _previewCts?.Cancel();
            ArmPreview(pending, pending.From, changed: false);
        }
        RenderTools();
    }

    private void ListenOriginal_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ListenSwitch.IsEnabled)
            ListenSwitch.IsOn = false;
    }

    private void ListenChanges_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ListenSwitch.IsEnabled)
            ListenSwitch.IsOn = true;
    }

    /// <summary>
    /// Plays the stretch with the tools applied, from <paramref name="at"/>. It is rendered to a temp
    /// file first. The chain is not part of the playback engine, and putting it there would apply it to
    /// everything else the user plays. The file is reused while the stretch and the settings still match.
    ///
    /// <para>The original may be playing while this renders, if the switch was flipped during playback.
    /// When the render is ready, the changed version starts at the point the original has reached by
    /// then, not at the point it was asked for. So the switch changes the sound and not the place.</para>
    ///
    /// <para>Every change of mind cancels the render: Stop (<see cref="StopPreview"/>), a flip back to
    /// Original, or a new render. A render that finishes after it was cancelled deletes its file and
    /// plays nothing. This holds whichever order the two arrive in.</para>
    /// </summary>
    private async Task PlayChangedAsync((double From, double To) span, double at)
    {
        if (_rendered is { } ready && ready.Span == span && ready.Settings.Equals(_restore))
        {
            ArmPreview(span, at, changed: true);
            return;
        }

        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var ct = cts.Token;
        var settings = _restore;
        string source = _subject.FilePath;

        _previewBusy = true;
        _pendingListen = PreviewPosition() is null ? span : null;
        _listenNote = Loc.Get("Split_PreviewBusy");
        RenderTools();
        try
        {
            // This runs on the thread pool. The chain's processing runs inside this call. On the UI
            // thread, a whole track of processing would freeze the window, the playhead included, while
            // the original plays on.
            var preview = await Task.Run(
                () => VinylRestorer.RenderPreviewAsync(source, span.From, span.To - span.From, settings, ct), ct);
            if (ct.IsCancellationRequested)
            {
                TryDelete(preview.Path);
                return;
            }

            UseRendered(new RenderedPreview(preview.Path, span, settings));
            _listenNote = DeclickFound(preview.Repairs, preview.RepairedPercent);

            double from = _state.PlayRegion == span && PreviewPosition() is { } now ? now : at;
            if (span.To - from < 0.2)
                from = span.From;
            ArmPreview(span, from, changed: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _listenNote = Loc.Format("Split_PreviewFailed", Faults.Note("split", "making the preview", ex).Say()); }
        finally
        {
            // Only the render that is still current says the wait is over. A render cancelled by a newer
            // one finishes while the newer one is still running, and the newer one owns these fields.
            if (ReferenceEquals(_previewCts, cts))
            {
                _previewCts = null;
                _previewBusy = false;
                _pendingListen = null;
            }
            cts.Dispose();
            RenderTools();
        }
    }

    /// <summary>Renders that were replaced while the player may still hold them open. They are deleted
    /// once it does not.</summary>
    private readonly List<string> _retiredRenders = new();

    /// <summary>
    /// Keeps a new render and retires the one it replaces.
    ///
    /// <para>The one replaced may be the one playing. A setting moved while the changed version plays
    /// renders a new one. The player stays on the old file until <see cref="ArmPreview"/> moves it
    /// across. So the old file is not deleted here. On each call, every retired file that the player is
    /// not on is deleted. Whatever is left when the window closes is deleted too.</para>
    /// </summary>
    private void UseRendered(RenderedPreview next)
    {
        if (_rendered is { } old && old.Path != next.Path)
            _retiredRenders.Add(old.Path);
        _rendered = next;
        _retiredRenders.RemoveAll(path => path != _playback.PlayingPath && TryDelete(path));
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch { return false; }   // still open; the next sweep or the temp folder gets it
    }

    /// <summary>Waits for a slider drag to stop before rendering again, so a drag costs one
    /// render.</summary>
    private readonly DispatcherTimer _relistenTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>Restarts the wait when the changed version is playing or being prepared, because what it
    /// plays no longer matches the settings. Otherwise there is nothing to do. The next Play renders
    /// afresh.</summary>
    private void QueueRelisten()
    {
        if (!PlayingRendered() && !_previewBusy)
            return;
        _relistenTimer.Stop();
        _relistenTimer.Start();
    }

    /// <summary>
    /// The settings stopped moving. The decision uses what is true now, not what was true when the timer
    /// started. The user may have pressed Stop or flipped the switch in between.
    /// </summary>
    private void Relisten_Tick(object? sender, object e)
    {
        _relistenTimer.Stop();

        bool playing = _playTimer.IsEnabled && _state.PlayRegion is not null && PreviewPosition() is not null;
        var span = _state.PlayRegion;
        double at = PreviewPosition() ?? 0;

        if (!WantChanges)
        {
            // Every tool was turned off while the changed version played. There is nothing left to
            // compare, so the switch shows Original, and the sound follows it.
            if (playing && PlayingRendered())
                ArmPreview(span!.Value, at, changed: false);
            return;
        }

        if (playing && (PlayingRendered() || _previewBusy))
            _ = PlayChangedAsync(span!.Value, at);
        else if (_pendingListen is { } pending)
            _ = PlayChangedAsync(pending, pending.From);
    }

    /// <summary>Stops anything this panel started. It is used on the dialog's closing path. That path has
    /// already stopped the player, so no rendered file is held open any more.</summary>
    private void StopRestoreWork()
    {
        _relistenTimer.Stop();
        _relistenTimer.Tick -= Relisten_Tick;
        _previewCts?.Cancel();
        if (_rendered is { } r)
            _retiredRenders.Add(r.Path);
        _rendered = null;
        _retiredRenders.RemoveAll(TryDelete);
    }

    /// <summary>A setting moved. It is saved, and whatever is playing is brought up to date with
    /// it.</summary>
    private void SettingsChanged()
    {
        SaveRestore();
        QueueRelisten();
    }

    private void SaveRestore()
    {
        if (_sessionDir is null) return;
        try { _restore.WriteTo(_sessionDir); }
        catch { /* the settings are still in hand for this cut */ }
    }

    /// <summary>
    /// The file the tracks are cut from: the restored album when there is anything to apply, otherwise
    /// the raw one.
    ///
    /// <para>It is rendered here, not when the sides were joined. The noise profile is taken from the
    /// joined album, and it does not exist until this window has been open. The raw album is left where
    /// it is. So changing the settings and cutting again costs one render, not another playing of the
    /// record.</para>
    /// </summary>
    private async Task<string> PrepareCutSourceAsync(string rawPath, CancellationToken ct)
    {
        if (_sessionDir is null || _restore.IsBypass)
            return rawPath;

        if (VinylImporter.RestoredAlbumIn(_sessionDir) is { } ready)
            return ready;

        string dest = Path.Combine(_sessionDir, VinylRestorer.RestoredFileName);
        var progress = new Progress<double>(p => _dispatcher.TryEnqueue(() =>
            StatusText.Text = Loc.Format("Split_Restoring", ((int)(p * 100)).ToString())));

        await VinylRestorer.RenderAsync(rawPath, _restore, dest, progress, ct);
        return dest;
    }

    /// <summary>
    /// Puts the raw album where the splitter would have put it.
    ///
    /// <para>The splitter moves whatever it cut into <c>Originals/</c>. For a restored session that is
    /// the render. The raw album would then still sit at the session's top level, and the import queue
    /// reads every audio file there as a track. So a forty-minute album would arrive next to its own
    /// tracks. Moving it in afterwards is also what makes a re-split work. <c>PrepareSessionAsync</c>
    /// looks for the raw album in <c>Originals/</c> and brings it back out.</para>
    /// </summary>
    private void StowRawAlbum(string rawPath)
    {
        if (_sessionDir is null || !File.Exists(rawPath))
            return;
        try
        {
            string originals = Path.Combine(
                _sessionDir, RPM.Core.Splitting.AlbumFileSplitter.OriginalsFolderName);
            Directory.CreateDirectory(originals);
            File.Move(rawPath, Path.Combine(originals, Path.GetFileName(rawPath)), overwrite: true);
        }
        catch { /* the tracks are cut; a raw album left at the top level is a tidying problem */ }
    }
}
