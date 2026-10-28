using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.RPaMp.Models;
using RPM.Desktop.Presentation.Shell;
using RPM.Desktop.Strings;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>The queue card the deck's queue key opens: every row of the one shared queue, with the playing one
/// lit. Pressing a row plays it where it stands. Its DataContext is the <see cref="RPaMpModel"/>, inherited from
/// the button that opens it as a flyout.
///
/// <para>Two keys in its band change the queue while it plays. Shuffle puts what is still to come in another
/// order and, pressed again, puts it back. Rearrange shows the rows' grips, and a row can then be dragged to
/// another place, as Pegasus's drawer and the deck's playlist panel do it (see <see cref="CarriedReorder{T}"/>).
/// </para></summary>
public sealed partial class QueueCard : UserControl
{
    /// <summary>One row for each place in the queue. The list shows these and not the queue, so a drag can hand
    /// the faces along them and tell the queue once (see <see cref="DeckRow"/> for why the queue the player
    /// reads must not be reordered for the length of a drag).</summary>
    private readonly ObservableCollection<DeckRow> _rows = new();
    private readonly CarriedReorder<DeckRow> _reorder;

    /// <summary>The queue the rows are kept in step with, while the card is open. Closed, the card does not
    /// listen: a song radio topping up a long queue would rebuild rows nobody can see.</summary>
    private RPaMpModel? _watched;

    /// <summary>Whether the rearrange key is on: the rows show their grips, and a press on a row picks it up
    /// rather than playing it.</summary>
    private bool _rearranging;

    public QueueCard()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;

        _reorder = new CarriedReorder<DeckRow>(
            Rows, _rows,
            exchange: (a, b) =>
            {
                var carried = _rows[a].Track;
                _rows[a].Show(_rows[b].Track);
                _rows[b].Show(carried);
            },
            commit: (from, to) => _watched?.Playlist.Move(from, to),
            canCarry: _ => _rearranging);

