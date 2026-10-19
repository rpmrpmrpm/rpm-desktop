using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The colour that the left pane wears where it meets whatever stands beside it. This lets the two be lit
/// as one surface across the seam between them.
///
/// <para>The album page takes its own ground from the record's sleeve. The track info pane takes its ground
/// from the artist's photograph. Side by side, those are two coloured cards with a gutter down the middle.
/// Carrying the pane's colour into the album page's facing corner joins them. The bottom left of the record
/// and the bottom right of the pane are then the same colour. The reader sees one lit room, not two lit
/// boxes.</para>
///
/// <para><b>Three facts, one answer, and the answer is a question.</b> A colour is only worth anything while
/// the pane is on screen. "On screen" is not the pane's own Visibility. The whole left column can be folded
/// away while the track info view is still marked visible inside it. The colour is also only worth anything
/// to a neighbour that shows the SAME RECORD. The join says "this album is the one playing". Carried onto any
/// other album, it would say something that is not true.</para>
///
/// <para>The three facts are stated separately by the places that know them. The pane paints its colour,
/// and it knows whose record it is. The shell decides which view is up. They are combined here, in <see
/// cref="For"/>. Every neighbour has to ask with its own record in hand. The colour cannot be read without
/// saying what it will be painted onto. That stops the join outliving the thing it is about.</para>
/// </summary>
internal static class PaneCorner
{
    private static Color? _wearing;
    private static string _folder = "";
    private static bool _showing;

    /// <summary>
    /// The colour that a page showing <paramref name="folder"/> should paint in its facing corner, or null.
    ///
    /// <para>Null covers every case where there is no join at all. These are: no pane on screen; a pane
    /// standing on plain glass with no colour to lend; a record playing from outside the collection; and the
    /// case this exists for, a page showing a different album from the one the pane is about.</para>
    /// </summary>
    public static Color? For(string folder)
        => _showing
        && _folder.Length > 0
        && string.Equals(folder, _folder, StringComparison.Ordinal)
            ? _wearing
            : null;

    /// <summary>Raised when any of the three has changed. Neighbours ask <see cref="For"/> again with their
    /// own record. What changed here may or may not concern them.</summary>
    public static event Action? Changed;

    /// <summary>The pane's own colour at the corner, and whose record it is. Set whenever either changes. A
    /// null colour means the pane is standing on glass. An empty folder means a record that the collection
    /// does not know about. No page can be showing that.</summary>
    public static void Wearing(Color? colour, string folder) => Set(colour, folder, _showing);

    /// <summary>Whether the pane is on screen at all. This is the shell's answer, not the view's.</summary>
    public static void Showing(bool showing) => Set(_wearing, _folder, showing);

    private static void Set(Color? wearing, string folder, bool showing)
    {
        if (_wearing == wearing && _folder == folder && _showing == showing)
            return;

        _wearing = wearing;
        _folder = folder;
        _showing = showing;
        Changed?.Invoke();
    }
}
