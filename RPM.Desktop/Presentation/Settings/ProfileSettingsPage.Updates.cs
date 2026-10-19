using Microsoft.UI.Xaml;

using RPM.Desktop.Services.Updates;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>
/// The updates card: what the app is doing about a new version, and a way to ask it to look now.
/// </summary>
/// <remarks>The app checks for updates on its own a little after each launch, so this card is a window
/// onto that check. It is not the only way updating happens. It is for a reader who has heard that a
/// new version exists and does not want to wait for the next launch to get it.
/// </remarks>
public sealed partial class ProfileSettingsPage
{
    private AppUpdates Updates => _updates ??= App.Services.GetRequiredService<AppUpdates>();

    private AppUpdates? _updates;

    /// <summary>Draws the card as the service stands now, and follows it while the page is open. The
    /// service changes on whichever thread its work finished on. So every reading of it is sent back to
    /// the dispatcher before it is drawn.</summary>
    private void HookUpdatesCard()
    {
        Updates.Changed += OnUpdatesChanged;
        ShowUpdates();
        CrashReportsToggle.IsOn = _settings.CrashConsent == RPM.Core.Crashes.CrashConsent.Send;
    }

    private void UnhookUpdatesCard() => Updates.Changed -= OnUpdatesChanged;

    private void OnUpdatesChanged() => DispatcherQueue.TryEnqueue(ShowUpdates);

    private void ShowUpdates()
    {
        // A copy that cannot update itself says so, and the button stays disabled. A button that does
        // nothing is worse than no button.
        CheckUpdatesButton.IsEnabled = Updates.Managed && Updates.Now is not
            (AppUpdates.State.Checking or AppUpdates.State.Fetching or AppUpdates.State.Ready);

        RestartForUpdateButton.Visibility =
            Updates.Now == AppUpdates.State.Ready ? Visibility.Visible : Visibility.Collapsed;

        UpdateStatusText.Text = Updates.Now switch
        {
            AppUpdates.State.Unmanaged => Loc.Get("Update_Unmanaged"),
            AppUpdates.State.Checking => Loc.Get("Update_Checking"),
            AppUpdates.State.Current => Loc.Get("Update_Current"),
            AppUpdates.State.Fetching => Loc.Get("Update_Fetching"),
            AppUpdates.State.Ready when Updates.Staged is { } version =>
                Loc.Format("Update_Ready", $"{version.Major}.{version.Minor}.{version.Build}"),
            AppUpdates.State.Failed => Loc.Get("Update_Failed"),
            _ => "",
        };
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        // The status line is the whole report. The states are drawn as they happen. A version that
        // turns up is offered by the window, the same way the launch check offers it.
        if (await Updates.FetchAsync() && Updates.MayAsk)
            await OfferStagedUpdateAsync();
    }

    /// <summary>The same question the shell puts at launch, for a check that the reader asked for. The
    /// question is shown in the shell's window. This page is inside the settings dialog. Both need the
    /// same tree of windows to draw in.</summary>
    private async Task OfferStagedUpdateAsync()
    {
        if (Updates.Staged is not { } version)
            return;

        Updates.AskedNow();

        var answer = await Dialogs.PromptDialog.ShowAsync(
            XamlRoot,
            Loc.Get("Update_Title"),
            Loc.Format("Update_Body", $"{version.Major}.{version.Minor}.{version.Build}"),
            primaryText: Loc.Get("Update_Restart"),
            closeText: Loc.Get("Update_Later"),
            defaultButton: Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary);

        if (answer == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            RestartForUpdate();
        else
            Updates.Defer();
    }

    private void OnRestartForUpdate(object sender, RoutedEventArgs e) => RestartForUpdate();

    /// <summary>Hands the update over and closes the window. Closing the window runs the app's own
    /// shutdown. The updater is already waiting for this process to end. See <see
    /// cref="AppUpdates.Apply"/>.</summary>
    private void RestartForUpdate()
    {
        if (Updates.Apply(restart: true))
            App.AppWindow?.Close();
    }

    /// <summary>Drawing the switch raises this too. <c>FromSwitch</c> returns null in that case, so
    /// drawing the switch for a value that is not yet answered records no answer.</summary>
    private void OnCrashReportsToggled(object sender, RoutedEventArgs e)
    {
        if (RPM.Core.Crashes.CrashConsentRule.FromSwitch(CrashReportsToggle.IsOn, _settings.CrashConsent) is { } changed)
            _settings.CrashConsent = changed;
    }
}
