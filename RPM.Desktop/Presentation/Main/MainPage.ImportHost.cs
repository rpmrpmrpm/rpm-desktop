using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Dialogs.Models;
using RPM.Desktop.Presentation.Import;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.Main.Models;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// The window's side of the seam with the import workbench.
///
/// <para>These are explicit interface implementations, so the page's own members stay private. Each
/// one only answers a call from the workbench. Each one passes the call on to the page member that
/// does the work, with no logic of its own.</para>
/// </summary>
public sealed partial class MainPage
{
    MainModel? IImportWorkbenchHost.Model => Model;

    XamlRoot? IImportWorkbenchHost.Root => XamlRoot;

    Task<bool> IImportWorkbenchHost.EnsureSignedInAsync() => EnsureSignedInAsync();

    Task<MassEditLookup?> IImportWorkbenchHost.LookUpReleaseForRowsAsync(
        IReadOnlyList<TrackRow> rows, ArtAsk ask, bool wantDiscArt, TrackMetadataStep step)
        => LookUpReleaseForRowsAsync(rows, ask, step, wantDiscArt);

    Task<TrackFill?> IImportWorkbenchHost.NameTracksFromReleaseAsync(
        IReadOnlyList<TrackRow> rows, ReleaseRef? settled, AlbumMeta? known, TrackMetadataStep step)
        => NameTracksFromReleaseAsync(rows, settled, known, step);

    void IImportWorkbenchHost.RequestAddMusic() => AddMusic_Click(this, new RoutedEventArgs());
}
