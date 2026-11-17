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
    private readonly Func<PlaylistTrack, ImageSource?> _cover;

    /// <summary>The cover lookup of a list that draws no covers (RPaMp's playlist panel).</summary>
    public static readonly Func<PlaylistTrack, ImageSource?> NoCovers = _ => null;

    /// <param name="track">The track the row shows first.</param>
    /// <param name="cover">The cover of a track, or <see cref="NoCovers"/>. The Now playing card keeps the
    /// covers, one for each record, so a drag that hands a track to another row hands over a cover that is
    /// already loaded.</param>
    public DeckRow(PlaylistTrack track, Func<PlaylistTrack, ImageSource?> cover)
    {
        _track = track;
        _cover = cover;
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
        Raise(nameof(Byline));
        Raise(nameof(Cover));
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

    /// <summary>Artist and album on one line, as on the phone's Now playing page.</summary>
    public string Byline => string.Join(" · ", new[] { _track.Artist, _track.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>The cover of the track's record. Empty until it is loaded, and empty for a record with no
    /// cover; the Now playing card draws a placeholder under it.</summary>
    public ImageSource? Cover => _cover(_track);

    private bool _grip;

    /// <summary>Whether the row shows a grip at its right end, which the Now playing card does while its
    /// rearrange key is on. The grip says the row can be moved.</summary>
    public bool Grip
    {
        get => _grip;
        set
        {
            if (_grip == value)
                return;
            _grip = value;
            Raise(nameof(Grip));
        }
    }

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name)
            Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
