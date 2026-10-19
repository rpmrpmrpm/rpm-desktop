using RPM.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Setup;

/// <summary>
/// The screen for a collection that is configured and will not open.
///
/// <para><b>It takes the failure, not a message.</b> The navigation parameter is a
/// <see cref="LibraryProblem"/>. It holds a <see cref="LibraryOpening.Trouble"/> and the place the screen
/// is shown in. So this page cannot be shown without saying which failure it is about, and it cannot be
/// shown for a first run. What each failure says is decided by <see cref="Show"/>, in one switch over the
/// cases.</para>
///
/// <para>Try again opens the same folder through <see cref="Landing"/>, which is also what the shell lands
/// with. So a folder that has come back opens in the same way as at launch. What happens next depends on
/// <see cref="ProblemPlace"/>. At the landing, the screen it opens replaces this one. Choose another folder
/// goes to the wizard's folder step, which holds the rules for which folders may be adopted. Over the
/// library, the shell hides this screen once the collection has no trouble. Choose another folder runs
/// the Settings folder change, which applies the same rules (<see cref="LibraryFolderChange"/>). Going to
/// the wizard instead would tear down a library page that is never built twice in one run.</para>
/// </summary>
public sealed partial class LibraryProblemPage : Page
{
    public LibraryProblemPage() => InitializeComponent();

    /// <summary>What this screen is about. It is kept so Try again can tell the same problem from a new
    /// one.</summary>
    private LibraryOpening.Trouble? _trouble;

    /// <summary>Where this screen is up. Decides what both buttons do.</summary>
    private ProblemPlace _place;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Nothing else can be shown here. A navigation without a trouble is a bug in the caller. Landing on
        // the setup wizard instead is what this page exists to prevent.
        if (e.Parameter is not LibraryProblem problem)
            throw new ArgumentException(
                $"{nameof(LibraryProblemPage)} needs a {nameof(LibraryProblem)}.", nameof(e));

