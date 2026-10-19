using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// Chooses the layout for each line of a record's tracklist: a track, the divider that opens a disc,
/// or the header that opens a work.
///
/// <para>Each kind is its own template. A collapsed element is not free: it is built, measured and held
/// for as long as the line exists. Most lines are plain tracks, so one template with a hidden disc
/// divider and work header would carry two elements that no plain track ever shows. A record has a
/// couple of hundred elements before choosing, and fewer after it. A screen of the album list shows
/// about six records.</para>
/// </summary>
public sealed partial class AlbumLineTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Track { get; set; }

    public DataTemplate? DiscHeader { get; set; }

    public DataTemplate? WorkHeader { get; set; }

    /// <summary>
    /// Called once for each line element that the repeater builds or takes back from its pool. The
    /// count of these calls shows whether the tracklist is virtualizing. A record of 120 songs should
    /// build only the lines on screen. If all 120 are built, the list is not virtualizing.
    /// </summary>
    protected override DataTemplate? SelectTemplateCore(object item)
    {
        ListDiag.LineRealized();
        return Chosen(item);
    }

    private DataTemplate? Chosen(object item) => KindOf(item) switch
    {
        AlbumLineKind.DiscHeader => DiscHeader,
        AlbumLineKind.WorkHeader => WorkHeader,
        _ => Track,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);

    /// <summary>The choice without the counter. Used by the tests and by the overload above, when only
    /// the template is needed.</summary>
    internal DataTemplate? TemplateFor(object item) => Chosen(item);

    /// <summary>The layout kind of a line, without the templates. A DataTemplate needs a dispatcher to
    /// create, so this part can be tested outside a running window. An item that is not a tracklist
    /// line gets the track kind, which is the only one of the three that draws something in every
    /// case.</summary>
    internal static AlbumLineKind KindOf(object? item) => item switch
    {
        AlbumTrackItem { IsDiscHeader: true } => AlbumLineKind.DiscHeader,
        AlbumTrackItem { IsWorkHeader: true } => AlbumLineKind.WorkHeader,
        _ => AlbumLineKind.Track,
    };
}

/// <summary>Which of the three layouts a line of a record's tracklist is drawn with.</summary>
internal enum AlbumLineKind
{
    Track,
    DiscHeader,
    WorkHeader,
}
