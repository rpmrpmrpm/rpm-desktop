using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Dialogs.Models;

/// <summary>One tile in the cover picker's two grids: a record on the records tab, or a generated
/// shape on the generated tab.
///
/// <para>Both tabs use the same row, because they make the same kind of choice with a different mark.
/// Each tile has a square, a thumbnail that fills in after the tile appears, and a mark that shows the
/// tile is part of the answer. On the records tab the mark is a number. Up to four records can be
/// picked, and the order they are picked in sets the layout. On the generated tab the mark is a ring,
/// because exactly one of the twelve shapes is the answer. Keeping one class stops the two tile types
/// from drifting apart.</para>
///
/// <para>Which half of the row is used depends on the tab that built it. A records row uses <see
/// cref="Folder"/>, <see cref="Album"/> and <see cref="Artist"/>, and it is numbered by <see
/// cref="Number"/>. A generated row uses <see cref="Style"/> and <see cref="Seed"/>, which hold
/// everything needed to draw it, and it is marked by <see cref="Selected"/>.</para>
///
/// <para>The thumbnail arrives after the tile does, on either tab. A folder's cover is read from disk,
/// and a generated tile is a Skia composite. Neither should run on the thread that draws the grid. See
/// <see cref="ThumbVisibility"/>, <see cref="Pending"/> and <see cref="UnavailableVisibility"/>.
/// <c>CoverResultRow</c> uses the same three for the same reason.</para>
/// </summary>
// Not required, although Folder, Album and Artist are always set together on a records row. Uno's
// bindable-metadata generator creates every [Bindable] type with a parameterless constructor, and a
// required member does not compile through that. See CoverResultRow for the same rule.
[RpmBindable]
public sealed partial class CoverChoiceRow : INotifyPropertyChanged
{
    private ImageSource? _thumb;
    private int? _number;
    private bool _selected;
    private bool _unavailable;
    private double _edge = 132;

    /// <summary>The album folder this row stands for, as the tracks table spells it. Empty on a
    /// generated row.</summary>
    public string Folder { get; init; } = "";

    public string Album { get; init; } = "";

    public string Artist { get; init; } = "";

    /// <summary>Which shape family this row draws, on the generated tab. Meaningless on a records
    /// row.</summary>
    public int Style { get; }

    /// <summary>The roll that arranges it, on the generated tab. Meaningless on a records
    /// row.</summary>
    public uint Seed { get; }

    /// <summary>Whether this tile is drawn with the advisory mark. The value is read from the row, not
    /// from the dialog when the tile is drawn. A tile drawn while the toggle changes could otherwise
    /// take a mark that belongs to the previous page.</summary>
    public CoverMark Mark { get; init; } = CoverMark.None;

    /// <summary>The constructor for a records row. It is built with an object initializer, so <see
    /// cref="Style"/> and <see cref="Seed"/> keep their defaults. Nothing reads them for this kind of
    /// row.</summary>
    public CoverChoiceRow()
    {
    }

    /// <summary>The constructor for a generated tile. The two numbers hold everything needed to draw
    /// it.</summary>
    public CoverChoiceRow(int style, uint seed)
    {
        Style = style;
        Seed = seed;
    }

    /// <summary>The size the tile is drawn at on the generated tab. It is one number for both sides,
    /// because the covers are square.
    ///
    /// <para>The size is set on the row, not in the markup, because it is not a fixed value. The
    /// generated grid shares its width out among whole columns, and the album wall does the same with
    /// whole cards. So the size changes with the window. See <c>LayOutTiles</c> in the dialog.</para>
    ///
    /// <para>The records tab never sets or reads this value. Those tiles are a fixed 104 in the markup.
    /// If the size changed under them, the width of their captions would change too.</para>
    /// </summary>
    public double Edge
    {
        get => _edge;
        set
        {
            if (Math.Abs(_edge - value) < 0.5)
                return;
            _edge = value;
            OnChanged();
        }
    }

    /// <summary>The picture, once it has been drawn or read and decoded. Null until then.</summary>
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

    /// <summary>This row's position in the records tab's pick, counted from one. It is null while the
    /// row is not ticked. The dialog renumbers every row after each tap, and rewrites the whole set
    /// rather than only the row that changed.</summary>
    public int? Number
    {
        get => _number;
        set
        {
            _number = value;
            OnChanged();
            OnChanged(nameof(BadgeText));
            OnChanged(nameof(BadgeVisibility));
        }
    }

    /// <summary>Whether this is the one figure the generated tab currently has chosen.</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            OnChanged();
            OnChanged(nameof(SelectionVisibility));
        }
    }

    /// <summary>Set on a records row whose folder returned no artwork at all. This is a disk read that
    /// came back empty, not one still in progress. A tile that will never fill in stops spinning, and
    /// it cannot be ticked. A tap that ticked it would select a folder that Save has no cover to
    /// compose with. See the dialog's own tap handler.</summary>
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

    public string BadgeText => _number is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    public Visibility BadgeVisibility => _number is > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectionVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThumbVisibility => _thumb is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnavailableVisibility => _unavailable ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Still waiting for its picture: none yet, and not known to be unavailable. The tile's
    /// spinner is bound to this value.</summary>
    public bool Pending => _thumb is null && !_unavailable;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
