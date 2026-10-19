using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

using RPM.Core.Library;
using RPM.Core.Viz;

using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main;
using RPM.Desktop.Presentation.Setup.Steps;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Presentation.Setup;

/// <summary>
/// The setup wizard: choose the music folder, bring the first music in, and finish.
///
/// <para><b>Which step it opens on is not this page's decision.</b> <see cref="OpeningScreen.Choose"/>
/// makes it, and the shell passes the answer in as the navigation parameter. So one place knows what a
/// first run is. A page that worked it out for itself would be a second opinion.</para>
/// </summary>
public sealed partial class WizardPage : Microsoft.UI.Xaml.Controls.Page
{
    /// <summary>How fast the ground drifts on this page, compared with the rate the banner cards
    /// use.</summary>
    private const double WizardPace = 0.34;

    /// <summary>Which step is up. The dots read it, and so does <see cref="Back"/>.</summary>
    private WizardStep _step = WizardStep.Folder;

    /// <summary>The folder chosen on step one, and the library opened at it. It is held here, not read back
    /// from settings by the later steps. The write to settings is what step one finishes with. A step that
    /// read it back would be checking that its own predecessor worked.</summary>
    private string? _libraryPath;

    public WizardPage()
    {
        InitializeComponent();

        // Three blues and the near-black, and nothing else. There is no record here to take a colour
        // from. The usual palette for a page with no record includes a purple third colour family. On a
        // screen that never holds a sleeve, that purple would belong to nothing. See RoomPalette.Blues.
        Room.Palette = RoomPalette.Blues;

        // A third of the rate the cards run at. CardRoomArt.Rate is tuned for a banner card, which is a
        // fifth of a screen. This room fills the window, so the same angular motion carries a lobe
        // several times as far, and the picture looks like swirling rather than drifting. Keep the pace
        // slow.
        Room.Pace = WizardPace;

        FolderStep.Chosen += Folder_Chosen;

        ImportStep.Finished += Import_Finished;
        // Told the outcome even when it is nothing. The last step uses the outcome to decide what its
        // second button says. If it arrives without being told, that button is left blank.
        //
        // The second skip goes straight to the library. The supporter ask is made only once. Without
        // this, stepping past the import, pressing Import on the last step, and stepping past it again
        // would bring the same screen back. The only way out of the wizard would then be to buy
        // something.
        ImportStep.Skipped += (_, _) =>
        {
            if (_asked)
            {
                OpenTheLibrary();
                return;
            }

            DoneStep.Show(new Steps.ImportOutcome(0, 0, Skipped: true));
            Show(WizardStep.Done);
        };

        // Nothing was imported and the user has changed their mind. Go back to step two, at its source
        // card. The queue is empty, so its review would have nothing to review.
        DoneStep.ImportRequested += (_, _) =>
        {
            ImportStep.Resume();
            Show(WizardStep.Import);
        };

        // The press needs the processor cores. The room is the only other thing on screen asking for
        // frames, so it stops drifting during the press, and picks the drift up where it left off.
        ImportStep.PressRunningChanged += (_, running) => Room.Drift = !running;

        DoneStep.Finished += (_, _) => OpenTheLibrary();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // The shell says where to start. Any other navigation (a stray one, or a gallery entry) starts
        // at the beginning. That is the only answer that is safe without a library behind it.
        var route = e.Parameter as OpeningRoute? ?? OpeningRoute.PickFolder;

        if (route == OpeningRoute.ImportFirstMusic
            && App.Services.GetRequiredService<SettingsService>().LibraryPath is { Length: > 0 } path)
        {
            _libraryPath = path;
            ImportStep.Begin(path);
            Show(WizardStep.Import);
            return;
        }

        Show(WizardStep.Folder);
    }

    private void Folder_Chosen(object? sender, string path)
    {
        _libraryPath = path;
        ImportStep.Begin(path);
        Show(WizardStep.Import);
    }

    private void Import_Finished(object? sender, ImportOutcome outcome)
    {
        DoneStep.Show(outcome);
        Show(WizardStep.Done);
    }

    /// <summary>Whether the supporter ask has been made. It is made once.</summary>
    private bool _asked;

    /// <summary>
    /// Puts one step up and lights its dot.
    /// </summary>
    private void Show(WizardStep step)
    {
        _step = step;

        FolderStep.Visibility = step is WizardStep.Folder ? Visibility.Visible : Visibility.Collapsed;
        ImportStep.Visibility = step is WizardStep.Import ? Visibility.Visible : Visibility.Collapsed;
        DoneStep.Visibility   = step is WizardStep.Done   ? Visibility.Visible : Visibility.Collapsed;

        Dot1.Fill = Lit(step >= WizardStep.Folder);
        Dot2.Fill = Lit(step >= WizardStep.Import);
        Dot3.Fill = Lit(step >= WizardStep.Done);

        // The arriving step reads its own state, rather than being handed it. A screen that is given its
        // contents at push time shows only what was true then.
        if (step is WizardStep.Done)
        {
            _asked = true;
            DoneStep.Arrived();
        }
    }

    private static Brush Lit(bool on) => on
        ? (Brush)Application.Current.Resources["RpmCyanBrush"]
        : new SolidColorBrush(Windows.UI.Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// Leaves the wizard for the collection. Every exit comes here: finishing, skipping the import, and
    /// becoming a supporter. Becoming a supporter opens a browser and then comes here anyway, so the app is
    /// not left parked on a screen that waits for a purchase it cannot see.
    /// </summary>
    private void OpenTheLibrary()
    {
        // The room stops before the page does. It is a timer on the drawing thread, and the next page is
        // about to want it.
        Room.Drift = false;

        App.Services.GetRequiredService<NavigationService>().NavigateRoot(typeof(MainPage));
    }
}

/// <summary>Which of the three steps is up. The order matters, because the dots behind the current step are
/// lit, and that is a comparison.</summary>
internal enum WizardStep
{
    Folder = 0,
    Import = 1,
    Done = 2,
}
