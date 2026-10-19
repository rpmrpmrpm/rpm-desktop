using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Core.Recording;
using RPM.Core.Ripping;
using RPM.Core.Splitting;
using RPM.Desktop.Services;
using RPM.Desktop.Services.Platform;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The waveform half of the window: drawing it, the tools over it, its own transport, keeping it and the
/// grid pointed at the same place, and dragging it taller.
/// </summary>
public sealed partial class SplitAlbumDialog
{
    // ── The waveform ──
    //
    // The editor draws and reports. The model decides. Every gesture the editor raises becomes a state,
    // and comes straight back as a redraw.

    private void HookWave()
    {
        Wave.EdgeDragged += OnEdgeDragged;
        Wave.CursorChanged += OnCursorChanged;
        Wave.SelectionChanged += OnSelectionChanged;
        Wave.CurrentChanged += index => Apply(_state.WithCurrentIndex(index));
        Wave.TrackActivated += PlayTrackAt;
        Wave.BoundaryActivated += index => Apply(_state.Combine(index));
        Wave.ViewChanged += UpdateWaveReadout;
    }

    /// <summary>A marker being dragged. It is live while it moves, and the edit stands when it is
    /// released. Both write the same value, so what the drag showed is what is kept.</summary>
    private void OnEdgeDragged(int index, bool isEnd, double seconds, bool committed)
    {
        if (_rendering)
            return;
        var next = _state.MoveEdge(index, isEnd, seconds);
        if (next is null)
            return;
        Apply(committed ? next.WithCurrentIndex(index) : next);
    }

    private void OnCursorChanged(double seconds)
    {
        if (_rendering)
            return;
        // Scrubbing while a preview runs moves the sound with it. Otherwise the cursor is simply where
        // the next play, and the next split, start.
        if (_playTimer.IsEnabled && PreviewPosition() is not null)
            MovePreviewTo(seconds);
        Apply(_state.WithCursor(seconds));
    }

    private void OnSelectionChanged((double From, double To)? selection)
    {
        if (_rendering)
            return;
        Apply(_state.WithSelection(selection));
    }

    /// <summary>
    /// The line beside the tools: where the cursor is (or where the sound has got to), what is selected,
    /// and how much of the recording is on screen. Everything the tools act on, in numbers. It is written
    /// only when it changes, because this also runs on every frame of a moving playhead.
    /// </summary>
    private void UpdateWaveReadout()
    {
        if (_analysis is null)
            return;

        // While a preview runs, the number that matters is where the sound has got to. So the playhead
        // takes the first slot. The cursor gets it back as soon as the sound stops.
        string text = Wave.PlayheadSeconds is { } at
            ? Loc.Format("Split_WavePlaying", SplitTrackVm.Format(at))
            : Loc.Format("Split_WaveCursor", SplitTrackVm.Format(_state.Cursor));
        if (_state.Selection is { } selection && selection.To - selection.From > 0.05)
            text += "   " + Loc.Format("Split_WaveSelection",
                SplitTrackVm.Format(selection.From), SplitTrackVm.Format(selection.To),
                SplitTrackVm.Format(selection.To - selection.From));
        text += "   " + Loc.Format("Split_WaveShowing", SplitTrackVm.Format(Wave.View.Span));

        if (WaveReadout.Text != text)
            WaveReadout.Text = text;
    }

    private void UpdateToolState()
    {
        bool ready = _analysis is not null && !_busy;
        bool selection = _state.HasSelection;
        bool current = _state.CurrentId != 0;
        WaveSplitButton.IsEnabled = ready && _state.IndexAt(_state.Cursor) >= 0;
        WaveCombineButton.IsEnabled = ready && current && _state.CurrentIndex + 1 < _state.Count;
        WaveNewTrackButton.IsEnabled = ready && selection;
        WaveClearButton.IsEnabled = ready && selection;
        WaveDropButton.IsEnabled = ready && current;
        WaveZoomSelectionButton.IsEnabled = ready && (selection || current);
        WavePlayButton.IsEnabled = ready;
    }

    // ── The tools ──
    //
    // Each one is a single line: take the state, ask it for the edit, apply what comes back. What each
    // action is allowed to do is decided in the model, with the arithmetic it depends on.

    private void WaveZoomIn_Click(object sender, RoutedEventArgs e) => Wave.ZoomIn();

    private void WaveZoomOut_Click(object sender, RoutedEventArgs e) => Wave.ZoomOut();

