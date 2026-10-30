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
/// The setup wizard: the language and country, the account, the music folder, the first music, how rpm looks, how the
/// library opens, and finish.
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

    /// <summary>Which step is up. The dots read it.</summary>
    private WizardStep _step = WizardStep.Region;

    /// <summary>What the import step ended with, held for the last step, which comes two steps later.</summary>
    private ImportOutcome _outcome = new(0, 0, Skipped: true);

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

        BuildDots();
        WizardSteps.Tagline(TaglineHead, TaglineTail);

        // A new language rebuilds the whole wizard in it, at this step, so everything read from here on
        // is in the language just picked. The welcome is not played again: the shell has revealed.
        RegionStep.LanguagePicked += (_, _) =>
            App.Services.GetRequiredService<NavigationService>().NavigateRoot(typeof(WizardPage), WizardStep.Region);
        RegionStep.Finished += (_, _) => Show(WizardStep.Account);
        AccountStep.Finished += (_, _) => Show(WizardStep.Folder);

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

            _outcome = new Steps.ImportOutcome(0, 0, Skipped: true);
            Show(WizardStep.Look);
        };

        LookStep.Finished += (_, _) => Show(WizardStep.Layout);
        LayoutStep.Finished += (_, _) =>
        {
            DoneStep.Show(_outcome);
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

        // A rebuild for a new language says its own step.
        if (e.Parameter is WizardStep again)
        {
            Show(again);
            SetupLoop.Play();
            return;
        }

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

        // A launch starts at the language and country. A wizard reached later (the problem screen's
        // Choose another folder) is past them, and goes straight to the folder.
        var launch = !Shell.ShellPage.HasRevealed && !Gallery.GalleryMode.Active;
        Show(launch ? WizardStep.Region : WizardStep.Folder);

        // The welcome belongs to a launch that lands here. A wizard reached later (the problem
        // screen's Choose another folder) is already past the opening, and the gallery has no opening.
        if (launch)
            HoldForWelcome();
        else
            SetupLoop.Play();
    }

    /// <summary>How long the greeting takes to come up, counted from the reveal.</summary>
    private static readonly TimeSpan HelloIn = TimeSpan.FromMilliseconds(600);

    /// <summary>How long the black stays before the room starts coming up out of it, counted from the
    /// reveal.</summary>
    private static readonly TimeSpan GroundAfter = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan GroundIn = TimeSpan.FromMilliseconds(1500);

    /// <summary>When the line under the greeting and the Continue button come up, counted from the
    /// reveal.</summary>
    private static readonly TimeSpan LineAfter = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan LineIn = TimeSpan.FromMilliseconds(700);

    /// <summary>How long each language's greeting stands before the next fades over it.</summary>
    private static readonly TimeSpan HelloEach = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan HelloOver = TimeSpan.FromMilliseconds(700);

    private static readonly TimeSpan IntroOut = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan StepsIn = TimeSpan.FromMilliseconds(800);

    /// <summary>The longest the welcome waits for the reveal. A startup whose reveal never comes is
    /// brought back by the splash's own backstop, and the first step must not stay hidden behind it.</summary>
    private static readonly TimeSpan RevealBackstop = TimeSpan.FromSeconds(15);

    private bool _welcomed;

    /// <summary>Set once Continue is pressed: the greetings stop turning and the welcome gives way.</summary>
    private bool _continued;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _turning;

    /// <summary>
    /// Puts the page in the welcome's opening state: black, with the room and the first step hidden, and
    /// waits for the reveal. The reveal is the moment the window is shown and the opening sound starts, so
    /// the welcome plays with the sound and not behind the splash.
    /// </summary>
    private void HoldForWelcome()
    {
        Ground.Opacity = 0;
        StepHost.Opacity = 0;
        StepHost.IsHitTestVisible = false;
        Intro.Opacity = 0;
        Intro.Visibility = Visibility.Visible;

        Shell.ShellPage.Revealed += Welcome;

        var backstop = DispatcherQueue.CreateTimer();
        backstop.Interval = RevealBackstop;
        backstop.IsRepeating = false;
        backstop.Tick += (_, _) => Welcome();
        backstop.Start();
    }

    /// <summary>
    /// The welcome: the greeting on black, the room rising under it after a second, the line and the
    /// Continue button after two, and the greeting turning through the app's languages every two seconds
    /// until Continue is pressed. Runs once, from whichever of the reveal and its backstop comes first.
    /// </summary>
    private async void Welcome()
    {
        if (_welcomed)
            return;
        _welcomed = true;
        Shell.ShellPage.Revealed -= Welcome;
        RPM.Core.Diagnostics.Scope.Said("boot", "wizard welcome");

        // The language on screen first, then the others in the table's order.
        var speaking = AppLanguage.Current(App.Services.GetRequiredService<SettingsService>());
        var greetings = AppLanguage.Offered
            .OrderBy(tag => tag == speaking ? 0 : 1)
            .Select(tag => Loc.GetIn("Wizard_Hello", tag))
            .ToList();

        HelloA.Text = greetings[0];
        new Dissolve(Intro, "wizard.hello").To(1, HelloIn);

        await Task.Delay(GroundAfter);
        if (_continued)
            return;
        new Dissolve(Ground, "wizard.ground").To(1, GroundIn);

        await Task.Delay(LineAfter - GroundAfter);
        if (_continued)
            return;
        new Dissolve(HelloLine, "wizard.hello").To(1, LineIn);
        new Dissolve(ContinueButton, "wizard.hello").To(1, LineIn);
        ContinueButton.IsEnabled = true;

        // Two greetings, one fading over the other, so a change of language is a dissolve and never a
        // blank between two words.
        var next = 1;
        var front = HelloA;
        var back = HelloB;
        var frontFade = new Dissolve(HelloA, "wizard.hello");
        var backFade = new Dissolve(HelloB, "wizard.hello");

        _turning = DispatcherQueue.CreateTimer();
        _turning.Interval = HelloEach;
        _turning.Tick += (_, _) =>
        {
            if (_continued)
                return;
            back.Text = greetings[next];
            next = (next + 1) % greetings.Count;
            backFade.To(1, HelloOver);
            frontFade.To(0, HelloOver);
            (front, back) = (back, front);
            (frontFade, backFade) = (backFade, frontFade);
        };
        _turning.Start();
    }

    /// <summary>The welcome gives way to the first step, and the setup's music starts.</summary>
    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_continued)
            return;
        _continued = true;
        _turning?.Stop();
        ContinueButton.IsEnabled = false;

        try
        {
            await Fade(new Dissolve(Intro, "wizard.hello"), 0, IntroOut);
        }
        finally
        {
            // Whatever happened above, the first step ends up usable.
            Intro.Visibility = Visibility.Collapsed;
            new Dissolve(Ground, "wizard.ground").Set(1);
            StepHost.IsHitTestVisible = true;
            new Dissolve(StepHost, "wizard.steps").To(1, StepsIn);
            SetupLoop.Play();
        }
    }

    private static Task Fade(Dissolve dissolve, double to, TimeSpan over)
    {
        var landed = new TaskCompletionSource();
        dissolve.To(to, over, () => landed.TrySetResult());
        return landed.Task;
    }

    private void Folder_Chosen(object? sender, string path)
    {
        _libraryPath = path;
        ImportStep.Begin(path);
        Show(WizardStep.Import);
    }

    private void Import_Finished(object? sender, ImportOutcome outcome)
    {
        _outcome = outcome;

        // Back from the last step for an import: the look and the layout were chosen on the way there.
        if (_asked)
        {
            DoneStep.Show(outcome);
            Show(WizardStep.Done);
            return;
        }

        Show(WizardStep.Look);
    }

    /// <summary>Whether the supporter ask has been made. It is made once.</summary>
    private bool _asked;

    /// <summary>
    /// Puts one step up and lights its dot.
    /// </summary>
    private void Show(WizardStep step)
    {
        _step = step;

        Bring(step switch
        {
            WizardStep.Region => RegionStep,
            WizardStep.Account => AccountStep,
            WizardStep.Folder => FolderStep,
            WizardStep.Import => ImportStep,
            WizardStep.Look => LookStep,
            WizardStep.Layout => LayoutStep,
            _ => DoneStep,
        });

        for (var i = 0; i < Dots.Children.Count; i++)
            ((Microsoft.UI.Xaml.Shapes.Ellipse)Dots.Children[i]).Fill = Lit(i <= (int)step);

        if (step is WizardStep.Look)
            LookStep.Arrived();
        if (step is WizardStep.Account)
            AccountStep.Arrived();

        // The arriving step reads its own state, rather than being handed it. A screen that is given its
        // contents at push time shows only what was true then.
        if (step is WizardStep.Done)
        {
            _asked = true;
            DoneStep.Arrived();
        }
    }

    /// <summary>The step on screen, or null before the first.</summary>
    private FrameworkElement? _up;

    /// <summary>Counts the changes of step, so a fade that lands after a newer change does not bring its
    /// step up over the newer one.</summary>
    private int _turn;

    private static readonly TimeSpan StepOut = TimeSpan.FromMilliseconds(170);
    private static readonly TimeSpan StepIn = TimeSpan.FromMilliseconds(280);

    /// <summary>
    /// Puts <paramref name="next"/> up: the step on screen fades out, and then this one fades in. One after
    /// the other rather than across each other, because two steps' text over each other for a moment
    /// reads as a muddle. The first step of a page just appears; the welcome fades the whole page in.
    /// </summary>
    private void Bring(FrameworkElement next)
    {
        if (_up == next)
            return;

        var leaving = _up;
        _up = next;
        var turn = ++_turn;

        foreach (var step in new FrameworkElement[] { RegionStep, AccountStep, FolderStep, ImportStep, LookStep, LayoutStep, DoneStep })
            if (step != leaving && step != next)
                step.Visibility = Visibility.Collapsed;

        if (leaving is null)
        {
            next.Opacity = 1;
            next.Visibility = Visibility.Visible;
            return;
        }

        next.Visibility = Visibility.Collapsed;
        new Dissolve(leaving, "wizard.steps").To(0, StepOut, () =>
        {
            if (leaving != _up)
            {
                leaving.Visibility = Visibility.Collapsed;
                leaving.Opacity = 1;
            }
            if (turn != _turn)
                return;

            next.Opacity = 0;
            next.Visibility = Visibility.Visible;
            new Dissolve(next, "wizard.steps").To(1, StepIn);
        });
    }

    /// <summary>One dot per step.</summary>
    private void BuildDots()
    {
        for (var i = 0; i < WizardSteps.Count; i++)
            Dots.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 9, Height = 9 });
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
        // about to want it. The setup's music fades out as the collection comes up.
        Room.Drift = false;
        SetupLoop.Stop();

        // The library's first landing after setup: the library itself for a small collection. See
        // MainPage.LandAfterSetup.
        MainPage.AfterSetup = true;

        App.Services.GetRequiredService<NavigationService>().NavigateRoot(typeof(MainPage));
    }
}

/// <summary>Which of the steps is up. The order matters, because the dots up to the current step are lit,
/// and that is a comparison. There are <see cref="WizardSteps.Count"/> of them.</summary>
public enum WizardStep
{
    Region = 0,
    Account = 1,
    Folder = 2,
    Import = 3,
    Look = 4,
    Layout = 5,
    Done = 6,
}
