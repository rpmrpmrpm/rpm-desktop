using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Dialogs.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.Main.Models;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// The handful of things the import workbench needs from the window it sits in.
///
/// <para>Kept short on purpose. The workbench owns its own tree, bar, panel and actions. What it
/// cannot own is anything that opens another window on the window's behalf: signing in, the release
/// picker and the add-music popup. Those stay with the page, and this interface is the whole of the
/// seam between them. If it starts to grow, something has been put on the wrong side of it.</para>
/// </summary>
public interface IImportWorkbenchHost
{
    /// <summary>The window's model. The workbench reads the queue through it and never touches the
    /// library service directly.</summary>
    MainModel? Model { get; }

    /// <summary>The root a dialog opened from the workbench belongs to. A control's own XamlRoot is
    /// null until it is in the tree, so the page hands its own over.</summary>
    XamlRoot? Root { get; }

    /// <summary>Makes sure there is an account behind the request, asking for one if there is not.
    /// False when the user declined, which is a cancel and not an error.</summary>
    Task<bool> EnsureSignedInAsync();

    /// <summary>Opens the release picker on what these rows already say, and hands back what was
    /// chosen. Null when nothing was.</summary>
    /// <param name="step">Whether the picker may go on to name these rows from the release, and what
    /// this side does with them when it has. Required. See <see cref="TrackMetadataStep"/>.</param>
    Task<MassEditLookup?> LookUpReleaseForRowsAsync(
        IReadOnlyList<TrackRow> rows, ArtAsk ask, bool wantDiscArt, TrackMetadataStep step);

    /// <summary>Names rows from a release the user picks, or from one already settled on. Returns how
    /// many were named out of how many were asked about, because "18 of 20" shows when the user picked
    /// the wrong pressing.</summary>
    Task<TrackFill?> NameTracksFromReleaseAsync(
        IReadOnlyList<TrackRow> rows, ReleaseRef? settled, AlbumMeta? known, TrackMetadataStep step);

    /// <summary>Asks for the add-music popup. The page owns it because it goes on to open the disc and
    /// vinyl windows, and only one dialog can be up at a time.</summary>
    void RequestAddMusic();
}
