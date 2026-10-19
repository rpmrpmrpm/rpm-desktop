using RPM.Core.Diagnostics;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services;
using RPM.Fingerprint.Catalog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> The <c>--gallery</c> control panel: a list of every screen in the app that <b>drives the
/// main window</b>. Page buttons navigate the main window's frame. Dialog buttons open over the main
/// window, each seeded from the demo library. The panel hosts nothing itself. The main window is where
/// things show. This is DEBUG-only development chrome. </summary>
public sealed partial class GalleryPage : Page
{
    public GalleryPage()
    {
        InitializeComponent();
    }

    private static IServiceProvider Services => App.Services;

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>The main window's XamlRoot. Dialogs open over the app, not over this panel.</summary>
    private static XamlRoot? MainXamlRoot => (App.AppWindow?.Content as FrameworkElement)?.XamlRoot;

    // ── pages: navigate the MAIN window ──

    private void Page_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        Type? page = tag switch
        {
            "MainPage" => typeof(MainPage),
            "WizardPage" => typeof(RPM.Desktop.Presentation.Setup.WizardPage),
            _ => null,
        };
        if (page is null)
            return;
        Services.GetRequiredService<NavigationService>().Navigate(page);
        SetStatus($"Main window → {tag}.");
    }

    /// <summary> One record, opened on its own page, with everything a record can carry: a sleeve, a
    /// pressing, stars given and stars averaged, songs marked, half of it in the cloud, a wall of
    /// reviews and comments under it, and a row of suggestions along the foot.
    ///
    /// <para>The left and middle come from the seeded demo library, which the harness dresses at launch
    /// (see <see cref="GalleryBootstrap"/>). The right is the stand-in wall from <see
    /// cref="GalleryCommunity"/>. Nothing is fetched.</para> </summary>
    private void AlbumPage_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryMode.LibraryPath is not { } path)
        {
            SetStatus("The demo library is only seeded on a --gallery launch.");
            return;
        }

        Services.GetRequiredService<LibraryService>().Open(path);
        Services.GetRequiredService<NavigationService>()
            .Navigate(typeof(MainPage), new GalleryAlbumRequest(DemoAlbumFolder));
        SetStatus($"Main window → {DemoAlbumFolder}. Rate it, vote, comment: the harness keeps it.");
    }

    /// <summary>The record the album page opens on: nine tracks, marked a favourite, five stars given,
    /// and a wall under it.</summary>
    private const string DemoAlbumFolder = "Daft Punk/Discovery";

    /// <summary> The ways a configured collection cannot be used, each on its own screen. These are
    /// stand-ins, not real faults. Reaching them for real needs a disk pulled out, a database from a
    /// build that does not exist yet, or a corrupt file. What is worth seeing here is the screen that
    /// the app shows for each one.
    ///
    /// <para>The page receives the failure itself (<see cref="LibraryOpening.Trouble"/>). So these are
    /// the same objects that a real launch would hand it, not a gallery-only shape. The read that fails
    /// after the open is shown where the app shows it, over the library. Each other failure replaces
    /// it.</para> </summary>
    private void Problem_Click(object sender, RoutedEventArgs e)
    {
        const string folder = "/Volumes/Music/rpm";
        const string malformed = "SQLite Error 11: 'database disk image is malformed'.";
        LibraryOpening.Trouble trouble = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "TooNew" => new LibraryOpening.Trouble.TooNew(folder, 71, 67),
            "Unreadable" => new LibraryOpening.Trouble.Unreadable(folder, malformed),
            "ReadFailed" => new LibraryOpening.Trouble.ReadFailed(folder, malformed),
            _ => new LibraryOpening.Trouble.FolderMissing(folder),
        };

        if (trouble is LibraryOpening.Trouble.ReadFailed)
        {
            ShellPage.Current?.PutUpLibraryTrouble(trouble);
            SetStatus("Main window → the collection stopped answering, over the library.");
            return;
        }

        Services.GetRequiredService<NavigationService>()
            .Navigate(typeof(RPM.Desktop.Presentation.Setup.LibraryProblemPage),
                new LibraryProblem(trouble, ProblemPlace.Landing));
        SetStatus($"Main window → the collection will not open ({trouble.GetType().Name}).");
    }

    // ── library: swap the main window's open library ──

    private void SmallLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryMode.LibraryPath is not { } path)
            return;
        Services.GetRequiredService<LibraryService>().Open(path);
        Services.GetRequiredService<NavigationService>().Navigate(typeof(MainPage));
        SetStatus("Main window → demo library.");
    }

    private async void BigLibrary_Click(object sender, RoutedEventArgs e)
    {
        SetStatus($"Preparing {GalleryLargeLibrary.TrackCount:N0}-track library (first run generates it)…");
        try
        {
            var progress = new Progress<string>(SetStatus);
            var root = await Task.Run(() => GalleryLargeLibrary.Ensure(progress));
            Services.GetRequiredService<LibraryService>().Open(root);
            Services.GetRequiredService<NavigationService>().Navigate(typeof(MainPage));
            SetStatus($"Main window → library ({GalleryLargeLibrary.TrackCount:N0} tracks).");
        }
        catch (Exception ex)
        {
            SetStatus($"Big library failed: {ex.Message}");
        }
    }

    private async void WallLibrary_Click(object sender, RoutedEventArgs e)
    {
        SetStatus($"Preparing {GalleryWallLibrary.AlbumCount:N0}-album wall (first run draws the covers)…");
        try
        {
            var progress = new Progress<string>(SetStatus);
            var root = await Task.Run(() => GalleryWallLibrary.Ensure(progress));
            Services.GetRequiredService<LibraryService>().Open(root);
            Services.GetRequiredService<NavigationService>().Navigate(typeof(MainPage));
            SetStatus($"Main window → album wall ({GalleryWallLibrary.AlbumCount:N0} albums).");
        }
        catch (Exception ex)
        {
            SetStatus($"Wall library failed: {ex.Message}");
        }
    }

    private bool _rpampSeeded;

    private void RPaMp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_rpampSeeded)
            {
                Services.GetRequiredService<RPaMpService>().AddTracks(GalleryDemo.RPaMpTracks(), play: false);
                _rpampSeeded = true;
            }
            // Go through the view-mode service, like every other way into RPaMp. It is a view. The
            // gallery must not show it any other way, because that would show something the app cannot
            // reach.
            Services.GetRequiredService<ViewModeService>().Switch(AppView.RPaMp);
            SetStatus("RPaMp is the view now, with a demo playlist. Close it to come back.");
        }
        catch (Exception ex)
        {
            SetStatus($"RPaMp failed: {ex.Message}");
        }
    }

    /// <summary> The greeting shown once a year, on an afternoon in August. It goes through <see
    /// cref="NewYearHerald"/> rather than putting the room up directly. The RPaMp button does the same
    /// thing, through the view service. What is worth seeing here is not the greeting on its own, but
    /// everything that leads to it: the record found in the open library by the rule that decides which
    /// record it is, put on the deck, and the room opening already laid out as the greeting, with a
    /// mirror ball turning over it because the record is ABBA. Only the clock is stood in for.
    ///
    /// <para>The rehearsal itself is debug-only, but this page is not. The gallery is only reachable
    /// from a debug launch, yet this page is compiled in every build. It uses the same guard that
    /// App.xaml.cs puts around <c>--gallery</c>.</para> </summary>
    private async void NewYear_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        try
        {
            SetStatus("Rehearsing the stroke of midnight…");
            var greeted = await Services.GetRequiredService<NewYearHerald>().RehearseAsync();
            SetStatus(greeted
                ? "Happy New Year. Escape, or the record ending, puts the room away."
                : "Nothing yet — the open library has no \"Happy New Year\" by ABBA in it. The demo "
                  + "library does; open that and it fires on its own within twenty seconds.");
        }
        catch (Exception ex)
        {
            SetStatus($"New Year failed: {ex.Message}");
        }
