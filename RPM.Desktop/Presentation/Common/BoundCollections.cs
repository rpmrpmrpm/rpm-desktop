using System.Collections.ObjectModel;
using RPM.Desktop.Presentation.Main.LeftPane;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Presentation.Pegasus;
using RPM.Desktop.Presentation.RPaMp.Models;
using RPM.Desktop.Presentation.Startup;

namespace RPM.Desktop.Presentation.Common;

// Every collection that a XAML {Binding} passes to an ItemsSource. Each one is a partial class of its own.
//
// WHY. Under Native AOT, a binding passes the list to WinUI as an object. CsWinRT gives an object its
// WinRT list interfaces only if it generated them. For a partial class in this project it always does.
// For a generic type such as ObservableCollection<T> or List<T>, it does so only when some line of code
// converts that exact generic instantiation to a WinRT value. Without that, a bound
// ObservableCollection<AlbumCard> reached the wall with no items. A bound List<TrackInfoRow> left the
// credits heading of the track sheet with nothing under it. Neither case threw an error.
// List<AlbumTrackItem> worked through a binding only because the album page also sets it from code.
// Windows ships with Native AOT.
//
// RPM007 refuses any other type on a property a XAML ItemsSource binds to, so a new bound list gets a
// line here: an ObservableCollection subclass for a list that changes in place, a List subclass for one
// that is replaced whole.

/// <summary>The radio's station rows. See the note at the top of this file.</summary>
public sealed partial class RadioStationList : ObservableCollection<RadioStationItem>;

/// <summary>The cast menu's speakers. See the note at the top of this file.</summary>
public sealed partial class CastTargetList : ObservableCollection<CastTarget>;

/// <summary>The playlist tree's visible rows. See the note at the top of this file.</summary>
public sealed partial class PlaylistTreeList : ObservableCollection<PlaylistTreeItem>;

/// <summary>The sleeves in one row of the album wall. See the note at the top of this file.</summary>
public sealed partial class AlbumCardList : ObservableCollection<AlbumCard>;

/// <summary>The records in one row of the home view. See the note at the top of this file.</summary>
public sealed partial class SimilarRowList : ObservableCollection<SimilarRow>;

/// <summary>One column of an album's track lines, in the drawer and in the list view. See the note at
/// the top of this file.</summary>
public sealed partial class AlbumTrackItemList : List<AlbumTrackItem>;

/// <summary>One row of covers on the Pegasus wall. See the note at the top of this file.</summary>
public sealed partial class PegasusAlbumCardList : List<PegasusAlbumCard>;

/// <summary>One row of artists on Pegasus's artists screen. See the note at the top of this file.</summary>
public sealed partial class PegasusArtistCardList : List<PegasusArtistCard>;

/// <summary>The lines of one block of the track sheet. See the note at the top of this file.</summary>
public sealed partial class TrackInfoRowList : List<TrackInfoRow>;

/// <summary>The placeholder cards on an album's empty community tab. See the note at the top of this
/// file.</summary>
public sealed partial class GrooveGhostList : List<GrooveGhost>;

/// <summary>The rows of the screen shown when the install is not whole. See the note at the top of this
/// file.</summary>
public sealed partial class PreflightRowList : List<PreflightRow>;

/// <summary>Lines of text shown one under the other, such as a release match's reasons. See the note at
/// the top of this file.</summary>
public sealed partial class TextLineList : List<string>;

/// <summary>
/// The equaliser's ten bands. RPaMpView binds them by index: <c>{Binding Bands[3].Gain}</c>.
///
/// <para>Under Native AOT, an index in a binding path is a second requirement, separate from the list type.
/// WinUI asks the list for an indexer through ICustomPropertyProvider. CsWinRT implements that only for a
/// type marked GeneratedBindableCustomProperty. With a typed ObservableCollection&lt;EqBand&gt;, the
/// binding threw NotSupportedException when the player opened, and the ten sliders were not bound. The
/// indexer is declared on this type as well as inherited from Collection&lt;T&gt;. So the fix does not
/// depend on whether the generator reads the members of a base type. That has not been checked.</para>
///
/// <para>The marker is used on the WinUI head only. Uno resolves an index on any IList without metadata.
/// This is how the Skia head has always read this binding, so the marker is not needed there.</para>
/// </summary>
#if RPM_WINUI
[RpmBindable]
#endif
public sealed partial class EqBandList(IEnumerable<EqBand> bands) : ObservableCollection<EqBand>(bands)
{
    public new EqBand this[int index]
    {
        get => base[index];
        set => base[index] = value;
    }
}