    private void WaveZoomFit_Click(object sender, RoutedEventArgs e) => Wave.ZoomFit();

    private void WaveZoomSelection_Click(object sender, RoutedEventArgs e) => Wave.ZoomToSelection();

    private void WaveSnap_Click(object sender, RoutedEventArgs e)
    {
        Wave.SnapToSilence = !Wave.SnapToSilence;
        ApplySnapLook();
    }

    private void ApplySnapLook() => WaveSnapButton.Background = WaveKeyBrush(lit: Wave.SnapToSilence);

    private static readonly SolidColorBrush LitWaveKey = new(Color.FromArgb(0xCC, 0x03, 0x71, 0xD0));
    private static readonly SolidColorBrush PlainWaveKey = new(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    /// <summary>The background of a toolbar key that shows a state: blue while it is on. It uses the same
    /// two brush objects every time, so writing the brush a key already has is not a change.</summary>
    private static SolidColorBrush WaveKeyBrush(bool lit) => lit ? LitWaveKey : PlainWaveKey;

    /// <summary>Cuts the current track in two at the cursor. The half that keeps the title is the first
    /// one. The second is new, and it has no name until the user gives one.</summary>
    private void WaveSplit_Click(object sender, RoutedEventArgs e) =>
        Apply(_state.SplitAt(_state.Cursor), "Split_CannotSplitHere");

    private void WaveCombine_Click(object sender, RoutedEventArgs e) => Apply(_state.Combine(_state.CurrentIndex));

    /// <summary>Makes the selected stretch a track of its own, for example a song the detector walked
    /// past. Its audio is taken back from whatever tracks were covering it.</summary>
    private void WaveNewTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Selection is not { } selection)
            return;
        Apply(_state.Carve(selection.From, selection.To), "Split_SelectionTooShort");
    }

    /// <summary>Removes every boundary the selection covers. The tracks it touches become one. This is
    /// the tool for a side that a noisy passage chopped into six pieces.</summary>
    private void WaveClearMarkers_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Selection is not { } selection)
            return;
        Apply(_state.ClearBoundariesIn(selection.From, selection.To), "Split_NoMarkersInSelection");
    }

    /// <summary>Drops the current track outright. Its audio ends up in no file at all. This is what a
    /// detected lead-in, a locked groove or a side of applause should get.</summary>
    private void WaveDropTrack_Click(object sender, RoutedEventArgs e) => Apply(_state.Drop(_state.CurrentIndex));

    // ── The waveform's own transport ──
    //
    // The sound is not part of the model. A playback device is not a value. But WHICH track is playing is
    // part of the model. So the row's stop button, the waveform's playhead and the readout come from one
    // decision, not from three flags kept in step by hand.

    /// <summary>Plays what is on the waveform: the selection when there is one, otherwise the track under
    /// the cursor, from the cursor. Pressed again, it stops.</summary>
    private void WavePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis is null)
            return;
        if (_state.PlayRegion is not null || PreviewPosition() is not null)
        {
            StopPreview();
            return;
        }

        if (_state.Selection is { } selection && selection.To - selection.From > 0.2)
        {
            Play(selection.From, selection.To, trackId: 0);
            return;
        }
        PlayFrom(_state.Cursor);
    }

    /// <summary>A track double-clicked on the waveform, or its row's play button. It plays that track
    /// from start to end.</summary>
    private void PlayTrackAt(int index)
    {
        if (index < 0 || index >= _state.Count)
            return;
        var track = _state.Tracks[index];
        Apply(_state.WithCurrent(track.Id));
        Play(track.Start, track.End, track.Id);
    }

    /// <summary>Plays from a point: the track that owns it, to that track's end. In the space between two
    /// tracks, it plays half a minute of whatever is there.</summary>
    private void PlayFrom(double seconds)
    {
        if (_analysis is null)
            return;
        int index = _state.IndexAt(seconds);
        double end = index >= 0
            ? _state.Tracks[index].End
            : Math.Min(_analysis.Plan.TotalSeconds, seconds + 30);
        if (end - seconds < 0.2)
        {
            // The cursor landed on the very end of a track. There is nothing left to hear, so the preview
            // ends here, not after it carries on from where it was.
            StopPreview();
            return;
        }
        Play(seconds, end, _state.IdAt(index));
    }

    /// <summary>
    /// Moves a running preview to where the needle was just put.
    ///
    /// <para>Inside the stretch that is playing, this is only a seek. The same track carries on, and its
    /// row keeps its stop button. If the needle lands somewhere else, the preview becomes a preview of
    /// whatever is there. The track that was playing gives back its button, the track under the needle
    /// takes it, and the clip's stop point moves to the end of that track, not the old one's.</para>
    /// </summary>
    private void MovePreviewTo(double seconds)
    {
        if (_analysis is null)
            return;

        if (_state.PlayRegion is { } region && seconds >= region.From && region.To - seconds > 0.2)
        {
            // The preview is re-armed, not sought. The engine keeps a clip's LENGTH. A bare seek would
            // carry the old stop point along, and the preview would run past the end of this track.
            // Whichever version is playing, the album or the rendered one, keeps playing.
            ArmPreview(region, seconds, changed: PlayingRendered());
            return;
        }
        PlayFrom(seconds);
    }

    /// <summary>
    /// Where the preview has got to, in the album's time, or null when none of this window's sound is
    /// playing.
    ///
    /// <para>A preview plays one of two files: the album itself, or the rendered preview of a stretch
    /// with the clean-up tools applied (the Listen switch). The rendered file starts at 0, so its
    /// position is offset by where its stretch starts. This is the one place that tells the two apart.
    /// Anything that asks whether our sound is still playing asks here.</para>
    /// </summary>
    private double? PreviewPosition()
    {
        string? path = _playback.PlayingPath;
        if (path is null)
            return null;
        if (path == _subject.FilePath)
            return _playback.PositionSeconds;
        if (_rendered is { } r && path == r.Path)
            return r.Span.From + _playback.PositionSeconds;
        return null;
    }

    private bool PlayingRendered() => _rendered is { } r && _playback.PlayingPath == r.Path;

    /// <summary>
    /// Plays a stretch from a point: the album as recorded, or the rendered preview of that stretch with
    /// the clean-up tools applied.
    ///
    /// <para>When the same stretch is already playing, only the engine is re-armed. So a flip of the
    /// Listen switch, or a click inside the stretch, changes the sound and not the stretch, the row, or
    /// the playhead's place. Otherwise it is a new preview, and it starts like one.</para>
    /// </summary>
    private void ArmPreview((double From, double To) span, double at, bool changed)
    {
        if (_analysis is null || span.To - at < 0.2)
            return;

        bool continuing = _playTimer.IsEnabled && _state.PlayRegion == span;
        if (!continuing)
            StopPreview();

        if (changed && _rendered is { } r && r.Span == span)
            _playback.Play(r.Path, r.Span.To - r.Span.From, at - r.Span.From,
                playLengthSeconds: span.To - at);
        else
            _playback.Play(_subject.FilePath, _analysis.Plan.TotalSeconds, at,
                playLengthSeconds: span.To - at);
        Wave.PlayheadSeconds = at;

        if (!continuing)
        {
            Wave.ResumeFollow();
            WavePlayGlyph.Text = "\uf04d";   // stop
            _playTimer.Start();
            Apply(_state.WithPlaying(0, span).WithCursor(span.From));
        }
        UpdateWaveReadout();
        RenderTools();
    }

    /// <summary>Starts the preview and puts the playhead on the waveform. Only one place does this, so
    /// there is only one thing to stop.</summary>
    private void Play(double start, double end, int trackId)
    {
        if (_analysis is null || end - start < 0.2)
            return;

        StopPreview();
        _playback.Play(_subject.FilePath, _analysis.Plan.TotalSeconds, start, playLengthSeconds: end - start);
        // What is being previewed is a stretch: a whole track when a track is playing, however far into
        // it the sound started. Otherwise it is just the clip. Scrubbing inside it stays this preview.
        // Leaving it starts another one. The cursor goes where the sound starts. A play that left the
        // cursor behind would show a time far from what is being heard.
        var track = _state.Track(trackId);
        var region = track is not null ? (track.Start, track.End) : (start, end);
        // A new preview is a new thing to watch, so the view follows the sound again, even if the last
        // preview was scrolled away.
        Wave.ResumeFollow();
        Wave.PlayheadSeconds = start;
        WavePlayGlyph.Text = "\uf04d";   // stop
        _playTimer.Start();
        Apply(_state.WithPlaying(trackId, region).WithCursor(start));
        UpdateWaveReadout();
    }

    private void StopPreview()
    {
        _playTimer.Stop();
        // A version with the tools applied may still be rendering. When it finishes, it would start
        // playing. Stopping means it must not.
        _previewCts?.Cancel();
        if (PreviewPosition() is not null)
            _playback.Stop();
        Wave.PlayheadSeconds = null;
        WavePlayGlyph.Text = "\uf04b";   // play
        Apply(_state.NotPlaying);
        UpdateWaveReadout();
    }

    /// <summary>
    /// Gets the player off the album file and waits for the handle to close, so the cut can have it. It
    /// returns false if the file is still held at the end of the wait. The caller then says so, rather
    /// than starting a split that cannot finish.
    ///
    /// <para><see cref="StopPreview"/> does not do two things, and both of them can cost a split.</para>
    ///
    /// <para>It only lets go when the file is the one PLAYING. The engine also holds the track queued
    /// behind it for the gapless hand-over. An album file lined up to play next is open on disk, just as
    /// if it were playing. <see cref="PlaybackService.HoldsOpen"/> is the full list of what the engine
    /// has open, and that is the question to ask.</para>
    ///
    /// <para>Stopping is also not letting go. The decoder unwinds on the audio thread, so the file stays
    /// held for a moment after the transport says it has stopped. The cut writes into that moment. This
    /// failure does not look like a failed cut. The tracks are written to a temp folder first, and the
    /// original is moved to Originals/ last. So the whole job would run for minutes and then fail on the
    /// final step, and the roll-back would throw the work away.</para>
    /// </summary>
    private async Task<bool> ReleaseSourceForCutAsync()
    {
        if (_playback.HoldsOpen(_subject.FilePath))
            _playback.Stop();

        return await RPM.Core.Files.FileHandles.WaitUntilWritableAsync(_subject.FilePath);
    }

    /// <summary>The playhead follows the sound, not a clock. The position comes from the output, which is
    /// what is actually coming out of the speakers.</summary>
    private void OnPlayTick(object? sender, object e)
    {
        if (PreviewPosition() is not { } at)
        {
            StopPreview();
            return;
        }
        Wave.PlayheadSeconds = at;
        UpdateWaveReadout();
    }

    /// <summary>The engine raises these on a thread other than the UI thread. The row and the toolbar
    /// belong to the UI thread.</summary>
    private void OnPlaybackChanged() => _dispatcher.TryEnqueue(() =>
    {
        if (!_playTimer.IsEnabled)
            return;   // nothing of ours is playing; this is the rest of the app
        // Still ours means the engine is still running one of our two files (see PreviewPosition). The
        // length handed to Play stops it at the end of the stretch, and that arrives here as a stop.
        bool stillPlaying = PreviewPosition() is not null && !_playback.IsPaused;
        if (!stillPlaying)
            StopPreview();
    });

    /// <summary>The row's own play button: this track, from start to end. The waveform's playhead runs
    /// with it. There is one preview and one place that starts it.</summary>
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SplitTrackVm row)
            return;
        // The same button in both states: it plays the row, and while the row is playing it stops it.
        if (row.Id == _state.PlayingId)
        {
            StopPreview();
            return;
        }
        PlayTrackAt(_state.IndexOf(row.Id));
    }

    /// <summary>
    /// The editor's keyboard. It is reached only when focus is not in a text box. A text box handles its
    /// own space bar and arrows, so typing a title never triggers a cut.
    /// </summary>
    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_analysis is null || _busy)
            return;

        bool shift = HeldKeys.Now.HasFlag(Modifier.Shift);

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space: WavePlay_Click(this, e); break;
            case Windows.System.VirtualKey.S: WaveSplit_Click(this, e); break;
            case Windows.System.VirtualKey.J: Apply(_state.Combine(_state.CurrentIndex)); break;
            case Windows.System.VirtualKey.N: WaveNewTrack_Click(this, e); break;
            case Windows.System.VirtualKey.Z: Wave.ZoomToSelection(); break;
            case Windows.System.VirtualKey.Number0 or Windows.System.VirtualKey.NumberPad0: Wave.ZoomFit(); break;
            case Windows.System.VirtualKey.Add or (Windows.System.VirtualKey)187: Wave.ZoomIn(); break;      // 187 = the main row's =/+
            case Windows.System.VirtualKey.Subtract or (Windows.System.VirtualKey)189: Wave.ZoomOut(); break;
            case Windows.System.VirtualKey.Delete or Windows.System.VirtualKey.Back:
                if (_state.HasSelection) WaveClearMarkers_Click(this, e);
                else WaveDropTrack_Click(this, e);
                break;
            case Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right:
            {
                // Nudges the cursor one pixel at a time (ten with Shift). This is how a cut is placed
                // exactly, once the zoom is close enough to see the note it belongs after.
                double step = Math.Max(0.005, Wave.View.Span / 400) * (shift ? 10 : 1);
                Apply(_state.WithCursor(_state.Cursor + (e.Key == Windows.System.VirtualKey.Left ? -step : step)));
                break;
            }
            default:
                return;
        }
        e.Handled = true;
    }

    // ── the grid follows the waveform, and the other way about ──

    private void Row_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => MakeCurrent(sender);

    private void Row_GotFocus(object sender, RoutedEventArgs e) => MakeCurrent(sender);

    /// <summary>Touching a row makes its stretch the current one and brings it on screen. The grid and
    /// the picture are two views of one thing.</summary>
    private void MakeCurrent(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is not SplitTrackVm row)
            return;
        if (row.Id == _state.CurrentId || _state.Track(row.Id) is not { } track)
            return;
        Apply(_state.WithCurrent(row.Id));
        Wave.EnsureVisible(track.Start, track.End);
    }

    // ── dragging the waveform taller ──

    private bool _splitterDragging;
    private double _splitterStartY;
    private double _splitterStartWave;

    private void WaveSplitter_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _splitterDragging = true;
        _splitterStartY = e.GetCurrentPoint(this).Position.Y;
        _splitterStartWave = WaveRow.ActualHeight;
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void WaveSplitter_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
            return;

        // Both rows use star sizes, so the pair keeps filling the dialog when it is resized. The drag
        // only changes the ratio between them.
        //
        // The minimum heights are read from the rows, not hard-coded here, so a drag never pushes the
        // wave row below its minimum. The clean-up strip can raise that minimum, and the drag then
        // respects it.
        double delta = e.GetCurrentPoint(this).Position.Y - _splitterStartY;
        double total = WaveRow.ActualHeight + ListRow.ActualHeight;
        double floor = WaveRow.MinHeight;
        double wave = Math.Clamp(
            _splitterStartWave + delta, floor, Math.Max(floor, total - ListRow.MinHeight));
        WaveRow.Height = new GridLength(wave, GridUnitType.Star);
        ListRow.Height = new GridLength(Math.Max(1, total - wave), GridUnitType.Star);
        e.Handled = true;
    }

    private void WaveSplitter_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
            return;
        _splitterDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // ── Editing boundaries by typing ──
    //
    // Typing moves nothing until it is committed, on focus loss or Enter. The text is parsed, that one
    // edge is moved through the model, and the row is written again from what actually happened. So a
    // time that was clamped shows the value it was clamped to.

    private void Start_Commit(object sender, RoutedEventArgs e) => CommitBoundary(sender as FrameworkElement, isEnd: false);
    private void End_Commit(object sender, RoutedEventArgs e) => CommitBoundary(sender as FrameworkElement, isEnd: true);

    private void TimeBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        var fe = sender as FrameworkElement;
        CommitBoundary(fe, isEnd: (fe?.Tag as string) == "end");
    }

    private void CommitBoundary(FrameworkElement? box, bool isEnd)
    {
        if (box?.DataContext is not SplitTrackVm row || _analysis is null)
            return;
        int index = _state.IndexOf(row.Id);
        if (index < 0)
            return;

        string text = (box as TextBox)?.Text ?? "";
        if (SplitTrackVm.Parse(text) is not { } value)
        {
            RenderAll();   // gibberish: put the canonical time back
            return;
        }
        Apply(_state.MoveEdge(index, isEnd, value));
        RenderAll();       // the box shows where the edge landed, clamped or not
    }

    /// <summary>Snaps this row's length to the album's length for that track, keeping its start. Adopting
    /// a release never moves a cut on its own. This button is how a release time gets taken, one row at a
    /// time, by someone who has looked at it. The edge it moves is marked as an album time, so the row's
    /// dot shows where that boundary came from. Names are left alone.</summary>
    private void ApplyHint_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SplitTrackVm row)
            return;
        int index = _state.IndexOf(row.Id);
        if (index < 0 || _albumLengths is not { } lengths || index >= lengths.Count || lengths[index] <= 0)
            return;
        Apply(_state.MoveEdge(index, isEnd: true, _state.Tracks[index].Start + lengths[index],
            BoundarySource.DurationAligned));
    }
}