#else
        await Task.CompletedTask;
#endif
    }

    // ── dialogs: open over the MAIN window, seeded from the demo library ──

    private async void Dialog_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        if (MainXamlRoot is not { } xamlRoot)
        {
            SetStatus("Main window not ready yet.");
            return;
        }
        try
        {
            switch (tag)
            {
                case "Login":
                    await LoginDialog.ShowAsync(Services.GetRequiredService<AuthService>(), xamlRoot);
                    break;

                // The exception HttpClient throws when the accounts host does not resolve. It is read
                // the way a real sign-in reads it. It is stood in for, not caused, because the harness
                // cannot make the machine offline for one dialog.
                case "LoginOffline":
                {
                    var login = new LoginDialog(Services.GetRequiredService<AuthService>()) { XamlRoot = xamlRoot };
                    login.Show(new SignInOutcome.Failed(Faults.Read(new System.Net.Http.HttpRequestException(
                        "No such host is known.", new System.Net.Sockets.SocketException(11001)))));
                    await DialogHost.ShowAsync(login);
                    break;
                }

                case "AddMusic":
                    await AddMusicDialog.ShowAsync(xamlRoot,
                        Services.GetRequiredService<FolderPickerService>(),
                        Services.GetRequiredService<RipperService>(),
                        StagingDir());
                    break;

                case "RipCd":
                case "RipDvd":
                    await RipDiscDialog.ShowAsync(xamlRoot,
                        tag == "RipDvd" ? DiscKind.Dvd : DiscKind.Cd,
                        Services.GetRequiredService<FolderPickerService>(),
                        Services.GetRequiredService<RipperService>(),
                        Services.GetRequiredService<CatalogService>(),
                        Services.GetRequiredService<RipJobService>(),
                        StagingDir(),
                        Services.GetRequiredService<SettingsService>().DefaultTemplate);
                    break;

                case "Recognize":
                case "RecognizeBasic":
                    // Both versions of the window, shown without an account of either kind. The
                    // standing decides everything visible here: the layer named in the footer, the mark
                    // beside it, and whether the supporter sentence is at the foot of the window. So
                    // the gallery sets the standing itself, rather than asking the server.
                    //
                    // Seed the model's queue and tick it, so the dialog shows a real "recognize N
                    // tracks" count, not nothing to do.
                    var recognizeModel = ActivatorUtilities.CreateInstance<MainModel>(Services);
                    await recognizeModel.SeedAndCheckAllAsync(GalleryDemo.QueueTracks(), "/Demo/Inbox");
                    await RecognizeDialog.ShowAsync(xamlRoot, recognizeModel,
                        SupportStanding.Fixed(tag == "RecognizeBasic"
                            ? new SupporterService.Status(false, null)
                            : new SupporterService.Status(true, DateTimeOffset.UtcNow.AddMonths(8))),
                        recognizeModel.SelectedQueueRowsSnapshot());
                    break;

                case "ReleaseLookup":
                    // No track step. The gallery has no files, and this is the window being looked at,
                    // not a record being named.
                    await ReleaseLookupDialog.ShowAsync(xamlRoot,
                        Services.GetRequiredService<CatalogService>(),
                        Presentation.Dialogs.Models.TrackMetadataStep.None,
                        detected: Array.Empty<double>(),
                        album: "Discovery", artist: "Daft Punk", media: ReleaseMedia.Vinyl);
                    break;

                case "RecordVinyl":
                case "RecordVinylSides":
#if DEBUG
                    // Both steps of the wizard, on a stand-in input (GalleryCapture), so the meters,
                    // the gain and the clip light are live on a machine with no turntable. The second
                    // entry opens straight on the recording face. Reaching it for real needs a device,
                    // a level and a Next press, and it is the face with the most on it.
                    //
                    // The stand-in and the way in past step 1 are both debug-only, but this page is
                    // not. The gallery is only reachable from a debug launch, yet this page is compiled
                    // in every build. It uses the same guard as the New Year rehearsal.
                    await RecordVinylDialog.ShowGalleryAsync(xamlRoot, StagingDir(), new GalleryCapture(),
                        openOnRecordStep: tag == "RecordVinylSides");
#else
                    await RecordVinylDialog.ShowAsync(xamlRoot, StagingDir());
#endif
                    break;

                case "TrackDetails":
                    // A fully tagged in-memory row, with every tab filled, cover and all. No file is
                    // read.
                    await TrackDetailsDialog.ShowAsync(xamlRoot, GalleryDemo.DetailRow());
                    break;

                case "SplitAlbum":
                    // A synthetic 20-minute vinyl side drives the analyser. It has real boundaries and
                    // no rip.
                    await SplitAlbumDialog.ShowAsync(xamlRoot,
                        GalleryDemo.SplitSubject(),
                        GalleryDemo.SplitAnalyzer(),
                        Services.GetRequiredService<PlaybackService>(),
                        Services.GetRequiredService<CatalogService>());
                    break;

                case "CloudFull":
                case "CloudFullAgain":
                    // A full free account, with "all my music" dropped to the selection by it. This is
                    // the state the window is actually shown in. There is no engine and no server. The
                    // window reads its two numbers and nothing else.
                    //
                    // The second entry is the same window a week later. It shows the opt-out box, which
                    // is the only difference between the two.
                    await SupporterDialog.ShowAsync(xamlRoot,
                        SupporterDialog.Occasion.StorageFull(
                            new RPM.Core.Cloud.CloudSpace(
                                UsedBytes: 100L * 1024 * 1024 * 1024,
                                QuotaBytes: 100L * 1024 * 1024 * 1024),
                            syncAllPaused: true,
                            offerNeverAgain: tag == "CloudFullAgain"));
                    break;

                default:
                    return;
            }
            SetStatus($"{tag} dialog closed.");
        }
        catch (Exception ex)
        {
            SetStatus($"{tag} failed: {ex.Message}");
        }
    }

    // ── demo seed ──

    /// <summary>The first few demo track titles, as the "missed a track" dialog would list
    /// them.</summary>

    /// <summary>A throwaway staging folder for the dialogs that rip or record into one. Nothing is
    /// written unless the user drives the flow. It only has to be a real, empty directory.</summary>
    private static string StagingDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rpm-gallery-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
