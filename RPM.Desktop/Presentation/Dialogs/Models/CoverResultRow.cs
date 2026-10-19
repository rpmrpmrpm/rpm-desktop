using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;

namespace RPM.Desktop.Presentation.Dialogs.Models;

/// <summary>One picture in the cover search's grid.
///
/// <para>The thumbnail arrives after the tile does. This is the main design of the window. The search
/// returns sixty results across seven services, and each service has its own CDN. Waiting for all of
/// them before drawing anything would leave a blank window for as long as the slowest host takes. So a
/// tile appears at once, with what the search already returned: the source, the title and the size. The
/// picture fills in when it arrives.</para>
///
/// <para>So every write to a row can happen after the user has moved on, from a task that no longer
/// matters. Nothing is written by position. A fetch holds its own row and writes only to that row. A
/// row removed from the list becomes an orphan, and its late write cannot reach anything. This is what
/// makes cancelling a search safe.</para>
/// </summary>
// Not required, although it is always set. Uno's bindable-metadata generator creates every [Bindable]
// type with a parameterless constructor, and a required member does not compile through that.
[RpmBindable]
public sealed partial class CoverResultRow : INotifyPropertyChanged
{
    private ImageSource? _thumb;
    private bool _unavailable;
    private CoverMeasurement? _measured;

    public CoverCandidate Candidate { get; init; } = new("", "", "");

    /// <summary>The picture, once it has been fetched and decoded. Null until then, and always null for
    /// a host that will not serve the picture.</summary>
    public ImageSource? Thumb
    {
        get => _thumb;
        set
        {
            _thumb = value;
            OnChanged();
            OnChanged(nameof(ThumbVisibility));
            OnChanged(nameof(Pending));
        }
    }

    /// <summary>Set when the thumbnail could not be fetched at all. This is different from "not yet". A
    /// tile that will never fill in stops showing the wait mark, and the user can see before clicking
    /// that this result is not available.</summary>
    public bool Unavailable
    {
        get => _unavailable;
        set
        {
            _unavailable = value;
            OnChanged();
            OnChanged(nameof(UnavailableVisibility));
            OnChanged(nameof(Pending));
        }
    }

    /// <summary>What the picture turned out to be, once it has been measured. Only set for a source
    /// that did not state its dimensions (see the measure pass in the search window). It never
    /// contradicts a source that did state them.</summary>
    public CoverMeasurement? Measured
    {
        get => _measured;
        set
        {
            _measured = value;
            OnChanged();
            OnChanged(nameof(Detail));
            OnChanged(nameof(DetailVisibility));
        }
    }

    public string Source => Candidate.Source;

    /// <summary>The album title, or the label text of the web result. It is empty rather than null, so
    /// a tile with no title collapses its line instead of binding to nothing.</summary>
    public string Title => Candidate.Title ?? "";

    /// <summary>The size, the weight and the qualifier on one line, for example "1425×1400 · 892 KB ·
    /// 1977, GB, Vinyl".
    ///
    /// <para>The separators match the ones the record's own cover editor uses for the same facts, so
    /// the two can be compared without translating between two styles. Any part may be missing, and its
    /// separator goes with it.</para>
    /// </summary>
    public string Detail => string.Join(" · ",
        new[] { Size, Weight, Candidate.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>What the source stated, or what measuring found. A source that states its dimensions is
    /// trusted and never measured, so the two values cannot disagree.</summary>
    private string? Size => Candidate.Dimensions
                         ?? (_measured is { } m ? $"{m.Width}×{m.Height}" : null);

    /// <summary>The size of the file, in the unit the number uses. It is only known for a measured
    /// picture, because the sources that state dimensions do not state the file size.</summary>
    private string? Weight => _measured is { Bytes: > 0 } m
        ? m.Bytes >= 1024 * 1024
            ? Strings.Loc.Format("Unit_MB", (m.Bytes / (1024.0 * 1024.0)).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture))
            : Strings.Loc.Format("Unit_KB", m.Bytes / 1024)
        : null;

    /// <summary>Whether this picture still needs measuring: nobody stated its size, and nobody has
    /// looked yet. The measure pass uses exactly this. So a source that starts stating its dimensions
    /// costs no requests on the day it does.</summary>
    public bool NeedsMeasuring => Candidate.Dimensions is null && _measured is null;

    public Visibility TitleVisibility => Title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThumbVisibility => _thumb is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnavailableVisibility => _unavailable ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Still waiting for its picture: none yet, and not known to be unavailable. The tile's
    /// spinner is bound to this value.</summary>
    public bool Pending => _thumb is null && !_unavailable;

    /// <summary>Whether there is a page to open for this result. Web results always have one, and it is
    /// the only way to check them. Most catalogue results have one too.</summary>
    public bool HasPage => !string.IsNullOrWhiteSpace(Candidate.PageUrl);

    /// <summary>Taking this sleeve would bring the matching disc with it. A small disc mark shows in
    /// the corner of the tile. It is a reason to prefer one result over another that looks just as
    /// good.</summary>
    public Visibility DiscMarkVisibility =>
        Candidate.HasDisc ? Visibility.Visible : Visibility.Collapsed;

    public static CoverResultRow From(CoverCandidate candidate) => new() { Candidate = candidate };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One source's tick box in the filter.
///
/// <para>The list comes from the sources the server says it can reach, plus the user's own fanart key.
/// It is not a fixed list in the window. A tick box for a source with no key returns nothing. A user
/// who unticks the other six to find out why would be right to think the window is broken.</para>
/// </summary>
[RpmBindable]
public sealed partial class CoverSourceToggle : INotifyPropertyChanged
{
    private bool _isOn = true;

    public string Name { get; init; } = "";

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
                return;
            _isOn = value;
            OnChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
