using RPM.Core.Diagnostics;
using System.Collections.ObjectModel;
using RPM.Core.Recording;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
// InputDevice is the sound card's input here. Uno's SDK adds System.Windows.Input to the implicit usings.
// On the WinUI head that namespace has its own InputDevice, so the name is ambiguous without this alias.
using InputDevice = RPM.Core.Recording.InputDevice;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>One recorded side as the sides list shows it.</summary>
[RpmBindable]
public sealed partial class VinylSideRow(string id, string name, string duration)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Duration { get; } = duration;
}

/// <summary>
/// The recording half of vinyl import: choose the input, calibrate the level against live peak meters,
/// then capture the record one side at a time into a session folder.
///
/// <para>It stops there on purpose. Splitting a recording into tracks is the same job as splitting any
/// album in one file, so that is done by <see cref="SplitAlbumDialog"/>. An interrupted session is also
/// reopened there. The output of this dialog is a folder of sides.</para>
///
/// <para>A session with recorded sides is never deleted when the window closes. It is kept in staging as
/// <c>vinyl-&lt;guid&gt;/</c> and is offered under Add music, like a pending rip. A side takes about
/// twenty minutes of real time, so a stray Cancel must not lose it. Only a session with nothing in it is
/// cleaned up.</para>
/// </summary>
public sealed partial class RecordVinylDialog : ContentDialog
{
    private enum Step { Device = 0, Record = 1 }

    private readonly VinylRecorder _recorder;
    private readonly string _stagingRoot;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<VinylSideRow> _sides = new();
    private readonly Dictionary<string, RecordedSide> _sideById = new();
    private readonly string _sessionDir;

    private Step _step = Step.Device;
    private DispatcherTimer? _elapsedTimer;
    private bool _recording;
    private bool _populatingDevices;
    private string? _result;

    // ── the tone controls ──
    private readonly List<Slider> _toneFaders = new();

    /// <summary>The index of the bass and treble shelves in <see cref="_toneFaders"/>, set while the
    /// faders are built. Everything before them is the pre-amp and the bands.</summary>
    private int _bassFader = -1, _trebleFader = -1;
    private RestoreSettings _restore = RestoreSettings.Off;

    /// <summary>Joins a drag's many changes (about a hundred a second) into one filter rebuild, and the
    /// file writes into far fewer. EqService uses the same approach for the playback curve.</summary>
    private DispatcherQueueTimer? _chainTimer;
    private DispatcherQueueTimer? _restoreWriteTimer;

    /// <summary>True while the faders are set from <see cref="_restore"/>, so the handlers do not read
    /// their own writes back.</summary>
    private bool _loadingTone;

    public RecordVinylDialog(VinylRecorder recorder, string stagingRoot)
    {
        _recorder = recorder;
        _stagingRoot = stagingRoot;
        _sessionDir = Path.Combine(stagingRoot, $"{RPM.Core.Ripping.PendingRips.VinylFolderPrefix}{Guid.NewGuid():N}");

        InitializeComponent();
        Shell.AttachResize(this, minWidth: 700, minHeight: 460, maxWidth: 920, defaultHeight: 620);
        Shell.SetSteps(Loc.Get("Vinyl_StepDevice"), Loc.Get("Vinyl_StepRecord"));

        SidesList.ItemsSource = _sides;
        _recorder.MeterUpdated += OnMeter;

        BuildToneFaders();
        RenderTone();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Records a record. Returns the session folder that holds the recorded sides, which the
    /// caller splits. Returns null when nothing was recorded.</summary>
    public static async Task<string?> ShowAsync(XamlRoot xamlRoot, string stagingRoot)
    {
        var capture = new OpenAlCapture();
        var monitor = new OpenAlSink();
        var recorder = new VinylRecorder(capture, monitor);
        var dialog = new RecordVinylDialog(recorder, stagingRoot) { XamlRoot = xamlRoot };
        try
        {
            await DialogHost.ShowAsync(dialog);
            return dialog._result;
        }
        finally
        {
            recorder.Dispose();   // releases capture device + monitor sink
        }
    }

#if DEBUG
    /// <summary>
    /// The gallery's way in: the same window on a stand-in input, so both faces can be seen on a machine
    /// with no turntable plugged in.
    ///
    /// <para>No monitor sink is passed. The stand-in signal is a test tone, and the gallery should not
    /// play it through the speakers.</para>
    /// </summary>
    /// <param name="openOnRecordStep">Skips the input step. The recording face is the hard one to reach:
    /// it needs a device, a level and a Next press. It is the face worth photographing.</param>
    internal static async Task ShowGalleryAsync(
        XamlRoot xamlRoot, string stagingRoot, IAudioCaptureSource capture, bool openOnRecordStep)
    {
        var recorder = new VinylRecorder(capture, monitor: null);
        var dialog = new RecordVinylDialog(recorder, stagingRoot)
        {
            XamlRoot = xamlRoot,
            _openOnRecordStep = openOnRecordStep,
        };
        try
        {
            await DialogHost.ShowAsync(dialog);
        }
        finally
        {
            recorder.Dispose();
        }
    }

    /// <summary>Set only by the gallery. See <see cref="ShowGalleryAsync"/>.</summary>
    private bool _openOnRecordStep;
#endif

    // ── setup ──────────────────────────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderStep();

        if (!_recorder.IsAvailable)
        {
            ShowDeviceError(Loc.Get("Vinyl_NoInput"));
            NextButton.IsEnabled = false;
            return;
        }

        PopulateDevices();

#if DEBUG
        if (_openOnRecordStep)
            EnterRecordStep();
#endif
    }

