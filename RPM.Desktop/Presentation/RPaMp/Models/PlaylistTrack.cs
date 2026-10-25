using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Models;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Strings;
using Windows.UI;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// One entry in the RPaMp playlist. Winamp-style rows read
/// "1. Artist - Title .......... 3:21"; the display fields here feed that layout.
/// </summary>
[RpmBindable]
public sealed partial class PlaylistTrack : INotifyPropertyChanged
{
    private int _index;
    private bool _isCurrent;

    /// <summary>The tags the ticker is built from, kept until it is first read.</summary>
    private readonly TrackInfo _track;
    private string? _details;

    /// <param name="radio">The song radio that queued this row, or null for a row put there any other way. No
    /// default, so every place that builds a row says which it is.</param>
    public PlaylistTrack(TrackInfo track, SongRadioRun? radio)
    {
        Radio = radio;
        FilePath = track.FilePath;
        DurationSeconds = track.DurationSeconds;
        RpmId = track.RpmId;
        IsExplicit = track.IsExplicit;

        var artist = string.IsNullOrWhiteSpace(track.Artist) ? "" : track.Artist!.Trim();
        var title = string.IsNullOrWhiteSpace(track.Title)
            ? Path.GetFileNameWithoutExtension(track.FilePath)
            : track.Title!.Trim();
        Label = artist.Length > 0 ? $"{artist} - {title}" : title;

        Title = title;
        Artist = artist.Length > 0 ? artist : null;
        Album = string.IsNullOrWhiteSpace(track.Album) ? null : track.Album!.Trim();
        Genre = string.IsNullOrWhiteSpace(track.Genre) ? null : track.Genre!.Trim();

        _track = track;
    }

    public string FilePath { get; }
    public double DurationSeconds { get; }

    /// <summary>The song radio that queued this row, or null. A row of a radio going on the deck is what asks that
    /// radio for more songs (RPaMpModel.Radio).</summary>
    public SongRadioRun? Radio { get; }

    /// <summary>Whether the listener added this row themselves, with Play next or Add to now playing, to a queue
    /// that held songs already. Pegasus's queue drawer lists these under an "Up next" heading, apart from the list
    /// that was played (see RPM.Core.Playback.QueueHeadings). The mark is the row's own, so it moves with the row.
    /// The main window does not read it.</summary>
    public bool IsUpNext { get; init; }

    /// <summary>The file's own audio facts, for the LCD's readout. Null where the tags do not say.</summary>
    public int? BitrateKbps => _track.BitrateKbps;
    public int? SampleRate => _track.SampleRate;
    public int? Channels => _track.Channels;

    /// <summary>The id the cloud uses for this track, where it has one. A row whose file is not on this
    /// disk plays from the cloud with this id. That happens for an album moved off this disk, or for one
    /// that only exists on another of the user's installs.</summary>
    public string? RpmId { get; }

    /// <summary>Whether the file's explicit flag marks the lyrics as explicit. The queue shows an E
    /// beside the title, the same mark the library lists use.</summary>
    public bool IsExplicit { get; }

    public string Label { get; }

    // Kept apart from Label, because a cast receiver shows them on the TV as separate fields.
    // "Artist - Title" on one line would look wrong there.
    public string Title { get; }
    public string? Artist { get; }
    public string? Album { get; }

    /// <summary>The genre tag, as the file gives it. It is a field of its own, not only part of
    /// <see cref="Details"/>, because more than the ticker reads it. The Pegasus room uses it to decide
    /// whether to show a mirror ball (see <c>MirrorBall</c>). Code cannot read a tag that is buried in
    /// the joined <see cref="Details"/> text.</summary>
    public string? Genre { get; }

    /// <summary>Everything known about the track, shown in the scrolling ticker. The text is built the
    /// first time it is read, not when the row is made. The words come from the string table, which only
    /// the running app can read. Rows are also made where no ticker is shown.</summary>
    public string Details => _details ??= BuildDetails(_track, Artist ?? "", Title);

    /// <summary>Joins every tag worth showing with bullets. The ticker scrolls the result.</summary>
    private static string BuildDetails(TrackInfo t, string artist, string title)
    {
        var parts = new List<string> { artist.Length > 0 ? $"{artist} · {title}" : title };

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            parts.Add(value.Trim());
        }

        if (!string.IsNullOrWhiteSpace(t.Album))
        {
            var album = t.Album!.Trim();
            if (t.TrackNumber > 0)
                album = t.TrackCount > 0 ? $"{album} [{t.TrackNumber}/{t.TrackCount}]" : $"{album} [{t.TrackNumber}]";
            if (t.Year > 0) album = $"{album} ({t.Year})";
            parts.Add(album);
        }
        else if (t.Year > 0)
        {
            parts.Add(t.Year.ToString());
        }

        if (!string.IsNullOrWhiteSpace(t.AlbumArtist) && !string.Equals(t.AlbumArtist, artist, StringComparison.OrdinalIgnoreCase))
            Add(t.AlbumArtist);

        Add(t.Genre);
        if (t.Bpm > 0) parts.Add($"{t.Bpm} {Loc.Get("Details_BpmUnit")}");
        if (!string.IsNullOrWhiteSpace(t.Composers)) parts.Add(Loc.Format("Amp_TickerComposedBy", t.Composers.Trim()));
        Add(t.Publisher);

        if (t.DurationSeconds > 0)
            parts.Add(TimeSpan.FromSeconds(t.DurationSeconds).ToString(@"m\:ss"));

        parts.Add(Path.GetExtension(t.FilePath).TrimStart('.').ToUpperInvariant());

        return string.Join("   •   ", parts);
    }

    public string DurationText =>
        DurationSeconds <= 0 ? "--:--" : TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss");

    /// <summary>1-based row number shown in the Winamp gutter; kept in sync by the model.</summary>
    public int Index
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            OnChanged();
            OnChanged(nameof(RowNumber));

            // The band depends on the row position, so a renumber changes it too.
            OnChanged(nameof(RowBackground));
        }
    }

    public string RowNumber => $"{_index}.";

    private bool _isSelected;

    /// <summary>True for the track loaded in the player. Its row gets a faint cyan background.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent == value) return; _isCurrent = value; OnChanged(); OnChanged(nameof(RowBackground)); }
    }

    /// <summary>True when this is the selected playlist row.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnChanged(); OnChanged(nameof(RowBackground)); }
    }

    /// <summary>
    /// The fill behind this row. The template draws it, so it covers the full row height at any window
    /// size.
    /// </summary>
    /// <remarks>
    /// The colours follow the data grid's own order of precedence. A selected row gets the cyan
    /// background (<c>TableViewRowBackgroundSelected</c>). A playing row gets a fainter cyan. Every
    /// other row gets the band that the painter draws (<c>GridRowBandBrush</c>). The queue then looks
    /// like one column of the same table the library uses.
    ///
    /// <para>The band depends on the row position, that is the index the queue renumbers, not on the
    /// track. During a reorder, faces are handed from row to row. A band carried by the track would
    /// move with it instead of staying at its position.</para>
    /// </remarks>
    public Brush RowBackground => new SolidColorBrush(
        _isSelected ? Color.FromArgb(0x24, 0x04, 0xC8, 0xFF)
        : _isCurrent ? Color.FromArgb(0x14, 0x04, 0xC8, 0xFF)
        : _index % 2 == 0 ? Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)
        : Microsoft.UI.Colors.Transparent);

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