        _place = problem.Place;
        Show(problem.Trouble);
    }

    /// <summary>
    /// Fills the screen for one failure. Each case sets every piece of text, so no case relies on a default
    /// left by another.
    /// </summary>
    private void Show(LibraryOpening.Trouble trouble)
    {
        _trouble = trouble;
        FolderText.Text = trouble.Folder;
        StatusText.Visibility = Visibility.Collapsed;

        switch (trouble)
        {
            case LibraryOpening.Trouble.FolderMissing:
                TitleText.Text = Loc.Get("LibProblem_MissingTitle");
                BodyText.Text = Loc.Get("LibProblem_MissingBody");
                Step1Text.Text = Loc.Get("LibProblem_MissingStep1");
                Step2Text.Text = Loc.Get("LibProblem_MissingStep2");
                Step3Text.Text = Loc.Get("LibProblem_MissingStep3");
                VersionsText.Visibility = Visibility.Collapsed;
                ShowDetails(null);
                break;

            case LibraryOpening.Trouble.TooNew tooNew:
                TitleText.Text = Loc.Get("LibProblem_NewerTitle");
                BodyText.Text = Loc.Get("LibProblem_NewerBody");
                Step1Text.Text = Loc.Get("LibProblem_NewerStep1");
                Step2Text.Text = Loc.Get("LibProblem_NewerStep2");
                Step3Text.Text = Loc.Get("LibProblem_NewerStep3");
                VersionsText.Text = Loc.Format("LibProblem_NewerVersions", tooNew.Found, tooNew.Supported);
                VersionsText.Visibility = Visibility.Visible;
                ShowDetails(null);
                break;

            case LibraryOpening.Trouble.Unreadable unreadable:
                TitleText.Text = Loc.Get("LibProblem_UnreadableTitle");
                BodyText.Text = Loc.Get("LibProblem_UnreadableBody");
                Step1Text.Text = Loc.Get("LibProblem_UnreadableStep1");
                Step2Text.Text = Loc.Get("LibProblem_UnreadableStep2");
                Step3Text.Text = Loc.Get("LibProblem_UnreadableStep3");
                VersionsText.Visibility = Visibility.Collapsed;
                ShowDetails(unreadable.Detail);
                break;

            // The same three things to try as for a file that would not open: another copy of rpm
            // holding it, a disk that went away, or starting over. Only the heading and the line under
            // it differ, because the collection did open.
            case LibraryOpening.Trouble.ReadFailed readFailed:
                TitleText.Text = Loc.Get("LibProblem_ReadFailedTitle");
                BodyText.Text = Loc.Get("LibProblem_ReadFailedBody");
                Step1Text.Text = Loc.Get("LibProblem_UnreadableStep1");
                Step2Text.Text = Loc.Get("LibProblem_UnreadableStep2");
                Step3Text.Text = Loc.Get("LibProblem_UnreadableStep3");
                VersionsText.Visibility = Visibility.Collapsed;
                ShowDetails(readFailed.Detail);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(trouble), trouble, "Unknown trouble.");
        }
    }

    /// <summary>The exception text, or nothing. It is collapsed again each time, so a reopened Details line
    /// never shows the last failure's text.</summary>
    private void ShowDetails(string? detail)
    {
        DetailsText.Text = detail ?? string.Empty;
        DetailsText.Visibility = Visibility.Collapsed;
        DetailsBlock.Visibility = string.IsNullOrWhiteSpace(detail)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Details_Click(object sender, RoutedEventArgs e)
        => DetailsText.Visibility = DetailsText.Visibility is Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>Opens the same folder again and lands wherever that now points. If a disk has been
    /// reconnected, that is the collection.
    ///
    /// <para>This runs on the interface thread, like the launch open it repeats. On another thread the
    /// <c>Opened</c> event would reach the shell and the panes on a worker.</para>
    /// </summary>
    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        try
        {
            var opening = Landing.Attempt();

            if (opening is LibraryOpening.Trouble again)
            {
                // The same problem again. The screen would look identical and the button would seem to do
                // nothing. A different problem replaces the whole screen.
                if (again == _trouble)
                {
                    StatusText.Text = Loc.Get("LibProblem_Unchanged");
                    StatusText.Visibility = Visibility.Visible;
                    return;
                }

                Show(again);
                return;
            }

            switch (_place)
            {
                case ProblemPlace.Landing:
                    Room.Drift = false;
                    Landing.Show(opening);
                    break;

                // The open raised Opened. The shell takes this screen down when it handles that and
                // finds no trouble on the new mirror. The library page under it re-reads itself from
                // the same event, as it does after the Settings folder change. The room stops its clock
                // when it is unloaded.
                case ProblemPlace.OverLibrary:
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(_place), _place, "Unknown place.");
            }
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Another folder. At the landing, this opens the wizard's folder step. That step owns the rules for
    /// which folders may be adopted, and it saves the folder picked there. Over the library, this runs the
    /// Settings folder change. That applies the same rules and saves the folder itself.
    /// </summary>
    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        switch (_place)
        {
            case ProblemPlace.Landing:
                Room.Drift = false;
                App.Services.GetRequiredService<NavigationService>()
                    .NavigateRoot(typeof(WizardPage), OpeningRoute.PickFolder);
                break;

            case ProblemPlace.OverLibrary:
                ChooseButton.IsEnabled = false;
                try
                {
                    var change = await LibraryFolderChange.PickAndOpenAsync(
                        App.Services.GetRequiredService<FolderPickerService>(),
                        App.Services.GetRequiredService<LibraryService>(),
                        App.Services.GetRequiredService<SettingsService>());

                    // Opened: the shell takes this screen down, as after Try again.
                    if (change.StatusKey() is { } status)
                    {
                        StatusText.Text = Loc.Get(status);
                        StatusText.Visibility = Visibility.Visible;
                    }
                }
                finally
                {
                    ChooseButton.IsEnabled = true;
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(_place), _place, "Unknown place.");
        }
    }
}