        Paint();
    }

    private RPaMpModel? Model => DataContext as RPaMpModel;

    /// <summary>Called by the host flyout as it opens. The rows are built from the queue and kept in step with
    /// it while the card is open, so rows that arrive while it is (a song radio topping up, a "play next") show
    /// at once. The scroll position is only set here.</summary>
    public void Refresh()
    {
        if (Model is not { } model) return;

        Watch(model);
        BuildRows();
        CapHeight();

        // The playing row is the one you came to see, so the card opens on it, a couple of rows down from the top
        // so what came before it is still in sight.
        var current = _rows.FirstOrDefault(r => r.IsCurrent);
        if (current is not null)
            Rows.ScrollIntoView(current, ScrollIntoViewAlignment.Leading);
    }

    /// <summary>Called by the host flyout as it closes. Rearranging goes off with the card, so it opens again
    /// as a list to play from.</summary>
    public void Closed()
    {
        _reorder.Cancel();
        _rearranging = false;
        Watch(null);
        foreach (var row in _rows)
            row.Release();
        _rows.Clear();
        Paint();
    }

    private void Watch(RPaMpModel? model)
    {
        if (ReferenceEquals(_watched, model))
            return;
        if (_watched is not null)
        {
            _watched.Playlist.CollectionChanged -= OnQueueChanged;
            _watched.PropertyChanged -= OnModelChanged;
        }
        _watched = model;
        if (_watched is not null)
        {
            _watched.Playlist.CollectionChanged += OnQueueChanged;
            _watched.PropertyChanged += OnModelChanged;
        }
    }

    /// <summary>The queue changed. The rows follow only when they do not already show what the queue says. A
    /// drag made here has already put every face on its final row before the queue is told, so the Move that
    /// comes back changes nothing, and following it anyway would make the list blink.</summary>
    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A change that did not come from the drag, under a carried row: the row would land on a place that
        // is no longer there.
        if (_reorder.IsBusy)
            _reorder.Cancel();

        // A shuffle moves many rows, one Move each. The rows are put in step once it is done (ShuffleKey_Click).
        if (_watched?.IsArrangingQueue == true)
            return;

        // The change is made on the rows as it was made on the queue, and the rows that did not change stay
        // as they are. Rebuilding them all would put the list back at its top, so a mixtape topping itself up
        // as the listener plays a song from it would take the list away from where they were.
        if (!RowsInStep())
            Follow(e);
        ShowCount();
    }

    /// <summary>Makes on the rows the change the queue reports. Anything the rows cannot follow one for one
    /// (a list cleared, or rows that were already out of step) puts them in step another way (see
    /// <see cref="CatchUp"/>).</summary>
    private void Follow(NotifyCollectionChangedEventArgs e)
    {
        if (_watched is not { } model)
            return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is { } added && e.NewStartingIndex >= 0
                                                        && _rows.Count + added.Count == model.Playlist.Count:
                for (var i = 0; i < added.Count; i++)
                    if (added[i] is PlaylistTrack track)
                        _rows.Insert(e.NewStartingIndex + i, new DeckRow(track) { Grip = _rearranging });
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is { } removed && e.OldStartingIndex >= 0
                                                           && _rows.Count - removed.Count == model.Playlist.Count:
                for (var i = 0; i < removed.Count; i++)
                {
                    _rows[e.OldStartingIndex].Release();
                    _rows.RemoveAt(e.OldStartingIndex);
                }
                break;

            case NotifyCollectionChangedAction.Move when e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0
                                                         && _rows.Count == model.Playlist.Count:
                _rows.Move(e.OldStartingIndex, e.NewStartingIndex);
                break;
        }

        if (!RowsInStep())
            CatchUp();
    }

    /// <summary>Puts the rows in step with the queue. With as many rows as songs (after a shuffle, for one),
    /// each row is given the song in its place, so the list keeps its rows and where it is scrolled to. Only a
    /// queue of another length is built again.</summary>
    private void CatchUp()
    {
        if (_watched is not { } model || _rows.Count != model.Playlist.Count)
        {
            BuildRows();
            return;
        }

        for (var i = 0; i < _rows.Count; i++)
            _rows[i].Show(model.Playlist[i]);
    }

    /// <summary>The playing song moved on, so whether a shuffle can do anything may have changed with it.</summary>
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RPaMpModel.IsQueueShuffled) or nameof(RPaMpModel.NowPlayingText))
            Paint();
    }

    private bool RowsInStep()
    {
        if (_watched is null || _rows.Count != _watched.Playlist.Count)
            return false;

        for (var i = 0; i < _rows.Count; i++)
            if (!ReferenceEquals(_rows[i].Track, _watched.Playlist[i]))
                return false;

        return true;
    }

    private void BuildRows()
    {
        foreach (var row in _rows)
            row.Release();
        _rows.Clear();

        if (_watched is not null)
            foreach (var track in _watched.Playlist)
                _rows.Add(new DeckRow(track) { Grip = _rearranging });

        ShowCount();
    }

    private void ShowCount()
    {
        var count = _rows.Count;
        CountText.Text = count == 0 ? "" : Loc.Format("Home_Songs", count);
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Rows.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Rearranging goes off with the last row it could move.
        if (_rearranging && count < 2)
            SetRearranging(false);
        else
            Paint();
    }

    /// <summary>Caps the list to the window, less the deck it opens from and a gap at the top, so a long queue
    /// scrolls inside the card rather than running off the screen.</summary>
    private void CapHeight()
    {
        if (XamlRoot is { } root && root.Size.Height > 0)
            Rows.MaxHeight = Math.Clamp(root.Size.Height - 180, 200, 520);
    }

    private void Rows_ItemClick(object sender, ItemClickEventArgs e)
    {
        // While rearranging, a press on a row is the start of a drag, not a song to play.
        if (_rearranging)
            return;
        if (e.ClickedItem is DeckRow row)
            Model?.PlayTrack(row.Track);
    }

    private void ShuffleKey_Click(object sender, RoutedEventArgs e)
    {
        _reorder.Cancel();
        Model?.ToggleQueueShuffle();
        if (!RowsInStep())
            CatchUp();
        Paint();
    }

    private void RearrangeKey_Click(object sender, RoutedEventArgs e) => SetRearranging(!_rearranging);

    private void SetRearranging(bool on)
    {
        if (!on)
            _reorder.Cancel();
        _rearranging = on;
        foreach (var row in _rows)
            row.Grip = on;
        Paint();
    }

    /// <summary>The two keys: lit while they are on (the app's cyan on a faint wash, as the playing row is lit),
    /// dimmed where they could change nothing, and the shuffle key saying which of its two presses comes
    /// next.</summary>
    private void Paint()
    {
        var shuffled = Model?.IsQueueShuffled == true;
        ShuffleKey.IsEnabled = Model?.CanShuffleQueue == true;
        PaintKey(ShuffleKey, shuffled);
        var shuffleTip = Loc.Get(shuffled ? "Deck_QueueUnshuffle" : "Rpm_Queue_Shuffle");
        ToolTipService.SetToolTip(ShuffleKey, shuffleTip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ShuffleKey, shuffleTip);

        RearrangeKey.IsEnabled = _rearranging || _rows.Count > 1;
        PaintKey(RearrangeKey, _rearranging);
    }

    private static void PaintKey(Button key, bool on)
    {
        key.Background = new SolidColorBrush(on ? Color.FromArgb(0x26, 0x04, 0xC8, 0xFF) : Color.FromArgb(0, 0, 0, 0));
        key.Foreground = new SolidColorBrush(!key.IsEnabled ? Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)
            : on ? Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF)
            : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    }
}
