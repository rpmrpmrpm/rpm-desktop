using System.ComponentModel;
using RPM.Desktop.Presentation.Shell;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// One line of the deck's playlist panel: a place in the list, showing whichever track is on it.
///
/// <para>
/// This class sits between the panel and the queue, and it exists for one reason. When a row is
/// dragged, the faces are handed from row to row so that the dragged track's index follows the
/// pointer (see <see cref="CarriedReorder{T}"/>). The queue is the collection the player reads:
/// <c>_currentIndex</c> points into it, the gapless chain is armed from the track after it, and album
/// gain is decided by comparing neighbours. If the faces were passed along the queue, the player would
/// read a different record from the one it is playing for as long as the drag lasted.
/// </para>
///
/// <para>So the panel gets its own rows to shuffle, and the queue is updated once, when the row is
/// dropped. The phone's playlist page arranges its list the same way, for the same reason
/// (<c>RPM.Mobile/Presentation/PlaylistPage.xaml.cs</c>).</para>
/// </summary>
[RpmBindable]
public sealed partial class DeckRow : INotifyPropertyChanged, ICarriedRow
{
    private PlaylistTrack _track;

    public DeckRow(PlaylistTrack track)
    {
        _track = track;
        _track.PropertyChanged += OnTrackChanged;
    }

    /// <summary>The track on this line. The row reads all its values through the track instead of copying
    /// them, so a change to the track's title or mark shows on the row too.</summary>
    public PlaylistTrack Track => _track;

    private TranslateTransform? _drop;

    /// <inheritdoc/>
    /// <remarks>Created on first read, not in the constructor, so a row can be built on any thread.
    /// A <c>TranslateTransform</c> can only be created on a thread with a dispatcher.</remarks>
    public TranslateTransform Drop => _drop ??= new();

    /// <summary>Puts a different track on this line, which is what a reorder does. The row keeps its place
    /// in the panel, and the list keeps its containers. Only the values the row shows change.</summary>
    public void Show(PlaylistTrack track)
    {
        if (ReferenceEquals(_track, track))
            return;

        _track.PropertyChanged -= OnTrackChanged;
        _track = track;
        _track.PropertyChanged += OnTrackChanged;

        Raise(nameof(Track));
        Raise(nameof(Index));
        Raise(nameof(Label));
        Raise(nameof(DurationText));
        Raise(nameof(IsCurrent));
        Raise(nameof(IsSelected));
        Raise(nameof(RowBackground));
        Raise(nameof(Title));
        Raise(nameof(Artist));
        Raise(nameof(ShowsIndex));
        Raise(nameof(ShowsMark));
    }

    /// <summary>Stops listening to the track it is showing. Call this when the panel discards its rows.
    /// A row that stays subscribed keeps its track alive through the event handler.</summary>
    public void Release() => _track.PropertyChanged -= OnTrackChanged;

    public int Index => _track.Index;
    public string Label => _track.Label;
    public string DurationText => _track.DurationText;
    public bool IsCurrent => _track.IsCurrent;
    public bool IsSelected => _track.IsSelected;
    public Brush RowBackground => _track.RowBackground;
    public string Title => _track.Title;
    public string? Artist => _track.Artist;

    private bool _grip;

    /// <summary>Whether the row shows a grip in place of its number, which the Now playing card does while its
    /// rearrange key is on. A number says where a song is, which the row's place already shows; the grip says
    /// it can be moved.</summary>
    public bool Grip
    {
        get => _grip;
        set
        {
            if (_grip == value)
                return;
            _grip = value;
            Raise(nameof(Grip));
            Raise(nameof(ShowsIndex));
            Raise(nameof(ShowsMark));
        }
    }

    /// <summary>The row's number shows on every row but the playing one, which has the play mark instead, and
    /// neither shows while the row has its grip.</summary>
    public bool ShowsIndex => !_grip && !IsCurrent;
    public bool ShowsMark => !_grip && IsCurrent;

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not { } name)
            return;
        Raise(name);
        if (name == nameof(IsCurrent))
        {
            Raise(nameof(ShowsIndex));
            Raise(nameof(ShowsMark));
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