    /// <summary>Fills the input picker and starts monitoring the chosen device. The device that was
    /// already selected stays selected when it is still in the list. So a rescan, for a turntable that
    /// has just been plugged in, does not move the user off the device they had.</summary>
    private void PopulateDevices()
    {
        var wanted = DeviceCombo.SelectedItem as string;

        // The picker is rebuilt behind a guard, including the selection. Refilling it raises
        // SelectionChanged once for the clear and once for the new index. If that handler also started
        // the monitor, the device would be opened twice for one rescan. Monitoring is started once,
        // below, whichever path got here.
        _populatingDevices = true;
        try
        {
            DeviceCombo.Items.Clear();
            foreach (var d in _recorder.Devices())
                DeviceCombo.Items.Add(d.Name);

            if (DeviceCombo.Items.Count == 0)
            {
                ShowDeviceError(Loc.Get("Vinyl_NoInput"));
                NextButton.IsEnabled = false;
                return;
            }

            int index = wanted is null ? 0 : DeviceCombo.Items.IndexOf(wanted);
            DeviceCombo.SelectedIndex = index < 0 ? 0 : index;
        }
        finally
        {
            _populatingDevices = false;
        }

        ApplyMonitorDefault();
        StartMonitoring();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (_step != Step.Device)
            return;
        DeviceStatusText.Visibility = Visibility.Collapsed;
        PopulateDevices();
        NextButton.IsEnabled = _recorder.IsAvailable && DeviceCombo.Items.Count > 0;
    }

    private void OnClosed(object sender, ContentDialogClosedEventArgs e)
    {
        StopElapsedTimer();
        _recorder.MeterUpdated -= OnMeter;
        try { _recorder.Stop(); } catch { }

        // A recorded side is never thrown away on close. The session stays in staging and is offered
        // under Add music. Only an untouched session folder is cleaned up.
        if (_recorder.Sides.Count == 0)
        {
            try { if (Directory.Exists(_sessionDir)) Directory.Delete(_sessionDir, recursive: true); } catch { }
        }
    }

    // ── Step 1 · the tone ────────────────────────────────────────────────────────────────────

    /// <summary>The pre-amp, the ten bands and the two tone shelves. They are built in code rather than
    /// written out. Each has one column, with the slider above its label. The sound settings page builds
    /// them the same way, at a smaller size.</summary>
    private void BuildToneFaders()
    {
        var labels = new List<string> { Loc.Get("Settings_EqPre") };
        labels.AddRange(RPM.Desktop.Services.Audio.PlaybackService.EqBands
            .Select(hz => hz >= 1000 ? $"{hz / 1000}K" : hz.ToString()));

        // The shelves go at the end. Their positions are read from the list rather than written down, so
        // inserting a band cannot quietly move the treble fader.
        _bassFader = labels.Count;
        _trebleFader = labels.Count + 1;
        labels.Add(Loc.Get("Vinyl_ToneBass"));
        labels.Add(Loc.Get("Vinyl_ToneTreble"));

        for (int i = 0; i < labels.Count; i++)
        {
            ToneBandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int index = i;
            var fader = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -12,
                Maximum = 12,
                StepFrequency = 1,
                Height = 78,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsThumbToolTipEnabled = false,
            };
            fader.ValueChanged += (_, e) => OnToneFaderMoved(index, e.NewValue);

            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(fader);
            cell.Children.Add(new TextBlock
            {
                Text = labels[i],
                FontSize = 9,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });

            Grid.SetColumn(cell, i);
            ToneBandGrid.Children.Add(cell);
            _toneFaders.Add(fader);
        }

