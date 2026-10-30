using System.ComponentModel;

namespace RPM.Desktop.Presentation.Main.Library.Models;

/// <summary>
/// One place on the album page's series row: a volume the listener has, or a numbered one still to
/// find. It is drawn at the same size as a suggestion (see SimilarTileMetrics), because it stands in
/// the same row and the row is measured once for both.
/// </summary>
[RpmBindable]
public sealed partial class SeriesRow(
    string? folder, string title, string artist, string volume, string mark, bool isHere,
    SimilarTileMetrics metrics)
    : INotifyPropertyChanged, RPM.Desktop.Services.Art.ISleeveCard
{
    public SimilarTileMetrics Metrics { get; } = metrics;

    /// <summary>The record's folder, or "" for a volume that is not in the library.</summary>
    public string Folder { get; } = folder ?? "";

    /// <summary>The record's name, or "Still to find" for a volume that is not here.</summary>
    public string Title { get; } = title;

    /// <summary>Who made it, for the sleeve lookup. Not drawn: every record in a series is usually the
    /// same composer's, and the volume says more.</summary>
    public string Artist { get; } = artist;

    /// <summary>"Volume 43", or "" for a volume with no number.</summary>
    public string Volume { get; } = volume;

    /// <summary>The volume's own spelling, printed large on an empty sleeve: "12".</summary>
    public string Mark { get; } = mark;

    public bool IsMissing => Folder.Length == 0;

    public bool IsHeld => !IsMissing;

    /// <summary>The record this page is showing. Its sleeve wears a ring, so the reader can see where in
    /// the series they are.</summary>
    public bool IsHere { get; } = isHere;

    private bool _coverMissing;

    public bool CoverMissing
    {
        get => _coverMissing;
        set
        {
            if (_coverMissing == value)
                return;
            _coverMissing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FigureSeed)));
        }
    }

    /// <summary>The made sleeve's seed, once the lookup has come back empty. Never for a missing volume:
    /// that one draws an empty sleeve with its number on it instead.</summary>
    public string? FigureSeed => _coverMissing && !IsMissing ? Folder : null;

    private Microsoft.UI.Xaml.Media.ImageSource? _cover;

    public Microsoft.UI.Xaml.Media.ImageSource? Cover
    {
        get => _cover;
        set
        {
            _cover = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
        }
    }

    public bool HasCover => _cover is not null;

    public event PropertyChangedEventHandler? PropertyChanged;
}