        foreach (var preset in RPM.Desktop.Services.Audio.EqService.Presets)
            TonePresetCombo.Items.Add(preset.Label);
    }

    private void OnToneFaderMoved(int index, double db)
    {
        if (_loadingTone) return;

        // The shelves are tested first. They sit past the end of the bands, so the band branch's index
        // arithmetic does not apply to them. It would read past the end of the array.
        if (index == _bassFader)
        {
            _restore = _restore with { Bass = db };
        }
        else if (index == _trebleFader)
        {
            _restore = _restore with { Treble = db };
        }
        else if (index == 0)
        {
            _restore = _restore with { Preamp = db };
        }
        else
        {
            var gains = _restore.Gains.ToArray();
            gains[index - 1] = db;
            _restore = _restore with { Gains = gains };
        }
        ToneChanged();
    }

    private void Tone_Click(object sender, RoutedEventArgs e)
    {
        _restore = _restore with { EqOn = ToneCheck.IsChecked == true };
        ToneChanged();
    }

    private void TonePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = TonePresetCombo.SelectedIndex;
        if (_loadingTone || index < 0 || index >= RPM.Desktop.Services.Audio.EqService.Presets.Length)
            return;
        // Bands only. A preset is a curve drawn across the ten bands. The shelves describe this
        // particular record's bottom and top ends. Choosing a preset must not undo them, so they are kept
        // as a separate pair.
        _restore = _restore with
        {
            EqOn = true,
            Gains = (double[])RPM.Desktop.Services.Audio.EqService.Presets[index].Gains.Clone(),
        };
        RenderTone();
        ToneChanged();
    }

    private void ToneReset_Click(object sender, RoutedEventArgs e)
    {
        // Reset clears the whole tone section, shelves included. A preset is the one action that leaves
        // them alone (see TonePreset_SelectionChanged). Reset means reset everything.
        _restore = _restore with
        {
            Preamp = 0,
            Gains = new double[RPM.Core.Audio.Equalizer.Bands.Length],
            Bass = 0,
            Treble = 0,
        };
        TonePresetCombo.SelectedIndex = -1;
        RenderTone();
        ToneChanged();
    }

    /// <summary>Puts the settings on the faders without the handlers reading them straight
    /// back.</summary>
    private void RenderTone()
    {
        _loadingTone = true;
        try
        {
            ToneCheck.IsChecked = _restore.EqOn;
            if (_toneFaders.Count > 0) _toneFaders[0].Value = _restore.Preamp;
            for (int i = 0; i < _restore.Gains.Count && i + 1 < _toneFaders.Count; i++)
                _toneFaders[i + 1].Value = _restore.Gains[i];
            if (_bassFader >= 0 && _bassFader < _toneFaders.Count)
                _toneFaders[_bassFader].Value = _restore.Bass;
            if (_trebleFader >= 0 && _trebleFader < _toneFaders.Count)
                _toneFaders[_trebleFader].Value = _restore.Treble;
        }
        finally { _loadingTone = false; }
    }

    /// <summary>A knob moved: rebuild the chain soon, write the file later.</summary>
    private void ToneChanged()
    {
        _chainTimer ??= NewCoalescingTimer(30, ApplyChain);
        _restoreWriteTimer ??= NewCoalescingTimer(800, WriteRestore);
        _chainTimer.Start();
        _restoreWriteTimer.Start();
    }

    private DispatcherQueueTimer NewCoalescingTimer(int milliseconds, Action tick)
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    /// <summary>Gives the recorder a chain built from the current settings. The pump reads the property
    /// once per block, so the change is heard from the next block.</summary>
    private void ApplyChain()
    {
        var chain = new RestoreChain(_restore, _recorder.SampleRate, _recorder.Channels);
        _recorder.MonitorChain = chain.IsBypass ? null : chain;
    }

    /// <summary>Writes the settings, but only once the session folder exists. Before the first side there
    /// is no folder. Creating one here would leave an empty session behind when the window closes without
    /// a recording.</summary>
    private void WriteRestore()
    {
        if (!Directory.Exists(_sessionDir)) return;
        try { _restore.WriteTo(_sessionDir); }
        catch { /* a curve that cannot be written is still being heard; the cut falls back to raw */ }
    }

    private InputDevice? SelectedDevice()
    {
        var devices = _recorder.Devices();
        int i = DeviceCombo.SelectedIndex;
        return i >= 0 && i < devices.Count ? devices[i] : devices.FirstOrDefault();
    }

    // ── Step 1 · device & calibration ────────────────────────────────────────────────────────

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingDevices || _step != Step.Device || !_recorder.IsAvailable) return;
        ApplyMonitorDefault();
        StartMonitoring();
    }

    /// <summary>
    /// Monitoring is switched on only for an input that is positively identified as line level. A
    /// microphone played through the speakers causes feedback. A device that cannot be identified ("USB
    /// Audio CODEC" is used by both turntables and microphones) is not worth guessing about. So any other
    /// input starts with monitoring off, and the reason is shown on screen. The switch stays usable
    /// either way. On headphones, a microphone monitors well.
    /// </summary>
    private void ApplyMonitorDefault()
    {
        var kind = SelectedDevice()?.Kind ?? InputDeviceKind.Unknown;
        SetMonitor(InputDevices.ShouldMonitorByDefault(kind));

        MonitorHint.Text = kind switch
        {
            InputDeviceKind.Microphone => Loc.Get("Vinyl_MonitorOffMic"),
            InputDeviceKind.Unknown => Loc.Get("Vinyl_MonitorOffUnknown"),
            _ => "",
        };
        MonitorHint.Visibility = kind == InputDeviceKind.LineIn ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StartMonitoring()
    {
        try
        {
            DeviceMeter.Reset();
            _recorder.StartMonitoring(SelectedDevice()?.Id);
            DeviceStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Faults.Note("vinyl", "monitoring the input", ex);
            ShowDeviceError(Loc.Get("Vinyl_DeviceFailed"));
        }
    }

    private void ShowDeviceError([Shown] string message)
    {
        DeviceStatusText.Text = message;
        DeviceStatusText.Visibility = Visibility.Visible;
    }

    private void Gain_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _recorder.Gain = (float)(e.NewValue / 100.0);
        if (GainText is not null)
            GainText.Text = $"{(int)e.NewValue}%";
    }

    private void Monitor_Click(object sender, RoutedEventArgs e) => SetMonitor(MonitorCheck.IsChecked == true);

    private void RecordMonitor_Click(object sender, RoutedEventArgs e) => SetMonitor(RecordMonitorCheck.IsChecked == true);

    /// <summary>One setting shown by two ticks (the calibrate step and the record step). Keep them in
    /// agreement.</summary>
    private void SetMonitor(bool on)
    {
        _recorder.MonitorEnabled = on;
        if (MonitorCheck is not null && MonitorCheck.IsChecked != on) MonitorCheck.IsChecked = on;
        if (RecordMonitorCheck is not null && RecordMonitorCheck.IsChecked != on) RecordMonitorCheck.IsChecked = on;
    }

    private void OnMeter(MeterFrame frame) => _dispatcher.TryEnqueue(() =>
    {
        switch (_step)
        {
            case Step.Device: DeviceMeter.Update(frame); break;
            case Step.Record: RecordMeter.Update(frame); break;
        }
    });

    // ── Step 2 · recording sides ─────────────────────────────────────────────────────────────

    private void EnterRecordStep()
    {
        try { _recorder.Stop(); } catch { }   // leave monitoring
        RecordMeter.Reset();
        _step = Step.Record;
        RenderStep();
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) return;
        Directory.CreateDirectory(_sessionDir);

        // A record is untagged audio until someone splits and names it. Until then, the session would
        // show in the pending list as a bare guid. So write down what is known: that it is a recording,
        // when it started, and from which input. The list can then show that instead.
        if (_sides.Count == 0)
        {
            new RPM.Core.Ripping.RipManifest(
                RPM.Core.Ripping.RipSource.Vinyl,
                CreatedUtc: DateTime.UtcNow,
                Device: SelectedDevice()?.Name).WriteTo(_sessionDir);
        }

        // The folder exists from here on, so the curve set during calibration has a place to be saved.
        WriteRestore();

        // Sides are kept at the top level of the session. An interrupted recording must be visible to the
        // pending scan, and the scanner would skip a hidden folder.
        string sidePath = VinylImporter.SidePath(_sessionDir, _sides.Count + 1);
        try
        {
            RecordMeter.Reset();
            _recorder.StartSide(SelectedDevice()?.Id, sidePath);
        }
        catch (Exception ex)
        {
            Faults.Note("vinyl", "starting a side", ex);
            RecordStatusText.Text = Loc.Get("Vinyl_DeviceFailed");
            return;
        }
        _recording = true;
        RecordStatusText.Text = Loc.Get("Vinyl_RecordingNow");
        StartElapsedTimer();
        RenderStep();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!_recording) return;
        StopElapsedTimer();
        RecordedSide? side;
        try
        {
            side = _recorder.Stop();
        }
        catch (Exception ex)
        {
            _recording = false;
            RecordStatusText.Text = Loc.Format("Vinyl_SideNotSaved", Faults.Note("vinyl", "saving a side", ex).Say());
            RenderStep();
            return;
        }
        _recording = false;

        if (side is not null && side.DurationSeconds > 0.5)
            RebuildSideRows();
        RecordStatusText.Text = Loc.Get("Vinyl_SideDone");
        RenderStep();
    }

    private void DiscardSide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || !_sideById.TryGetValue(id, out var side))
            return;
        _recorder.DiscardSide(side);
        RebuildSideRows();
        RenderStep();
    }

    private void MoveSideUp_Click(object sender, RoutedEventArgs e) => MoveSide(sender, -1);

    private void MoveSideDown_Click(object sender, RoutedEventArgs e) => MoveSide(sender, +1);

    /// <summary>Reorders the sides. Recording order is not always play order (side B may go on the
    /// platter first). The order here is the order the sides are joined and numbered in.</summary>
    private void MoveSide(object sender, int delta)
    {
        if (_recording || sender is not Button { Tag: string id } || !_sideById.TryGetValue(id, out var side))
            return;
        if (!_recorder.MoveSide(side, delta))
            return;

        // MoveSide swaps the files under the two records. So the rows are rebuilt from the recorder, not
        // shuffled in the list. The paths the rows are keyed by have just changed.
        RebuildSideRows();
    }

    private void RebuildSideRows()
    {
        _sideById.Clear();
        _sides.Clear();
        int n = 1;
        foreach (var side in _recorder.Sides)
        {
            _sideById[side.FilePath] = side;
            _sides.Add(new VinylSideRow(
                side.FilePath,
                Loc.Format("Vinyl_SideN", n++),
                FormatTime(side.DurationSeconds)));
        }

        // The list and its empty message swap places rather than the pane appearing with the first side.
        // The empty message is the instruction for this step.
        bool any = _sides.Count > 0;
        SidesList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SidesEmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StartElapsedTimer()
    {
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _elapsedTimer.Tick += (_, _) => RecordElapsedText.Text = FormatTime(_recorder.CurrentElapsedSeconds);
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
    }

    // ── navigation ───────────────────────────────────────────────────────────────────────────

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Device:
                EnterRecordStep();
                break;
            case Step.Record when _sides.Count > 0 && !_recording:
                _result = _sessionDir;   // the caller joins and splits it
                Hide();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_recording || _step != Step.Record) return;
        _step = Step.Device;
        RenderStep();
        StartMonitoring();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Stop a running side instead of closing out from under it. Anything recorded is kept and offered
        // under Add music, so closing costs nothing.
        if (_recording)
        {
            Stop_Click(sender, e);
            return;
        }
        Hide();
    }

    private void RenderStep()
    {
        DeviceStep.Visibility = _step == Step.Device ? Visibility.Visible : Visibility.Collapsed;
        RecordStep.Visibility = _step == Step.Record ? Visibility.Visible : Visibility.Collapsed;
        Shell.CurrentStep = (int)_step;

        BackButton.IsEnabled = _step != Step.Device && !_recording;
        DeviceCombo.IsEnabled = _step == Step.Device;
        RescanButton.IsEnabled = _step == Step.Device;

        RecordButton.Visibility = _step == Step.Record && !_recording ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = _step == Step.Record && _recording ? Visibility.Visible : Visibility.Collapsed;
        RecordButtonText.Text = Loc.Format("Vinyl_RecordSideN", _sides.Count + 1);

        NextButtonText.Text = _step == Step.Device
            ? Loc.Get("Vinyl_StartRecording")
            : Loc.Get("Vinyl_FinishSplit");
        NextButton.IsEnabled = _step switch
        {
            Step.Device => _recorder.IsAvailable,
            Step.Record => _sides.Count > 0 && !_recording,
            _ => false,
        };
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
